using YuWangCloudSync.Models;
using YuWangCloudSync.Net;
using YuWangSync.Core.Storage;

namespace YuWangSync.Core;

public sealed partial class Workspace : IAsyncDisposable
{
    public IOperationStore Store { get; }
    public LocalFiles Files { get; }
    private readonly ISecretStore _secrets;
    private readonly IFileSnapshotProvider _snapshots;
    private readonly Func<Account, string, ICloudClient> _factory;
    private readonly SemaphoreSlim _network = new(1, 1);
    private readonly SemaphoreSlim _planning = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private CancellationTokenSource _accountStop = new();
    private CancellationTokenSource? _currentStop;
    private ICloudClient? _client;
    private Task? _worker;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _opened = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, SemaphoreSlim> _localLocks = new();
    private readonly Dictionary<Guid, (long Length, DateTime Written, DateTimeOffset Observed)> _observations = [];
    private FileSystemWatcher? _watcher;
    private volatile bool _scanRequested = true;
    public Account? ActiveAccount { get; private set; }
    public bool Offline { get; private set; } = true;
    public bool Paused { get; private set; }
    public bool ManualOffline { get; private set; }
    public bool IsStopping => _stop.IsCancellationRequested;
    public string ConnectionStatus { get; private set; } = "请添加账号";
    public event Action? Changed;
    public event Action<TransferProgress>? Progress;
    public Action<string>? Log { get; set; }
    public Workspace(IOperationStore store, LocalFiles files, ISecretStore secrets, IFileSnapshotProvider snapshots,
        Func<Account, string, ICloudClient>? factory = null)
    {
        Store = store; Files = files; _secrets = secrets; _snapshots = snapshots;
        _factory = factory ?? ((a, p) => new CloudClient(a, p, message => Log?.Invoke(message)));
        var working = Path.Combine(files.DataRoot, "working"); Directory.CreateDirectory(working);
        _watcher = new(working) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName };
        _watcher.Changed += (_, _) => _scanRequested = true;
        _watcher.Created += (_, _) => _scanRequested = true;
        _watcher.Renamed += (_, _) => _scanRequested = true;
        _watcher.Error += (_, _) => _scanRequested = true;
        _watcher.EnableRaisingEvents = true;
    }
    public void Start() => _worker ??= Task.Run(WorkerAsync);
    private void Notify() => Changed?.Invoke();
    public ICloudClient CreateLoginClient(Account account, string password) => _factory(account, password);
    public async Task CompleteLoginAsync(Account account, string password, ICloudClient client, CancellationToken ct = default)
    {
        await _secrets.SetAsync(account.Id + ":password", password, ct);
        if (client.SessionId is { } session) await _secrets.SetAsync(account.Id + ":session", session, ct);
        Store.SaveAccount(account);
        await ActivateClientAsync(account, client, true, ct);
    }
    public async Task ActivateAsync(Account account, CancellationToken ct = default)
    {
        var password = await _secrets.GetAsync(account.Id + ":password", ct) ?? "";
        var client = _factory(account, password);
        client.SetSession(await _secrets.GetAsync(account.Id + ":session", ct));
        await ActivateClientAsync(account, client, false, ct);
        await ReconnectAsync(ct);
    }
    private async Task ActivateClientAsync(Account account, ICloudClient client, bool online, CancellationToken ct)
    {
        _accountStop.Cancel(); _currentStop?.Cancel();
        await _network.WaitAsync(ct);
        try
        {
            _client?.Dispose(); _accountStop.Dispose(); _accountStop = new();
            _client = client; ActiveAccount = account; Offline = !online;
            ConnectionStatus = online ? "在线" : "离线缓存"; _scanRequested = true;
        }
        finally { _network.Release(); }
        Notify();
    }
    public async Task RemoveAccountAsync(Account account, CancellationToken ct = default)
    {
        if (Store.Operations(account.Id).Any(o => !o.IsTerminal) || Store.Entries(account.Id).Any(e => e.Dirty))
            throw new InvalidOperationException("该账号仍有待办或本地修改，请先处理或导出。");
        if (account.Id == ActiveAccount?.Id)
        {
            _accountStop.Cancel(); _currentStop?.Cancel(); await _network.WaitAsync(ct);
            try { Store.RemoveAccount(account.Id); _client?.Dispose(); _client = null; ActiveAccount = null; Offline = true; }
            finally { _network.Release(); }
        }
        else Store.RemoveAccount(account.Id);
        await _secrets.DeleteAsync(account.Id + ":password", ct);
        await _secrets.DeleteAsync(account.Id + ":session", ct); Notify();
    }
    public void SetPaused(bool value) { Paused = value; if (value) _currentStop?.Cancel(); Notify(); }
    public void SetManualOffline(bool value)
    { ManualOffline = value; if (value) { Offline = true; _currentStop?.Cancel(); ConnectionStatus = "离线模式"; } Notify(); }
    public async Task ReconnectAsync(CancellationToken ct = default)
    {
        if (ManualOffline || _client is null) return;
        await _network.WaitAsync(ct);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _accountStop.Token, _stop.Token);
            var valid = await _client.CheckSessionAsync(linked.Token);
            Offline = !valid; ConnectionStatus = valid ? "在线" : "无法复用会话，请重新登录或稍后重连";
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException)
        { Offline = true; ConnectionStatus = "连接不可用，显示离线缓存"; }
        finally { _network.Release(); }
        Notify();
    }
    private async Task<T> WithCloudAsync<T>(Guid account, Func<ICloudClient, CancellationToken, Task<T>> action, CancellationToken ct)
    {
        if (account != ActiveAccount?.Id || _client is null || ManualOffline || Offline) throw new IOException("当前离线，操作已保留在本地。");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _accountStop.Token, _stop.Token);
        await _network.WaitAsync(linked.Token);
        try
        {
            if (account != ActiveAccount?.Id || _client is null) throw new OperationCanceledException();
            return await action(_client, linked.Token);
        }
        catch (HttpRequestException) { Offline = true; ConnectionStatus = "连接中断，待办已保留"; throw; }
        finally { _network.Release(); }
    }
    public async Task RefreshTreeAsync(CancellationToken ct = default)
    {
        var account = ActiveAccount ?? throw new InvalidOperationException("请先选择账号。");
        var tree = await WithCloudAsync(account.Id, (c, t) => c.TreeAsync(t), ct);
        var existing = Store.Entries(account.Id);
        var roots = tree.Where(n => n.IsDisk).Select(n =>
        {
            var root = existing.FirstOrDefault(e => e.IsDiskRoot && e.DiskId == n.Id)
                ?? new Entry { AccountId = account.Id, DiskId = n.Id, RemoteId = 0, IsDiskRoot = true, IsDirectory = true };
            return root with { Name = n.Name, RemoteMissing = false };
        }).ToList();
        roots.AddRange(existing.Where(e => e.IsDiskRoot && roots.All(r => r.Id != e.Id)).Select(e => e with { RemoteMissing = true }));
        Store.SaveTree(account.Id, tree); Store.Save(entries: roots); Notify();
    }
    public IReadOnlyList<Entry> ProjectedEntries(Guid account)
    {
        var entries = Store.Entries(account).ToDictionary(e => e.Id);
        var operations = Store.Operations(account).Where(o => !o.IsTerminal).ToArray();
        var hidden = new HashSet<Guid>();
        foreach (var op in operations)
        {
            if (!entries.TryGetValue(op.EntryId, out var entry)) continue;
            var status = op.KindLabel + " · " + op.StateLabel;
            if (op.Kind is OperationKind.Rename or OperationKind.Upload or OperationKind.CreateFolder)
                entry = entry with { Name = op.Name, Status = status };
            if (op.Kind == OperationKind.Move) entry = entry with { ParentId = op.TargetParentId, Name = op.Name, Status = status };
            if (op.Kind == OperationKind.Delete) hidden.Add(entry.Id);
            entries[entry.Id] = entry with { Status = status };
        }
        bool IsHidden(Entry entry)
        {
            var visited = new HashSet<Guid>();
            for (Entry? current = entry; current is not null && visited.Add(current.Id); current = current.ParentId is { } p ? entries.GetValueOrDefault(p) : null)
                if (hidden.Contains(current.Id)) return true;
            return false;
        }
        return entries.Values.Where(e => !IsHidden(e) && (!e.RemoteMissing || e.Dirty || operations.Any(o => o.EntryId == e.Id)))
            .Select(e => e.Status.Length > 0 ? e : e with { Status = e.Dirty ? "本地已修改" : e.LocalPath is not null ?
                (e.DownloadStamp != e.RemoteStamp ? "云端有更新 · 本地副本保留" : e.KeepOffline ? "离线可用" : "已下载") : "云端" })
            .OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.CurrentCulture).ToArray();
    }
    public IReadOnlyList<Entry> Children(Guid parent) => ActiveAccount is { } a ? ProjectedEntries(a.Id).Where(e => e.ParentId == parent).ToArray() : [];
    public async Task BrowseAsync(Guid parentId, bool force = false, CancellationToken ct = default)
    {
        var parent = Store.GetEntry(parentId) ?? throw new IOException("目录不存在。");
        if (!parent.IsDirectory || parent.RemoteId == 0 && !parent.IsDiskRoot) return;
        if (!force && parent.LastListed is { } listed && DateTimeOffset.UtcNow - listed < TimeSpan.FromMinutes(30)) return;
        if (Offline || ManualOffline) { Notify(); return; }
        var result = await WithCloudAsync(parent.AccountId, (c, t) => c.ListAsync(parent.DiskId, parent.RemoteId, t), ct);
        ApplyListing(parent, result); Notify();
    }
    private void ApplyListing(Entry parent, FolderListingResult result)
    {
        if (!result.IsValidPage) throw new IOException("目录结果不完整。");
        var existing = Store.Entries(parent.AccountId);
        var changed = new List<Entry>();
        foreach (var item in result.Items)
        {
            var entry = existing.FirstOrDefault(e => e.DiskId == parent.DiskId && e.RemoteId == item.Id && e.IsDirectory == item.IsFolder && !e.IsDiskRoot)
                ?? new Entry { AccountId = parent.AccountId, DiskId = parent.DiskId, RemoteId = item.Id, IsDirectory = item.IsFolder };
            changed.Add(entry with { ParentId = parent.Id, Name = item.Name, Size = item.Size, UploadTime = item.UploadTime, RemoteStamp = Entry.Stamp(item), RemoteMissing = false });
        }
        changed.AddRange(existing.Where(e => e.ParentId == parent.Id && e.RemoteId != 0 && changed.All(c => c.Id != e.Id))
            .Select(e => e with { RemoteMissing = true }));
        var latest = Store.GetEntry(parent.Id)!;
        changed.Add(latest with { LastListed = DateTimeOffset.UtcNow, Permission = result.Permission });
        Store.Save(entries: changed);
    }
    public async Task<string> EnsureLocalAsync(Guid id, bool keepOffline = false, CancellationToken ct = default)
    {
        var gate = _localLocks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1)); await gate.WaitAsync(ct);
        try { return await EnsureLocalCoreAsync(id, keepOffline, ct); }
        finally { gate.Release(); }
    }
    private async Task<string> EnsureLocalCoreAsync(Guid id, bool keepOffline, CancellationToken ct)
    {
        var entry = Store.GetEntry(id) ?? throw new IOException("文件不存在。");
        if (entry.IsDirectory) throw new IOException("请选择文件。");
        if (entry.LocalPath is { } local && File.Exists(local))
        {
            var changed = await LocalFiles.HashAsync(local, ct) != entry.ContentHash;
            // Never replace a document handed to another app in this session.
            if (changed || entry.DownloadStamp == entry.RemoteStamp || Offline || _opened.ContainsKey(id))
            {
                Store.UpdateEntry(id, e => e with { Dirty = e.Dirty || changed, KeepOffline = e.KeepOffline || keepOffline, LastAccess = DateTimeOffset.UtcNow });
                return local;
            }
        }
        if (entry.RemoteId == 0)
        {
            var pending = Store.Operations(entry.AccountId).LastOrDefault(o => o.EntryId == id && o.SnapshotPath is not null && !o.IsTerminal);
            if (pending?.SnapshotPath is not { } snapshot) throw new IOException("文件尚未上传，且没有可用快照。");
            var path = Files.WorkingPath(entry); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.Copy(snapshot, path, false);
            Store.UpdateEntry(id, e => e with { LocalPath = path, ContentHash = pending.SnapshotHash, Dirty = true });
            return path;
        }
        var destination = Files.WorkingPath(entry); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var part = destination + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            await using var output = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true);
            var receipt = await WithCloudAsync(entry.AccountId, (c, t) => c.DownloadAsync(entry, output,
                (done, total) => Progress?.Invoke(new(null, entry.Name, "下载", done, total)), t), ct);
            await output.FlushAsync(ct); output.Flush(true); await output.DisposeAsync();
            if (File.Exists(destination) && entry.LocalPath is not null && await LocalFiles.HashAsync(destination, ct) != entry.ContentHash)
                throw new ConflictException("下载期间本地副本已被编辑，未覆盖。");
            File.Move(part, destination, true);
            Store.UpdateEntry(id, e => e with { LocalPath = destination, Size = receipt.Length, ContentHash = receipt.Hash,
                DownloadStamp = entry.RemoteStamp, Dirty = false, KeepOffline = e.KeepOffline || keepOffline, LastAccess = DateTimeOffset.UtcNow });
            Notify(); return destination;
        }
        finally { if (File.Exists(part)) File.Delete(part); }
    }
    public async Task<string> OpenAsync(Guid id, CancellationToken ct = default)
    {
        var path = await EnsureLocalAsync(id, ct: ct);
        _opened.TryAdd(id, 0); _scanRequested = true; Notify(); return path;
    }
    public async Task<IReadOnlyList<string>> DownloadRecursiveAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var files = new List<Guid>(); var visited = new HashSet<Guid>(); var errors = new List<string>();
        async Task Visit(Guid id)
        {
            ct.ThrowIfCancellationRequested();
            if (!visited.Add(id)) return;
            var entry = Store.GetEntry(id)!;
            if (!entry.IsDirectory) { files.Add(id); return; }
            await BrowseAsync(id, force: true, ct);
            if (Store.GetEntry(id)!.LastListed is null && (entry.IsDiskRoot || entry.RemoteId != 0)) throw new IOException("目录从未缓存，无法离线完整下载。");
            foreach (var child in Children(id)) await Visit(child.Id);
        }
        foreach (var id in ids)
            try { await Visit(id); } catch (Exception e) when (e is not OperationCanceledException) { errors.Add(e.Message); }
        long complete = 0;
        foreach (var id in files)
        {
            ct.ThrowIfCancellationRequested();
            try { await EnsureLocalAsync(id, true, ct); }
            catch (Exception e) when (e is not OperationCanceledException) { errors.Add(Store.GetEntry(id)!.Name + ": " + e.Message); }
            Progress?.Invoke(new(null, "离线下载", $"已处理 {++complete}/{files.Count} 个文件", complete, files.Count));
        }
        return errors;
    }
    public async Task ExportAsync(Guid id, string destination, CancellationToken ct = default, bool overwrite = false)
    {
        var entry = Store.GetEntry(id) ?? throw new IOException("条目不存在。");
        if (!entry.IsDirectory)
        {
            var source = await EnsureLocalAsync(id, ct: ct);
            if (Path.GetFullPath(source) == Path.GetFullPath(destination)) return;
            var part = destination + "." + Guid.NewGuid().ToString("N") + ".part";
            try
            {
                await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true))
                await using (var output = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
                { await input.CopyToAsync(output, ct); await output.FlushAsync(ct); output.Flush(true); }
                File.Move(part, destination, overwrite);
            }
            finally { if (File.Exists(part)) File.Delete(part); }
            return;
        }
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("导出目录已存在，请选择新名称，避免覆盖。");
        await BrowseAsync(id, true, ct);
        if (Store.GetEntry(id)!.LastListed is null && (entry.IsDiskRoot || entry.RemoteId != 0)) throw new IOException("目录尚未完整缓存，不能导出为空目录。");
        Directory.CreateDirectory(destination);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in Children(id))
        {
            var name = LocalFiles.SafeName(child.Name);
            if (!names.Add(name)) { name = Path.GetFileNameWithoutExtension(name) + "_" + child.Id.ToString("N")[..8] + Path.GetExtension(name); names.Add(name); }
            await ExportAsync(child.Id, Path.Combine(destination, name), ct);
        }
    }
    public async Task<int> CleanupAsync(bool allClean, CancellationToken ct = default)
    {
        var count = 0;
        foreach (var account in Store.Accounts())
        foreach (var entry in Store.Entries(account.Id))
        {
            if (entry.LocalPath is not { } path || !File.Exists(path) || !Files.Owns(path) || entry.Dirty || _opened.ContainsKey(entry.Id)
                || Store.Operations(account.Id).Any(o => o.EntryId == entry.Id && !o.IsTerminal)
                || !allClean && (entry.KeepOffline || DateTimeOffset.UtcNow - entry.LastAccess < TimeSpan.FromDays(7))) continue;
            LocalFiles.RejectLinks(path);
            if (await LocalFiles.HashAsync(path, ct) != entry.ContentHash) { Store.UpdateEntry(entry.Id, e => e with { Dirty = true }); continue; }
            File.Delete(path); Store.UpdateEntry(entry.Id, e => e with { LocalPath = null, ContentHash = null, KeepOffline = false }); count++;
        }
        Notify(); return count;
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _accountStop.Cancel(); _currentStop?.Cancel(); _watcher?.Dispose();
        if (_worker is not null) try { await _worker; } catch (OperationCanceledException) { }
        await _network.WaitAsync();
        try { _client?.Dispose(); } finally { _network.Release(); }
        _accountStop.Dispose(); _stop.Dispose(); Store.Dispose();
    }
}
