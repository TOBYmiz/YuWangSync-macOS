using YuWangSync.Core.Storage;

namespace YuWangSync.Core;

public sealed partial class Workspace
{
    private Entry RequireEntry(Guid id) => Store.GetEntry(id) ?? throw new IOException("条目不存在。");
    private Entry Projected(Guid id)
    {
        var raw = RequireEntry(id);
        return ProjectedEntries(raw.AccountId, e => e.Id == id).FirstOrDefault() ?? throw new IOException("条目已排队删除。");
    }
    private List<Guid> Dependencies(Guid account, params Guid[] entries)
    {
        var related = new HashSet<Guid>(entries);
        foreach (var id in entries)
        {
            var entry = Store.GetEntry(id); var seen = new HashSet<Guid>();
            while (entry?.ParentId is { } parent && seen.Add(parent)) { related.Add(parent); entry = Store.GetEntry(parent); }
        }
        return Store.Operations(account).Where(o => !o.IsTerminal && related.Contains(o.EntryId)).Select(o => o.Id).ToList();
    }
    private void CheckAccount(Entry entry)
    {
        if (entry.AccountId != ActiveAccount?.Id) throw new InvalidOperationException("请先切换至该条目所属账号。");
    }
    private static void CheckMutable(Entry entry)
    { if (entry.IsDiskRoot) throw new InvalidOperationException("不能修改共享盘根目录。"); }

