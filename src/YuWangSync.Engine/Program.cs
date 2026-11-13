using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using YuWangSync.Core;
using YuWangSync.Core.Storage;
using YuWangSync.Engine.Platform;

namespace YuWangSync.Engine;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.InputEncoding = new UTF8Encoding(false); Console.OutputEncoding = new UTF8Encoding(false);
        string? Option(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        if (args.Contains("--platform-selftest")) return await SelfTests.PlatformAsync(Option("--output"));
        if (args.Contains("--keychain-probe")) return await SelfTests.KeychainProbeAsync(Option("--keychain-probe"), Option("--key"));
        var demo = args.Contains("--demo") || args.Contains("--smoke-test");
        var data = Option("--data-root") ?? (demo ? Path.Combine(Path.GetTempPath(), "YuWangSync-demo-" + Guid.NewGuid().ToString("N")) : null);
        try
        {
            var files = new LocalFiles(data, data is null ? null : Path.Combine(data, "cache"));
            using var lease = new FileStream(Path.Combine(files.DataRoot, "engine.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            await using var host = new EngineHost(files, demo);
            await host.InitializeAsync();
            if (args.Contains("--smoke-test")) return await host.SmokeTestAsync(Option("--output"));
            await host.RunAsync(); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
    }
}

public sealed class EngineHost : IAsyncDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
    private readonly Workspace _workspace;
    private readonly LocalFiles _files;
    private readonly ISecretStore _secrets;
    private readonly bool _demo;
    private readonly SemaphoreSlim _output = new(1, 1), _loginGate = new(1, 1);
    private readonly CancellationTokenSource _done = new();
    private CancellationTokenSource? _transfer, _loginStop;
    private CancellationTokenSource? _treeStop, _sizeStop;
    private Task _treePreload = Task.CompletedTask;
    private ICloudClient? _login;
    private Account? _loginAccount;
    private string? _loginPassword;
    private Guid? _folder;
    private readonly object _stateLock = new(), _logLock = new();
    private readonly Timer _stateTimer;
    private bool _running, _disposed;
    private long _lastProgress;
    private int _stateScheduled;
    public EngineHost(LocalFiles files, bool demo)
    {
        _files = files; _demo = demo;
        ISecretStore secrets = demo ? new MemorySecretStore() : OperatingSystem.IsMacOS() ? new MacSecretStore() : new WindowsSecretStore(Path.Combine(files.DataRoot, "credentials"));
        _secrets = secrets;
        IFileSnapshotProvider snapshots = OperatingSystem.IsMacOS() ? new MacSnapshotProvider() : new FileSnapshotProvider();
        var cloud = demo ? new DemoCloudClient() : null;
        _workspace = new(new OperationStore(files.DatabasePath), files, secrets, snapshots, cloud is null ? null : (_, _) => cloud);
        _workspace.Log = message =>
        {
            lock (_logLock)
            {
                var path = Path.Combine(files.DataRoot, "application.log");
                if (File.Exists(path) && new FileInfo(path).Length > 4 * 1024 * 1024) File.Move(path, path + ".previous", true);
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message.Replace('\r', ' ').Replace('\n', ' ')}\n");
            }
        };
        _stateTimer = new(_ =>
        {
            Interlocked.Exchange(ref _stateScheduled, 0);
            if (_running && !_disposed) _ = SendStateSafelyAsync();
        }, null, Timeout.Infinite, Timeout.Infinite);
        _workspace.Changed += ScheduleState;
        _workspace.Progress += progress =>
        {
            if (!_running) return;
            var now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _lastProgress) < 150 && progress.Completed != progress.Total) return;
            Interlocked.Exchange(ref _lastProgress, now);
            _ = WriteAsync(new { @event = "progress", data = progress });
        };
    }
    public async Task InitializeAsync()
    {
        if (_demo)
        {
            var account = new Account(Guid.NewGuid(), "https://example.invalid", "演示账号");
            await _workspace.CompleteLoginAsync(account, "", _workspace.CreateLoginClient(account, ""));
        }
        else if ((_workspace.Store.Accounts().FirstOrDefault(a => a.Id == _workspace.Store.ActiveAccountId) ?? _workspace.Store.Accounts().FirstOrDefault()) is { } account)
            await _workspace.ActivateAsync(account);
        await LoadAccountAsync();
    }
    private async Task LoadAccountAsync()
    {
        await StopTreePreloadAsync();
        if (!_workspace.Offline)
            try { await _workspace.RefreshTreeAsync(_done.Token); } catch (IOException) { }
        if (_workspace.ActiveAccount is { } account && _workspace.ProjectedEntries(account.Id, e => e.IsDiskRoot).FirstOrDefault() is { } root)
        {
            lock (_stateLock) _folder = root.Id;
            try { await _workspace.BrowseAsync(root.Id, ct: _done.Token, probePermission: true); } catch (IOException) { }
        }
        if (!_workspace.Offline && _workspace.ActiveAccount is not null)
        {
            _treeStop = CancellationTokenSource.CreateLinkedTokenSource(_done.Token);
            var token = _treeStop.Token;
            _treePreload = Task.Run(async () =>
            {
                try { await _workspace.PreloadDirectoriesAsync(token); }
                catch (OperationCanceledException) { }
                catch (Exception error) when (error is IOException or HttpRequestException)
                { _workspace.Log?.Invoke("目录预加载未完成，可展开节点重试。"); }
            });
        }
    }
    private async Task StopTreePreloadAsync()
    {
        _treeStop?.Cancel(); await _treePreload;
        _treeStop?.Dispose(); _treeStop = null;
    }
    public object State()
    {
        Guid? folder; lock (_stateLock) folder = _folder;
        var all = _workspace.ActiveAccount is { } a ? _workspace.ProjectedEntries(a.Id, e => e.IsDirectory || e.ParentId == folder) : [];
        return new
        {
            version = YuWangCloudSync.Core.ProductVersion.Current, demo = _demo, accounts = _workspace.Store.Accounts(), activeAccount = _workspace.ActiveAccount,
            folderId = folder, entries = all,
            tree = _workspace.ActiveAccount is { } account ? _workspace.Store.Tree(account.Id) : [],
            operations = _workspace.Store.Operations(_workspace.ActiveAccount?.Id).OrderBy(o => o.IsTerminal).ThenByDescending(o => o.Created).Take(500),
            offline = _workspace.Offline, manualOffline = _workspace.ManualOffline, paused = _workspace.Paused,
            connectionStatus = _workspace.ConnectionStatus, dataDirectory = _files.DataRoot,
            settings = _workspace.Settings, networkCost = _workspace.NetworkCost
        };
    }
    // Throttle, not debounce: re-arming on every change starved state events for as long as
    // preload or size probes kept notifying, hiding finished tasks until they stopped.
    // Request replies send their own state immediately; this only paces background changes.
    private void ScheduleState()
    {
        if (!_running || _disposed || Interlocked.Exchange(ref _stateScheduled, 1) != 0) return;
        try { _stateTimer.Change(250, Timeout.Infinite); } catch (ObjectDisposedException) { }
    }
    private async Task SendStateSafelyAsync()
    {
        try { if (!_workspace.IsStopping) await WriteAsync(new { @event = "state", data = State() }); }
        catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException) { }
    }
    private async Task WriteAsync(object value)
    {
        var line = JsonSerializer.Serialize(value, Json);
        await _output.WaitAsync();
        try { await Console.Out.WriteLineAsync(line); await Console.Out.FlushAsync(); }
        finally { _output.Release(); }
    }
    public async Task RunAsync()
    {
        _running = true; _workspace.Start(); await SendStateSafelyAsync();
        var requests = new HashSet<Task>();
        try
        {
            while (!_done.IsCancellationRequested)
            {
                var line = await Console.In.ReadLineAsync(_done.Token); if (line is null) break;
                if (line.Length > 8 * 1024 * 1024) throw new IOException("IPC request exceeds limit.");
                // Start synchronously to preserve command ordering up to the first awaited operation.
                var request = HandleLineAsync(line); requests.Add(request); requests.RemoveWhere(t => t.IsCompleted);
            }
        }
        catch (OperationCanceledException) when (_done.IsCancellationRequested) { }
        finally { _done.Cancel(); _transfer?.Cancel(); _loginStop?.Cancel(); await Task.WhenAll(requests); }
    }
    private async Task HandleLineAsync(string line)
    {
        long id = 0; string? method = null;
        try
        {
            using var document = JsonDocument.Parse(line); var request = document.RootElement;
            id = request.GetProperty("id").GetInt64(); method = request.GetProperty("method").GetString();
            var parameters = request.TryGetProperty("params", out var p) ? p : JsonSerializer.SerializeToElement(new { });
            var result = await DispatchAsync(method ?? "", parameters);
            await WriteAsync(new { id, result });
        }
        catch (Exception e)
        {
            var message = e is HttpRequestException ? "服务器连接失败，本地内容已保留。" : e is OperationCanceledException ? "操作已取消。" : e.Message;
            await WriteAsync(new { id, error = new { message, type = e.GetType().Name } });
        }
        finally
        {
            if (method == "shutdown") _done.Cancel();
            else if (!_disposed) await SendStateSafelyAsync();
        }
    }
    private static Guid Id(JsonElement p, string name) => p.GetProperty(name).GetGuid();
    private static string Text(JsonElement p, string name) => p.GetProperty(name).GetString() ?? "";
    private static bool Flag(JsonElement p, string name) => p.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    private CancellationToken BeginTransfer()
    { _transfer?.Cancel(); _transfer = CancellationTokenSource.CreateLinkedTokenSource(_done.Token); return _transfer.Token; }
    public async Task<object> DispatchAsync(string method, JsonElement p)
    {
        switch (method)
        {
            case "state": return State();
            case "activate":
                await _workspace.ActivateAsync(_workspace.Store.Accounts().Single(a => a.Id == Id(p, "accountId")), _done.Token);
                lock (_stateLock) _folder = null; await LoadAccountAsync(); return State();
            case "beginLogin": return await BeginLoginAsync(p);
            case "submitLogin":
                await _loginGate.WaitAsync(_done.Token);
                try
                {
                    if (_login is null || _loginAccount is null || _loginStop is null) throw new IOException("请先获取验证码。");
                    if (!await _login.LoginAsync(Text(p, "code"), _loginStop.Token)) throw new IOException("登录未成功，请刷新验证码并检查账号密码。");
                    await _workspace.CompleteLoginAsync(_loginAccount, _loginPassword!, _login, _loginStop.Token);
                    _login = null; _loginPassword = null; await LoadAccountAsync(); return State();
                }
                finally { _loginGate.Release(); }
            case "cancelLogin": _loginStop?.Cancel(); return new { };
            // Navigation replies carry no state: the state event sent after every
            // request already does, and a second full snapshot doubles client parsing.
            case "browse":
                var folder = Id(p, "folderId"); lock (_stateLock) _folder = folder;
                // The previous folder's size probes must not hold network slots during this listing.
                StopSizeProbes();
                // Show cached children immediately; a stale-listing refresh or a
                // directory lock held by preload must not delay the switch.
                if (_workspace.Store.GetEntry(folder)?.LastListed is not null) await SendStateSafelyAsync();
                await _workspace.BrowseAsync(folder, Flag(p, "force"), _done.Token, probeSizes: false, probePermission: true);
                ProbeSizesInBackground(folder); return new { };
            case "loadFolder":
                await _workspace.BrowseAsync(Id(p, "folderId"), ct: _done.Token, probeSizes: false, probePermission: true); return new { };
            case "refresh":
                if (_workspace.Offline && !_workspace.ManualOffline) await _workspace.ReconnectAsync(_done.Token);
                Guid? current; lock (_stateLock) current = _folder;
                if (current is { } root) { await _workspace.BrowseAsync(root, true, _done.Token, probeSizes: false, probePermission: true); ProbeSizesInBackground(root); }
                else await _workspace.RefreshTreeAsync(_done.Token);
                return new { };
            case "open":
                using (var foreground = _workspace.BeginForegroundTransfer()) return new { path = await _workspace.OpenAsync(Id(p, "entryId"), BeginTransfer()) };
            case "download":
                using (var foreground = _workspace.BeginForegroundTransfer()) return new { errors = await _workspace.DownloadRecursiveAsync(p.GetProperty("ids").EnumerateArray().Select(x => x.GetGuid()).ToArray(), BeginTransfer()) };
            case "cancelTransfer": _transfer?.Cancel(); return new { };
            case "export":
                using (var foreground = _workspace.BeginForegroundTransfer())
                { await _workspace.ExportAsync(Id(p, "entryId"), Text(p, "destination"), BeginTransfer(), Flag(p, "overwrite")); return new { }; }
            case "import":
                return new { operationIds = await _workspace.ImportAsync(Id(p, "parentId"), p.GetProperty("paths").EnumerateArray().Select(x => x.GetString()!).ToArray(), BeginTransfer()) };
            case "createFolder": return new { entryId = _workspace.CreateFolder(Id(p, "parentId"), Text(p, "name")) };
            case "rename": _workspace.Rename(Id(p, "entryId"), Text(p, "name")); return new { };
            case "move":
                var moving = Id(p, "entryId");
                if (_workspace.Store.GetEntry(moving)!.IsDirectory && !_workspace.Offline) await _workspace.PrepareDirectoryAsync(moving, _done.Token);
                _workspace.Move(moving, Id(p, "parentId")); return new { };
            case "delete":
                foreach (var item in p.GetProperty("ids").EnumerateArray()) await _workspace.DeleteAsync(item.GetGuid(), _done.Token); return new { };
            case "retry":
                if (_workspace.Offline && !_workspace.ManualOffline) await _workspace.ReconnectAsync(_done.Token);
                await _workspace.RetryAsync(Id(p, "operationId"), _done.Token); return new { };
            case "cancel": _workspace.Cancel(Id(p, "operationId")); return new { };
            case "deleteTask": return new { removed = _workspace.DeleteTask(Id(p, "operationId")) };
            case "clearCompleted": return new { count = _workspace.ClearCompleted() };
            case "verifyUpload": _workspace.VerifyUploadNow(Id(p, "operationId")); return new { };
            case "setVerification":
                var settings = _workspace.Settings;
                if (p.TryGetProperty("verifyAfterUpload", out var verify)) settings = settings with { VerifyAfterUpload = verify.GetBoolean() };
                if (p.TryGetProperty("verifyOnExpensiveNetwork", out var expensive)) settings = settings with { VerifyOnExpensiveNetwork = expensive.GetBoolean() };
                _workspace.SetSettings(settings); return State();
            case "networkCost":
                if (!Enum.TryParse<NetworkCost>(Text(p, "value"), true, out var cost) || !Enum.IsDefined(cost)) throw new ArgumentException("未知的网络费用类型。");
                _workspace.SetNetworkCost(cost); return new { };
            case "resolve":
                if (!Enum.TryParse<CollisionChoice>(Text(p, "choice"), true, out var choice)) throw new ArgumentException("无效的冲突选择。");
                await _workspace.ResolveAsync(Id(p, "operationId"), choice, p.TryGetProperty("name", out var n) ? n.GetString() : null, _done.Token); return new { };
            case "linkFolder": await _workspace.LinkCreatedFolderAsync(Id(p, "operationId"), p.GetProperty("remoteId").GetUInt64(), _done.Token); return new { };
            case "exportTask":
                var op = _workspace.Store.GetOperation(Id(p, "operationId")) ?? throw new IOException("任务不存在。");
                if (op.SnapshotPath is not { } source || !_files.Owns(source) || !File.Exists(source)) throw new IOException("任务没有可导出的内容快照。");
                File.Copy(source, Text(p, "destination"), Flag(p, "overwrite")); return new { };
            case "pause": _workspace.SetPaused(Flag(p, "value")); return new { };
            case "offline": if (Flag(p, "value")) _transfer?.Cancel(); _workspace.SetManualOffline(Flag(p, "value")); if (!_workspace.ManualOffline) await _workspace.ReconnectAsync(_done.Token); return new { };
            case "removeAccount": await _workspace.RemoveAccountAsync(_workspace.Store.Accounts().Single(a => a.Id == Id(p, "accountId")), _done.Token); lock (_stateLock) _folder = null; return State();
            case "cleanup": return new { count = await _workspace.CleanupAsync(Flag(p, "allClean"), _done.Token) };
            case "shutdown": await DisposeAsync(); return new { };
            default: throw new ArgumentException("未知命令。");
        }
    }
    private void StopSizeProbes()
    {
        CancellationTokenSource? previous;
        lock (_stateLock) { previous = _sizeStop; _sizeStop = null; }
        previous?.Cancel();
    }
    // Real sizes need one direct-link probe per file. Run them after the listing is
    // returned so entering a folder is not held up; leaving the folder cancels them.
    private void ProbeSizesInBackground(Guid folder)
    {
        var next = CancellationTokenSource.CreateLinkedTokenSource(_done.Token); CancellationTokenSource? previous;
        lock (_stateLock) { previous = _sizeStop; _sizeStop = next; }
        previous?.Cancel();
        _ = Task.Run(async () =>
        {
            try { await _workspace.RefreshSizesAsync(folder, next.Token); }
            catch (OperationCanceledException) { }
            catch (Exception error) { _workspace.Log?.Invoke("文件大小暂未确认：" + error.Message); }
        });
    }
    private async Task<object> BeginLoginAsync(JsonElement p)
    {
        _loginStop?.Cancel(); await _loginGate.WaitAsync(_done.Token);
        try
        {
            _login?.Dispose(); _loginStop?.Dispose(); _loginStop = CancellationTokenSource.CreateLinkedTokenSource(_done.Token);
            if (!Uri.TryCreate(Text(p, "server").Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0)
                throw new ArgumentException("请输入有效的 HTTP 或 HTTPS 服务器地址。");
            var user = Text(p, "username").Trim(); var password = Text(p, "password");
            if (user.Length == 0) throw new ArgumentException("请输入账号。");
            var server = uri.AbsoluteUri.TrimEnd('/');
            _loginAccount = _workspace.Store.Accounts().FirstOrDefault(a => a.Server == server && a.Username == user) ?? new Account(Guid.NewGuid(), server, user);
            if (password.Length == 0) password = await _secrets.GetAsync(_loginAccount.Id + ":password", _loginStop.Token) ?? "";
            if (password.Length == 0) throw new ArgumentException("没有可用的已保存密码，请输入密码。");
            _loginPassword = password; _login = _workspace.CreateLoginClient(_loginAccount, password);
            return new { image = Convert.ToBase64String(await _login.CaptchaAsync(_loginStop.Token)) };
        }
        finally { _loginGate.Release(); }
    }
    public async Task<int> SmokeTestAsync(string? output)
    {
        var checks = new Dictionary<string, bool>(); var error = "";
        try
        {
            var account = _workspace.ActiveAccount!; var root = _workspace.ProjectedEntries(account.Id).Single(e => e.IsDiskRoot);
            var before = _workspace.Children(root.Id).Count; checks["list"] = before > 0;
            _workspace.SetManualOffline(true); var created = _workspace.CreateFolder(root.Id, "smoke");
            checks["offline-projection"] = _workspace.Children(root.Id).Count == before + 1;
            _workspace.SetManualOffline(false); await _workspace.ReconnectAsync(); await _workspace.RunOnceAsync();
            checks["durable-queue"] = _workspace.Store.GetEntry(created)!.RemoteId != 0;
            var file = _workspace.Children(root.Id).First(e => !e.IsDirectory); var path = await _workspace.OpenAsync(file.Id);
            checks["download"] = File.Exists(path);
        }
        catch (Exception e) { error = e.ToString(); }
        return await SelfTests.ReportAsync(output, checks.Count == 4 && checks.Values.All(x => x) && error.Length == 0, checks, error);
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return; _disposed = true; _running = false; _stateTimer.Dispose();
        _workspace.Changed -= ScheduleState; _transfer?.Cancel(); _loginStop?.Cancel(); _sizeStop?.Cancel(); _login?.Dispose();
        await StopTreePreloadAsync();
        await _workspace.DisposeAsync();
    }
}

