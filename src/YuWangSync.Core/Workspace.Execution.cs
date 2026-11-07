using YuWangCloudSync.Models;
using YuWangCloudSync.Net;
using YuWangSync.Core.Storage;

namespace YuWangSync.Core;

public sealed partial class Workspace
{
    private Guid? _runningId;
    private async Task WorkerAsync()
    {
        var nextScan = DateTimeOffset.MinValue; var nextReconnect = DateTimeOffset.MinValue;
        var nextCleanup = DateTimeOffset.UtcNow.AddMinutes(1);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                try
                {
                    if (_scanRequested || DateTimeOffset.UtcNow >= nextScan)
                    { _scanRequested = false; await ScanEditsAsync(_stop.Token); nextScan = DateTimeOffset.UtcNow.AddSeconds(30); }
                    if (DateTimeOffset.UtcNow >= nextCleanup)
                    { await CleanupAsync(false, _stop.Token); nextCleanup = DateTimeOffset.UtcNow.AddHours(24); }
                    if (Offline && !ManualOffline && !_credentialsUnavailable && ActiveAccount is not null && DateTimeOffset.UtcNow >= nextReconnect)
                    { nextReconnect = DateTimeOffset.UtcNow.AddSeconds(30); await ReconnectAsync(_stop.Token); }
                    if (!Paused && !Offline && !ManualOffline) await RunOnceAsync(_stop.Token);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (Exception e) { Log?.Invoke("后台处理已保留现场：" + SafeError(e)); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    public async Task ScanEditsAsync(CancellationToken ct = default)
    {
        await _planning.WaitAsync(ct);
        try
        {
            foreach (var account in Store.Accounts())
            foreach (var entry in Store.Entries(account.Id).Where(e => !e.IsDirectory && e.LocalPath is not null))
            {
                ct.ThrowIfCancellationRequested(); var path = entry.LocalPath!;
                if (!File.Exists(path) || !Files.Owns(path)) continue; // Cache deletion is never a cloud delete.
                try
                {
                    var info = new FileInfo(path); var now = DateTimeOffset.UtcNow;
                    if (!_observations.TryGetValue(entry.Id, out var seen) || seen.Length != info.Length || seen.Written != info.LastWriteTimeUtc)
                    { _observations[entry.Id] = (info.Length, info.LastWriteTimeUtc, now); _scanRequested = true; continue; }
                    if (now - seen.Observed < TimeSpan.FromSeconds(2)) { _scanRequested = true; continue; }
                    var hash = await LocalFiles.HashAsync(path, ct);
                    if (hash == entry.ContentHash) continue;
                    Store.UpdateEntry(entry.Id, e => e with { Dirty = true });
                    var uploads = Store.Operations(account.Id).Where(o => o.EntryId == entry.Id && o.Kind == OperationKind.Upload).ToArray();
                    // Cancelling an edit snapshot is an explicit choice. Do not silently
                    // recreate it on the next watcher event; a new save or Retry can resume.
                    if (uploads.LastOrDefault() is { State: OperationState.Cancelled } cancelled && cancelled.SnapshotHash == hash) continue;
                    var pending = uploads.Where(o => !o.IsTerminal).ToArray();
                    if (pending.LastOrDefault()?.SnapshotHash == hash) continue;
                    var snapshot = await _snapshots.CaptureAsync(path, Files.SnapshotDirectory(account.Id), ct);
                    if (entry.ParentId is not { } parent) throw new IOException("编辑副本没有目标父目录。");
                    var previous = pending.LastOrDefault();
                    if (previous is { State: OperationState.Queued, MutationStarted: false }
                        && !Store.Operations(account.Id).Any(o => !o.IsTerminal && o.Dependencies.Contains(previous.Id)))
                        Store.Save(operations: [previous with { SnapshotPath = snapshot.Path, SnapshotHash = snapshot.Hash, SnapshotLength = snapshot.Length }]);
                    else
                    {
                        var projected = ProjectedEntries(account.Id).FirstOrDefault(e => e.Id == entry.Id);
                        if (projected is null) continue; // A queued delete has precedence; bytes remain protected.
                        Store.Save(operations: [NewOperation(projected, OperationKind.Upload, projected.ParentId ?? parent, projected.Name) with
                        { SnapshotPath = snapshot.Path, SnapshotHash = snapshot.Hash, SnapshotLength = snapshot.Length,
                            SourcePath = path, BaselineStamp = entry.DownloadStamp ?? entry.RemoteStamp, BaselineHash = entry.ContentHash,
                            Collision = CollisionChoice.Replace }]);
                    }
                    Notify();
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                { Store.UpdateEntry(entry.Id, item => item with { Dirty = true }); Log?.Invoke("编辑副本保留，稍后重试：" + SafeError(e)); }
            }
        }
        finally { _planning.Release(); }
    }

    public async Task<bool> RunOnceAsync(CancellationToken ct = default)
    {
        if (Paused || Offline || ManualOffline || ActiveAccount is not { } account || _client is null) return false;
        Operation? op = null;
        await _planning.WaitAsync(ct);
        try
        {
            var all = Store.Operations(account.Id);
            foreach (var blocked in all.Where(o => o.State == OperationState.Blocked && o.Error?.StartsWith("依赖任务", StringComparison.Ordinal) == true))
                if (blocked.Dependencies.All(id => all.Any(d => d.Id == id && d.State == OperationState.Completed)))
                    Store.UpdateOperation(blocked.Id, o => o with { State = OperationState.Queued, Error = null });
            all = Store.Operations(account.Id);
            foreach (var candidate in all.Where(o => o.State is OperationState.Queued or OperationState.Uncertain && (o.RetryAfter is null || o.RetryAfter <= DateTimeOffset.UtcNow)))
            {
                var dependencies = candidate.Dependencies.Select(Store.GetOperation).ToArray();
                if (dependencies.Any(d => d is null || d.State is OperationState.Cancelled or OperationState.Failed or OperationState.Blocked))
                { Store.UpdateOperation(candidate.Id, o => o with { State = OperationState.Blocked, Error = "依赖任务需要先处理。" }); continue; }
                if (dependencies.Any(d => d!.State != OperationState.Completed)) continue;
                op = candidate; Store.UpdateOperation(op.Id, o => o with { State = OperationState.Running, Error = null }); break;
            }
        }
        finally { _planning.Release(); }
        if (op is null) return false;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token, _accountStop.Token);
        _currentStop = linked; _runningId = op.Id; Notify();
        var acquired = false;
        try
        {
            await _network.WaitAsync(linked.Token); acquired = true;
            if (ActiveAccount?.Id != account.Id || _client is null) throw new OperationCanceledException();
            await ExecuteAsync(Store.GetOperation(op.Id)!, _client, linked.Token);
        }
        catch (OperationCanceledException)
        {
            Store.UpdateOperation(op.Id, current => current.IsTerminal ? current : current with
            {
                State = current.MutationStarted ? OperationState.Uncertain : OperationState.Queued,
                Error = "操作已暂停，内容与恢复记录已保存。",
                RetryAfter = current.RetryAfter == DateTimeOffset.MaxValue ? current.RetryAfter : null
            });
        }
        catch (Exception e)
        {
            if (e is HttpRequestException or TimeoutException) { Offline = true; ConnectionStatus = "连接中断，待办已保留"; }
            Store.UpdateOperation(op.Id, current => current with
            {
                State = e is ConflictException or UnauthorizedAccessException ? OperationState.Blocked
                    : current.MutationStarted ? OperationState.Uncertain : e is HttpRequestException or TimeoutException ? OperationState.Queued : OperationState.Failed,
                Error = SafeError(e), RetryAfter = current.MutationStarted || e is HttpRequestException or TimeoutException ? DateTimeOffset.UtcNow.AddSeconds(30) : null
            });
            Log?.Invoke("任务未完成：" + SafeError(e));
        }
        finally { _currentStop = null; _runningId = null; if (acquired) _network.Release(); Notify(); }
        return true;
    }
    private static string SafeError(Exception error) => error is HttpRequestException ? "网络请求失败，稍后重连后核对。"
        : error is OperationCanceledException ? "操作已取消。" : error.Message;
    private void Remember(Guid id, Func<Operation, Operation> update) => Store.UpdateOperation(id, update);
    private static void RequireWritable(FolderListingResult listing)
    {
        if (!listing.IsValidPage || listing.Permission != DirectoryPermission.FullAccess)
            throw new UnauthorizedAccessException("目标目录没有已确认的写权限。只读、未知权限或登录失效时不写入。");
    }
    private async Task ExecuteAsync(Operation op, ICloudClient cloud, CancellationToken ct)
    {
        var entry = RequireEntry(op.EntryId);
        if (entry.AccountId != op.AccountId || op.AccountId != ActiveAccount?.Id) throw new IOException("账号不匹配，停止任务。");
        var parent = RequireEntry(op.TargetParentId ?? entry.ParentId ?? throw new IOException("任务缺少父目录。"));
        if (parent.AccountId != op.AccountId || parent.DiskId != entry.DiskId || !parent.IsDirectory) throw new IOException("目标目录身份不符。");
        if (!parent.IsDiskRoot && parent.RemoteId == 0) throw new IOException("目标目录尚未创建。");
        var target = await cloud.ListAsync(parent.DiskId, parent.RemoteId, ct); RequireWritable(target);
        switch (op.Kind)
        {
            case OperationKind.CreateFolder:
            {
                var existing = target.Items.Where(i => i.Name.Equals(op.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
                YuWangItem created;
                if (existing.Length == 1 && existing[0].IsFolder && op.NewRemoteId == 0)
                    created = existing[0]; // Directory merge; never create another same-name directory.
                else
                {
                    if (existing.Any(i => !i.IsFolder) || existing.Length > 1) throw new ConflictException("创建目录时遇到同名冲突。");
                    if (op.MutationStarted && op.NewRemoteId == 0)
                        throw new UncertainException("目录创建结果未确认，停止重复创建。请在云端核对后关联已创建的目录 ID。");
                    created = await cloud.CreateFolderAsync(parent.DiskId, parent.RemoteId, op.Name, op.NewRemoteId,
                        id => Remember(op.Id, o => o with { NewRemoteId = id }),
                        ids => Remember(op.Id, o => o with { BeforeIds = ids, MutationStarted = true }), ct);
                }
                Complete(op, FromRemote(entry, created, parent.Id)); break;
            }
            case OperationKind.Upload:
                await ExecuteUploadAsync(op, entry, parent, target, cloud, ct); break;
            case OperationKind.Rename:
            case OperationKind.Move:
            case OperationKind.Delete:
            {
                CheckMutable(entry);
                var sourceParent = RequireEntry(entry.ParentId!.Value);
                var source = sourceParent.Id == parent.Id ? target : await cloud.ListAsync(entry.DiskId, sourceParent.RemoteId, ct);
                RequireWritable(source);
                var old = source.Items.SingleOrDefault(i => i.Id == entry.RemoteId && i.IsFolder == entry.IsDirectory);
                if (op.Kind == OperationKind.Delete && old is null)
                { Complete(op, entry with { RemoteMissing = true }); return; }
                var arrived = target.Items.SingleOrDefault(i => i.Id == entry.RemoteId && i.Name == op.Name && i.IsFolder == entry.IsDirectory);
                if (op.MutationStarted && op.Kind is OperationKind.Rename or OperationKind.Move && arrived is not null
                    && (op.Kind == OperationKind.Rename || old is null))
                { Complete(op, FromRemote(entry, arrived, parent.Id)); return; }
                if (old is null) throw new ConflictException("原云端条目已移动或被替换，请核对。");
                if (op.BaselineStamp.Length == 0 || Entry.Stamp(old) != op.BaselineStamp)
                    throw new ConflictException("云端版本已变化，暂停操作；本地内容已保留。");
                if (entry.IsDirectory && op.Kind is OperationKind.Delete or OperationKind.Move)
                {
                    if (op.BaselineTree is null) throw new ConflictException("目录子树基准不完整，需要联网核对并确认。");
                    var current = await RemoteTreeAsync(entry.DiskId, entry.RemoteId, cloud, ct);
                    if (current.Count != op.BaselineTree.Count || current.Any(p => !op.BaselineTree.TryGetValue(p.Key, out var stamp) || stamp != p.Value))
                        throw new ConflictException("目录内出现新增、删除或修改，暂停目录操作。");
                }
                if (op.Kind != OperationKind.Delete && target.Items.Any(i => i.Id != entry.RemoteId && i.Name.Equals(op.Name, StringComparison.OrdinalIgnoreCase)))
                    throw new ConflictException("目标已有同名条目，未移动或改名。");
                Remember(op.Id, o => o with { MutationStarted = true });
                if (op.Kind == OperationKind.Delete)
                {
                    if (!await cloud.DeleteAsync(entry.DiskId, sourceParent.RemoteId, entry.RemoteId, entry.IsDirectory, ct)) throw new UncertainException("删除结果未确认。");
                    Complete(op, entry with { RemoteMissing = true });
                }
                else
                {
                    if (!await cloud.RenameAsync(entry.DiskId, sourceParent.RemoteId, entry.RemoteId, entry.IsDirectory, op.Name, parent.RemoteId, ct))
                        throw new UncertainException("移动或改名结果未确认。");
                    var verified = await cloud.ListAsync(entry.DiskId, parent.RemoteId, ct);
                    var item = verified.Items.SingleOrDefault(i => i.Id == entry.RemoteId && i.Name == op.Name)
                        ?? throw new UncertainException("目标条目尚未通过复查。");
                    Complete(op, FromRemote(entry, item, parent.Id));
                }
                break;
            }
        }
    }
    private async Task ExecuteUploadAsync(Operation op, Entry entry, Entry parent, FolderListingResult target, ICloudClient cloud, CancellationToken ct)
    {
        if (op.SnapshotPath is null || op.SnapshotHash is null || !Files.Owns(op.SnapshotPath) || !File.Exists(op.SnapshotPath))
            throw new IOException("上传快照缺失，未发送内容。");
        LocalFiles.RejectLinks(op.SnapshotPath);
        if (new FileInfo(op.SnapshotPath).Length != op.SnapshotLength || await LocalFiles.HashAsync(op.SnapshotPath, ct) != op.SnapshotHash)
            throw new IOException("上传快照已变化，停止发送。");
        if (!op.MutationStarted)
        {
            var old = target.Items.SingleOrDefault(i => i.Id == entry.RemoteId && !i.IsFolder);
            if (entry.RemoteId != 0)
            {
                if (old is null || Entry.Stamp(old) != op.BaselineStamp) throw new ConflictException("云端文件已更新、移动或删除，暂停覆盖。");
                if (op.BaselineHash is null) throw new ConflictException("没有可靠的编辑基准，请明确确认覆盖或另存副本。");
                var currentHash = await cloud.HashAsync(old.Id, ct);
                if (currentHash.Hash != op.BaselineHash) throw new ConflictException("云端内容已被其他操作修改，请处理冲突。");
            }
            else if (target.Items.Any(i => i.Name.Equals(op.Name, StringComparison.OrdinalIgnoreCase)))
                throw new ConflictException("云端新增了同名文件，请选择跳过、覆盖或改名。");
            var backup = Path.GetFileNameWithoutExtension(op.Name) + "_back" + Path.GetExtension(op.Name);
            if (target.Items.Any(i => i.Name.Equals(backup, StringComparison.OrdinalIgnoreCase))) backup = UniqueName(backup, target.Items.Select(i => i.Name));
            LocalFiles.ValidateCloudName(backup);
            Remember(op.Id, o => o with { OldRemoteId = entry.RemoteId, BackupName = backup, MutationStarted = true });
            op = Store.GetOperation(op.Id)!;
        }
        // A retry may find the old ID under the backup name. It must still contain our
        // original baseline before we let the inherited replacement protocol recycle it.
        if (op.OldRemoteId != 0 && target.Items.Any(i => i.Id == op.OldRemoteId))
        {
            if (op.BaselineHash is null || (await cloud.HashAsync(op.OldRemoteId, ct)).Hash != op.BaselineHash)
                throw new ConflictException("原文件或备份内容发生变化，保留两端内容并暂停事务。");
        }
        await using var input = new FileStream(op.SnapshotPath!, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        var uploaded = await cloud.UploadAsync(entry.DiskId, parent.RemoteId, op, input,
            id => Remember(op.Id, o => o with { NewRemoteId = id }),
            attempt => Remember(op.Id, o => o with { UploadAttempt = attempt }),
            p => Progress?.Invoke(new(op.Id, op.Name, p.Stage switch { UploadStage.Preparing => "准备", UploadStage.Uploading => "上传",
                UploadStage.Verifying => "下载复核", _ => "确认结果" }, p.Stage == UploadStage.Verifying ? p.VerifiedBytes : p.SentBytes, p.TotalBytes)), ct);
        Remember(op.Id, o => o with { NewRemoteId = uploaded.Id });
        entry = RequireEntry(entry.Id);
        var completed = FromRemote(entry, uploaded, parent.Id);
        if (entry.LocalPath is { } local && File.Exists(local) && op.SourcePath == local)
        {
            var current = await LocalFiles.HashAsync(local, ct);
            completed = completed with { ContentHash = op.SnapshotHash, DownloadStamp = Entry.Stamp(uploaded), Dirty = current != op.SnapshotHash };
        }
        else if (entry.LocalPath is null) completed = completed with { Dirty = false };
        Complete(op, completed);
        _scanRequested = true;
    }
    private static Entry FromRemote(Entry entry, YuWangItem remote, Guid parent) => entry with
    { RemoteId = remote.Id, Name = remote.Name, ParentId = parent, Size = remote.Size, UploadTime = remote.UploadTime,
        RemoteStamp = Entry.Stamp(remote), RemoteMissing = false };
    private async Task<Dictionary<ulong, string>> RemoteTreeAsync(ulong disk, ulong folder, ICloudClient cloud, CancellationToken ct)
    {
        var result = new Dictionary<ulong, string>(); var visited = new HashSet<ulong>();
        async Task Visit(ulong id, int depth)
        {
            if (depth > 128 || !visited.Add(id)) throw new IOException("目录过深或存在循环。");
            var listing = await cloud.ListAsync(disk, id, ct);
            foreach (var item in listing.Items)
            {
                if (result.Count > 100000) throw new IOException("目录条目超过单次核对上限。");
                result[item.Id] = $"{id}|{Entry.Stamp(item)}";
                if (item.IsFolder) await Visit(item.Id, depth + 1);
            }
        }
        await Visit(folder, 0); return result;
    }
    private void Complete(Operation op, Entry entry)
    {
        var priorEntry = RequireEntry(entry.Id);
        var all = Store.Operations(op.AccountId);
        var updates = new List<Operation> { Store.GetOperation(op.Id)! with { State = OperationState.Completed, Error = null, RetryAfter = null } };
        foreach (var dependent in all.Where(o => !o.IsTerminal && o.Dependencies.Contains(op.Id)))
        {
            var next = dependent;
            if (dependent.EntryId == entry.Id)
                next = next with { BaselineStamp = entry.RemoteStamp, BaselineHash = op.Kind == OperationKind.Upload ? op.SnapshotHash : entry.ContentHash };
            if (dependent.BaselineTree is { } baseline)
            {
                var tree = new Dictionary<ulong, string>(baseline);
                tree.Remove(priorEntry.RemoteId);
                if (!entry.RemoteMissing && entry.RemoteId != 0 && entry.ParentId is { } p)
                    tree[entry.RemoteId] = $"{RequireEntry(p).RemoteId}|{entry.RemoteStamp}";
                next = next with { BaselineTree = tree };
            }
            updates.Add(next);
        }
        var entries = new List<Entry> { entry };
        if (op.Kind == OperationKind.Delete && entry.IsDirectory)
        {
            var descendants = new HashSet<Guid> { entry.Id }; var stored = Store.Entries(op.AccountId);
            while (true)
            {
                var count = descendants.Count;
                descendants.UnionWith(stored.Where(e => e.ParentId is { } p && descendants.Contains(p)).Select(e => e.Id));
                if (descendants.Count == count) break;
            }
            entries.AddRange(stored.Where(e => e.Id != entry.Id && descendants.Contains(e.Id)).Select(e => e with { RemoteMissing = true }));
        }
        Store.Save(entries, updates); Notify();
    }

    public async Task LinkCreatedFolderAsync(Guid operationId, ulong remoteId, CancellationToken ct = default)
    {
        var op = Store.GetOperation(operationId) ?? throw new IOException("任务不存在。");
        if (op.Kind != OperationKind.CreateFolder || !op.MutationStarted || op.IsTerminal) throw new IOException("仅未确认的新建目录可以关联。");
        var parent = RequireEntry(op.TargetParentId!.Value);
        var listing = await WithCloudAsync(op.AccountId, (c, t) => c.ListAsync(parent.DiskId, parent.RemoteId, t), ct);
        RequireWritable(listing);
        if (!listing.Items.Any(i => i.Id == remoteId && i.IsFolder) || op.BeforeIds?.Contains(remoteId) == true)
            throw new IOException("该目录不是本次操作之后新增的候选项。");
        Remember(op.Id, o => o with { NewRemoteId = remoteId, State = OperationState.Queued, RetryAfter = null, Error = null }); Notify();
    }
}