    public Guid CreateFolder(Guid parentId, string name)
    {
        LocalFiles.ValidateCloudName(name); var parent = Projected(parentId); CheckAccount(parent);
        if (!parent.IsDirectory) throw new IOException("目标不是目录。");
        if (Children(parentId).Any(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new ConflictException("已存在同名条目。");
        var entry = new Entry { AccountId = parent.AccountId, ParentId = parentId, DiskId = parent.DiskId, Name = name, IsDirectory = true };
        var op = new Operation { AccountId = parent.AccountId, EntryId = entry.Id, TargetParentId = parentId,
            Name = name, Kind = OperationKind.CreateFolder, Dependencies = Dependencies(parent.AccountId, parentId) };
        Store.Save([entry], [op]); Notify(); return entry.Id;
    }
    public void Rename(Guid id, string name)
    {
        LocalFiles.ValidateCloudName(name); var entry = Projected(id); CheckAccount(entry); CheckMutable(entry);
        if (entry.ParentId is not { } parent) throw new IOException("条目没有目标父目录。");
        if (Children(parent).Any(e => e.Id != id && e.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new ConflictException("已存在同名条目。");
        if (entry.Name == name) return;
        Store.Save(operations: [NewOperation(entry, OperationKind.Rename, parent, name)]); Notify();
    }
    public void Move(Guid id, Guid parentId)
    {
        var entry = Projected(id); var parent = Projected(parentId); CheckAccount(entry); CheckMutable(entry);
        if (entry.AccountId != parent.AccountId || entry.DiskId != parent.DiskId) throw new IOException("首版仅支持同账号、同共享盘移动。");
        if (!parent.IsDirectory) throw new IOException("目标不是目录。");
        var seen = new HashSet<Guid>();
        for (Entry? ancestor = parent; ancestor is not null && seen.Add(ancestor.Id); ancestor = ancestor.ParentId is { } p ? Projected(p) : null)
            if (ancestor.Id == id) throw new IOException("不能移入自身或子目录。");
        if (entry.ParentId == parentId) return;
        if (Children(parentId).Any(e => e.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase))) throw new ConflictException("目标目录已有同名条目。");
        Store.Save(operations: [NewOperation(entry, OperationKind.Move, parentId, entry.Name) with
        { BaselineTree = entry.IsDirectory ? CachedTreeBaseline(id) : null }]); Notify();
    }
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var entry = Projected(id); CheckAccount(entry); CheckMutable(entry);
        if (entry.ParentId is not { } parent) throw new IOException("条目没有父目录。");
        if (entry.RemoteId == 0)
        {
            var creator = Store.Operations(entry.AccountId).FirstOrDefault(o => o.EntryId == id && !o.IsTerminal);
            if (creator is not null && !creator.MutationStarted) { Cancel(creator.Id); return; }
        }
        if (entry.IsDirectory && !Offline && !ManualOffline) await CacheSubtreeAsync(id, ct);
        Store.Save(operations: [NewOperation(entry, OperationKind.Delete, parent, entry.Name) with
        { BaselineTree = entry.IsDirectory ? CachedTreeBaseline(id) : null }]); Notify();
    }
    private Operation NewOperation(Entry entry, OperationKind kind, Guid parent, string name) => new()
    {
        AccountId = entry.AccountId, EntryId = entry.Id, Kind = kind, TargetParentId = parent, Name = name,
        BaselineStamp = entry.RemoteStamp, BaselineHash = entry.ContentHash,
        Dependencies = OperationDependencies(entry, kind, parent)
    };
    private List<Guid> OperationDependencies(Entry entry, OperationKind kind, Guid parent)
    {
        var dependencies = Dependencies(entry.AccountId, entry.Id, parent);
        if (!entry.IsDirectory || kind is not (OperationKind.Delete or OperationKind.Move)) return dependencies;
        var descendants = new HashSet<Guid> { entry.Id }; var entries = Store.Entries(entry.AccountId);
        while (true)
        {
            var count = descendants.Count;
            descendants.UnionWith(entries.Where(e => e.ParentId is { } p && descendants.Contains(p)).Select(e => e.Id));
            if (count == descendants.Count) break;
        }
        dependencies.AddRange(Store.Operations(entry.AccountId).Where(o => !o.IsTerminal && descendants.Contains(o.EntryId)).Select(o => o.Id));
        return dependencies.Distinct().ToList();
    }
    private async Task CacheSubtreeAsync(Guid root, CancellationToken ct)
    {
        var visited = new HashSet<Guid>();
        async Task Visit(Guid id)
        {
            if (!visited.Add(id)) throw new IOException("检测到目录循环。");
            await BrowseAsync(id, true, ct);
            foreach (var item in Children(id).Where(e => e.IsDirectory)) await Visit(item.Id);
        }
        await Visit(root);
    }
    public Task PrepareDirectoryAsync(Guid root, CancellationToken ct = default) => CacheSubtreeAsync(root, ct);
    private Dictionary<ulong, string>? CachedTreeBaseline(Guid id)
    {
        var root = RequireEntry(id); var entries = Store.Entries(root.AccountId); var result = new Dictionary<ulong, string>();
        var seen = new HashSet<Guid>();
        bool Visit(Entry parent)
        {
            if (!seen.Add(parent.Id) || parent.LastListed is null) return false;
            foreach (var item in entries.Where(e => e.ParentId == parent.Id && !e.RemoteMissing))
            {
                if (item.RemoteId == 0) return false;
                result[item.RemoteId] = $"{parent.RemoteId}|{item.RemoteStamp}";
                if (item.IsDirectory && !Visit(item)) return false;
            }
            return true;
        }
        return Visit(root) ? result : null;
    }

    public async Task<IReadOnlyList<Guid>> ImportAsync(Guid parentId, IEnumerable<string> paths, CancellationToken ct = default)
    {
        using var foreground = BeginForegroundTransfer();
        await _planning.WaitAsync(ct);
        var created = new List<Guid>();
        try
        {
            var parent = Projected(parentId); CheckAccount(parent);
            async Task Add(Guid targetId, string path)
            {
                ct.ThrowIfCancellationRequested(); LocalFiles.RejectLinks(path);
                var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)); LocalFiles.ValidateCloudName(name);
                if (name.StartsWith("~$", StringComparison.Ordinal) || name.StartsWith("._", StringComparison.Ordinal)
                    || name is ".DS_Store" or ".localized" || (File.GetAttributes(path) & FileAttributes.Temporary) != 0) return;
                var target = Projected(targetId);
                if (!Offline) await BrowseAsync(targetId, false, ct);
                var matches = Children(targetId).Where(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length > 1) throw new ConflictException("云端有多个同名条目，需先核对：" + name);
                var existing = matches.SingleOrDefault();
                if (Directory.Exists(path))
                {
                    if (existing is { IsDirectory: false }) throw new ConflictException("文件夹与已有文件同名：" + name);
                    var directoryId = existing?.Id ?? CreateFolder(targetId, name);
                    foreach (var child in Directory.EnumerateFileSystemEntries(path).Order()) await Add(directoryId, child);
                    return;
                }
                if (existing?.IsDirectory == true) throw new ConflictException("文件与已有目录同名：" + name);
                var snapshot = await _snapshots.CaptureAsync(path, Files.SnapshotDirectory(target.AccountId), ct);
                var entry = existing ?? new Entry { AccountId = target.AccountId, ParentId = targetId, DiskId = target.DiskId, Name = name, Size = snapshot.Length };
                var op = NewOperation(entry, OperationKind.Upload, targetId, existing?.Name ?? name) with
                {
                    SnapshotPath = snapshot.Path, SnapshotHash = snapshot.Hash, SnapshotLength = snapshot.Length,
                    SourcePath = path, Collision = CollisionChoice.Ask,
                    State = existing is null ? OperationState.Queued : OperationState.Blocked,
                    Error = existing is null ? null : "上传目标有同名文件，请选择跳过、覆盖或改名。"
                };
                try { Store.Save(existing is null ? [entry] : [], [op]); }
                catch { File.Delete(snapshot.Path); throw; }
                created.Add(op.Id); Notify();
            }
            foreach (var path in paths) await Add(parentId, Path.GetFullPath(path));
            return created;
        }
        finally { _planning.Release(); }
    }
    public void Cancel(Guid operationId)
    {
        var op = Store.GetOperation(operationId) ?? throw new IOException("任务不存在。");
        if (op.IsTerminal) return;
        if (VerificationPending(op))
        {
            // The upload was already acknowledged and the entry points at the candidate.
            // Cancelling only drops the optional download check; nothing is deleted and
            // an overwrite backup stays in the cloud for the user to inspect.
            lock (_metadataGate)
            {
                CompleteCore(op, RequireEntry(op.EntryId) with { Status = "已上传 · 未下载校验" });
                Remember(op.Id, o => o with { Error = op.OldRemoteId != 0 && op.BackupName is { } backup
                    ? $"已取消下载校验；原文件备份“{backup}”保留在云端。" : "已取消下载校验，云端文件保留。" });
            }
            if (_runningId == operationId) _currentStop?.Cancel();
            DiscardSnapshots(op.AccountId, [op.Id]);
            Notify(); return;
        }
        if (op.State == OperationState.Running || op.MutationStarted)
        {
            if (_runningId == operationId) _currentStop?.Cancel();
            Store.UpdateOperation(op.Id, o => o with { State = OperationState.Uncertain, Error = "已停止；请求可能已生效，请核对或重试恢复。", RetryAfter = DateTimeOffset.MaxValue });
            Notify(); return;
        }
        var cancelled = new HashSet<Guid> { operationId }; var all = Store.Operations(op.AccountId);
        while (true)
        {
            var next = all.Where(o => !o.IsTerminal && !o.MutationStarted && o.Dependencies.Any(cancelled.Contains)).Select(o => o.Id).ToArray();
            var before = cancelled.Count; cancelled.UnionWith(next); if (before == cancelled.Count) break;
        }
        var updates = all.Where(o => cancelled.Contains(o.Id)).Select(o => o with { State = OperationState.Cancelled,
            CancelledBy = o.Id == operationId ? null : operationId, Error = "用户取消，相关本地内容保留。" }).ToArray();
        var newEntries = updates.Select(o => Store.GetEntry(o.EntryId)).OfType<Entry>().Where(e => e.RemoteId == 0).DistinctBy(e => e.Id)
            .Select(e => e with { RemoteMissing = true });
        Store.Save(newEntries, updates);
        DiscardSnapshots(op.AccountId, cancelled); Notify();
    }
    // Cancelled work no longer needs its private copy. Only finished tasks are touched:
    // a request that may already have reached the cloud keeps its snapshot for recovery,
    // and a file still referenced by any unfinished task is left alone. The hash stays on
    // the record so edit scanning still recognises the cancelled content and Retry can
    // recapture an unchanged source.
    private void DiscardSnapshots(Guid account, IEnumerable<Guid> operationIds)
    {
        var ids = operationIds.ToHashSet(); var all = Store.Operations(account);
        var inUse = all.Where(o => !o.IsTerminal && o.SnapshotPath is not null).Select(o => o.SnapshotPath!).ToHashSet(StringComparer.Ordinal);
        foreach (var op in all.Where(o => ids.Contains(o.Id) && o.IsTerminal && o.SnapshotPath is not null))
        {
            var path = op.SnapshotPath!;
            if (inUse.Contains(path) || !Files.Owns(path)) continue;
            try { if (File.Exists(path)) { LocalFiles.RejectLinks(path); File.Delete(path); } }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { Log?.Invoke("取消任务的快照暂未删除：" + e.Message); continue; }
            Store.UpdateOperation(op.Id, o => o.SnapshotPath == path ? o with { SnapshotPath = null } : o);
        }
    }
    private bool VerificationPending(Operation op) => op.Kind == OperationKind.Upload && op.VerificationStamp is not null && op.NewRemoteId != 0
        && (op.State == OperationState.AwaitingVerification
            || op.State == OperationState.Running && _runningId == op.Id && _verifying
            // Older builds parked a cancelled verification here permanently.
            || op.State == OperationState.Uncertain && op.RetryAfter == DateTimeOffset.MaxValue)
        && Store.GetEntry(op.EntryId) is { } entry && entry.RemoteId == op.NewRemoteId && entry.RemoteStamp == op.VerificationStamp;