internal static class SelfTests
{
    public static async Task<int> KeychainProbeAsync(string? action, string? key)
    {
        // Accept only synthetic CI keys. Never dump values or touch real account credentials.
        if (!OperatingSystem.IsMacOS() || key is null || !key.StartsWith("signing-selftest:", StringComparison.Ordinal)
            || action is not ("write" or "read" or "delete" or "legacy-write" or "legacy-delete")) return 2;
        try
        {
            if (Native.DisableKeychainUI() != 0) return 3;
            var store = new MacSecretStore();
            const string value = "YuWangSync synthetic signing probe";
            if (action is "legacy-write" or "legacy-delete")
            {
                if (Native.LegacySecretProbe(key, action == "legacy-write" ? value : null) != 0) return 5;
            }
            else if (action == "write") await store.SetAsync(key, value);
            else if (action == "read" && await store.GetAsync(key) != value) return 4;
            else if (action == "delete") await store.DeleteAsync(key);
            Console.WriteLine("Keychain signing probe passed: " + action); return 0;
        }
        catch (IOException) { Console.Error.WriteLine("Keychain signing probe failed; authorization was not retained."); return 1; }
    }
    public static async Task<int> PlatformAsync(string? output)
    {
        var root = Path.Combine(Path.GetTempPath(), "YuWangSync-platform-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var checks = new Dictionary<string, bool>(); var error = ""; var key = "selftest:" + Guid.NewGuid().ToString("N");
        ISecretStore store = OperatingSystem.IsMacOS() ? new MacSecretStore() : new WindowsSecretStore(Path.Combine(root, "secrets"));
        try
        {
            await store.SetAsync(key, "合成测试 secret"); checks["secret-roundtrip"] = await store.GetAsync(key) == "合成测试 secret";
            await store.DeleteAsync(key); checks["secret-delete"] = await store.GetAsync(key) is null;
            var input = Path.Combine(root, "中文文件.txt"); await File.WriteAllTextAsync(input, "snapshot test\n");
            IFileSnapshotProvider snapshots = OperatingSystem.IsMacOS() ? new MacSnapshotProvider() : new FileSnapshotProvider();
            checks["coordinated-snapshot"] = (await snapshots.CaptureAsync(input, Path.Combine(root, "snapshots"))).Hash == await LocalFiles.HashAsync(input);
        }
        catch (Exception e) { error = e.ToString(); }
        finally { try { await store.DeleteAsync(key); } catch { } }
        return await ReportAsync(output, checks.Count == 3 && checks.Values.All(x => x) && error.Length == 0, checks, error);
    }
    public static async Task<int> ReportAsync(string? output, bool passed, object checks, string error)
    {
        var json = JsonSerializer.Serialize(new { passed, architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(), checks, error }, new JsonSerializerOptions { WriteIndented = true });
        if (output is not null) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!); await File.WriteAllTextAsync(output, json); }
        Console.WriteLine(json); return passed ? 0 : 1;
    }
}