    /// <summary>Removes a task record. Unfinished tasks are cancelled first; a task whose request
    /// may already have taken effect keeps its record for verification and returns false.</summary>
    public bool DeleteTask(Guid operationId)
    {
        var op = Store.GetOperation(operationId) ?? throw new IOException("任务不存在。");
        if (op.AccountId != ActiveAccount?.Id) throw new InvalidOperationException("请先切换至该任务所属账号。");
        if (!op.IsTerminal) Cancel(operationId);
        if (Store.GetOperation(operationId) is not { IsTerminal: true }) return false;
        RemoveTasks(op.AccountId, [operationId]); return true;
    }
    public int ClearCompleted()
    {
        var account = ActiveAccount?.Id ?? throw new InvalidOperationException("请先选择账号。");
        var ids = Store.Operations(account).Where(o => o.State == OperationState.Completed).Select(o => o.Id).ToArray();
        RemoveTasks(account, ids); return ids.Length;
    }
    private void RemoveTasks(Guid account, IReadOnlyCollection<Guid> ids)
    {
        if (ids.Count == 0) return;
        lock (_metadataGate)
        {
            var all = Store.Operations(account); var removed = ids.ToHashSet();
            var targets = all.Where(o => removed.Contains(o.Id)).ToArray();
            if (targets.Any(o => !o.IsTerminal)) throw new InvalidOperationException("任务尚未结束，不能删除记录。");
            var cancelled = targets.Where(o => o.State == OperationState.Cancelled).Select(o => o.Id).ToHashSet();
            if (all.Any(o => !o.IsTerminal && o.Dependencies.Any(cancelled.Contains)))
                throw new InvalidOperationException("仍有未结束的任务依赖这项已取消的任务，请先处理后续任务。");
            // A completed dependency is satisfied; drop the reference so dependents keep running.
            var rewritten = all.Where(o => !removed.Contains(o.Id) && o.Dependencies.Any(removed.Contains))
                .Select(o => o with { Dependencies = o.Dependencies.Where(d => !removed.Contains(d)).ToList() }).ToArray();
            Store.RemoveOperations(removed, rewritten);
        }
        Notify();
    }
    public async Task RetryAsync(Guid operationId, CancellationToken ct = default)
    {
        var op = Store.GetOperation(operationId) ?? throw new IOException("任务不存在。");
        if (op.State == OperationState.Completed) return;
        var restore = Store.Operations(op.AccountId).Where(o => o.Id == operationId || o.State == OperationState.Cancelled && o.CancelledBy == operationId)
            .Select(o => o with { State = o.MutationStarted ? OperationState.Uncertain : OperationState.Queued, RetryAfter = null, Error = null, CancelledBy = null }).ToArray();
        var captured = new List<string>();
        try
        {
            for (var i = 0; i < restore.Length; i++)
            {
                var item = restore[i];
                if (item.Kind != OperationKind.Upload || item.SnapshotPath is not null || item.SnapshotHash is null) continue;
                // The snapshot was discarded on cancel; only an unchanged source may stand in for it.
                if (item.SourcePath is not { } source || !File.Exists(source))
                    throw new IOException($"“{item.Name}”的快照已在取消时删除，源文件也已不在，请重新上传。");
                var snapshot = await _snapshots.CaptureAsync(source, Files.SnapshotDirectory(item.AccountId), ct);
                captured.Add(snapshot.Path);
                if (snapshot.Hash != item.SnapshotHash || snapshot.Length != item.SnapshotLength)
                    throw new IOException($"“{item.Name}”在取消后已被修改，原快照已删除，请重新上传。");
                restore[i] = item with { SnapshotPath = snapshot.Path };
            }
            Store.Save(entries: restore.Select(o => Store.GetEntry(o.EntryId)).OfType<Entry>().Where(e => e.RemoteId == 0).DistinctBy(e => e.Id).Select(e => e with { RemoteMissing = false }), operations: restore);
        }
        catch
        {
            foreach (var path in captured) try { File.Delete(path); } catch (IOException) { }
            throw;
        }
        Notify();
    }
    public async Task ResolveAsync(Guid operationId, CollisionChoice choice, string? newName = null, CancellationToken ct = default)
    {
        var op = Store.GetOperation(operationId) ?? throw new IOException("任务不存在。");
        if (op.State == OperationState.Running || op.MutationStarted) throw new IOException("该事务可能已执行，必须先通过重试核对结果，不能当作新的上传。");
        if (choice == CollisionChoice.Skip) { Cancel(operationId); return; }
        var entry = RequireEntry(op.EntryId); CheckAccount(entry);
        if (choice == CollisionChoice.Rename)
        {
            if (op.Kind != OperationKind.Upload) throw new IOException("仅上传任务可以另存云端副本。");
            var parent = op.TargetParentId ?? entry.ParentId!.Value;
            newName ??= UniqueName(op.Name, Children(parent).Select(e => e.Name)); LocalFiles.ValidateCloudName(newName);
            if (Children(parent).Any(e => e.Name.Equals(newName, StringComparison.OrdinalIgnoreCase))) throw new IOException("副本名称已存在。");
            var copy = new Entry { AccountId = entry.AccountId, ParentId = parent, DiskId = entry.DiskId, Name = newName, Size = op.SnapshotLength };
            if (op.SourcePath == entry.LocalPath)
            {
                copy = copy with { LocalPath = entry.LocalPath, Dirty = true, ContentHash = entry.ContentHash };
                Store.UpdateEntry(entry.Id, e => e with { LocalPath = null, Dirty = false, ContentHash = null });
            }
            Store.Save([copy], [op with { EntryId = copy.Id, Name = newName, State = OperationState.Queued, Error = null,
                BaselineHash = null, BaselineStamp = "", Collision = CollisionChoice.Rename }]); Notify(); return;
        }
        var rawParent = RequireEntry(op.TargetParentId ?? entry.ParentId!.Value);
        await BrowseAsync(rawParent.Id, true, ct);
        entry = RequireEntry(entry.Id);
        if (op.Kind == OperationKind.Upload && (entry.RemoteId == 0 || entry.RemoteMissing))
        {
            var candidate = Store.Entries(op.AccountId).SingleOrDefault(e => e.ParentId == rawParent.Id && !e.RemoteMissing && !e.IsDirectory && e.RemoteId != 0 && e.Name == op.Name);
            if (candidate is not null && candidate.Id != entry.Id)
            {
                if (candidate.Dirty || Store.Operations(op.AccountId).Any(o => o.EntryId == candidate.Id && !o.IsTerminal))
                    throw new ConflictException("新的云端版本已有独立编辑或待办，请先处理该副本。");
                entry = entry with { RemoteId = candidate.RemoteId, RemoteStamp = candidate.RemoteStamp, RemoteMissing = false, Size = candidate.Size, UploadTime = candidate.UploadTime };
                Store.Save(entries: [entry, candidate with { RemoteId = 0, RemoteMissing = true }]);
            }
            else if (entry.RemoteMissing) throw new ConflictException("云端没有可明确覆盖的同名文件；可选择另存副本。");
        }
        else if (entry.RemoteMissing) throw new ConflictException("原条目已不存在，不能继续此操作。");
        string? hash = null;
        if (op.Kind == OperationKind.Upload && entry.RemoteId != 0)
            hash = (await WithCloudAsync(entry.AccountId, (c, t) => c.HashAsync(entry, t), ct)).Hash;
        if (entry.IsDirectory && op.Kind is OperationKind.Delete or OperationKind.Move) await CacheSubtreeAsync(entry.Id, ct);
        Store.Save(operations: [op with { EntryId = entry.Id, State = OperationState.Queued, Error = null, RetryAfter = null,
            BaselineStamp = entry.RemoteStamp, BaselineHash = hash ?? op.BaselineHash, Collision = CollisionChoice.Replace,
            BaselineTree = entry.IsDirectory ? CachedTreeBaseline(entry.Id) : op.BaselineTree }]); Notify();
    }
    public static string UniqueName(string name, IEnumerable<string> names)
    {
        var existing = names.ToHashSet(StringComparer.OrdinalIgnoreCase); var stem = Path.GetFileNameWithoutExtension(name); var extension = Path.GetExtension(name);
        for (var i = 1; i < 10000; i++)
        { var candidate = $"{stem[..Math.Min(stem.Length, 220)]} ({i}){extension}"; if (!existing.Contains(candidate)) return candidate; }
        throw new IOException("无法生成不重复的名称。");
    }
}
