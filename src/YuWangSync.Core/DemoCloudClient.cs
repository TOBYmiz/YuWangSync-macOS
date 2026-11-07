using System.Security.Cryptography;
using System.Text;
using YuWangCloudSync.Models;
using YuWangCloudSync.Net;

namespace YuWangSync.Core;

/// <summary>Explicit --demo mode. All operations stay in memory, without network requests.</summary>
public class DemoCloudClient : ICloudClient
{
    public sealed record Item(ulong Id, ulong Parent, string Name, bool Directory, byte[] Data, DateTime Modified);
    public Dictionary<ulong, Item> Items { get; } = [];
    private ulong _next = 100;
    public DemoCloudClient()
    {
        Items[1] = new(1, 0, "使用说明.txt", false, Encoding.UTF8.GetBytes("欢迎使用育网云 macOS 演示。\n此模式仅使用合成数据，不连接真实服务器。\n可以尝试目录操作、下载和编辑回传。\n"), DateTime.UtcNow);
        Items[2] = new(2, 0, "资料", true, [], DateTime.UtcNow);
        Items[3] = new(3, 2, "示例.txt", false, Encoding.UTF8.GetBytes("示例内容\n"), DateTime.UtcNow);
    }
    public string? SessionId => "synthetic-demo-session";
    public void SetSession(string? session) { }
    public Task<bool> CheckSessionAsync(CancellationToken ct) => Task.FromResult(true);
    public Task<byte[]> CaptchaAsync(CancellationToken ct) => Task.FromResult(Array.Empty<byte>());
    public Task<bool> LoginAsync(string code, CancellationToken ct) => Task.FromResult(true);
    public Task<IReadOnlyList<DiskTreeNode>> TreeAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<DiskTreeNode>>([
        new() { Id = 10, NodeType = "container", Name = "共享资源", ParentId = 0 },
        new() { Id = 7, NodeType = "disk", Name = "演示共享盘", ParentId = 10 }]);
    public virtual Task<FolderListingResult> ListAsync(ulong disk, ulong folder, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); return Task.FromResult(new FolderListingResult(Items.Values.Where(i => i.Parent == folder).Select(Remote).ToArray(), DirectoryPermission.FullAccess, true)); }
    public static YuWangItem Remote(Item i) => new() { Id = i.Id, Name = i.Name, Type = i.Directory ? 'D' : 'F', Size = i.Directory ? null : i.Data.LongLength, UploadTime = i.Modified };
    public virtual async Task<DownloadReceipt> DownloadAsync(Entry entry, Stream output, Action<long, long>? progress, CancellationToken ct)
    {
        var bytes = Items[entry.RemoteId].Data; await output.WriteAsync(bytes, ct); progress?.Invoke(bytes.Length, bytes.Length);
        return new(Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length);
    }
    public virtual Task<DownloadReceipt> HashAsync(ulong id, CancellationToken ct)
    { var bytes = Items[id].Data; return Task.FromResult(new DownloadReceipt(Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length)); }
    public virtual Task<YuWangItem> CreateFolderAsync(ulong disk, ulong parent, string name, ulong existingId, Action<ulong> rememberId, Action<ulong[]> beforeCreate, CancellationToken ct)
    {
        if (existingId != 0) return Task.FromResult(Remote(Items[existingId]));
        beforeCreate(Items.Values.Where(i => i.Parent == parent).Select(i => i.Id).ToArray());
        var item = new Item(_next++, parent, name, true, [], DateTime.UtcNow); Items[item.Id] = item; rememberId(item.Id); return Task.FromResult(Remote(item));
    }
    public virtual async Task<YuWangItem> UploadAsync(ulong disk, ulong parent, Operation op, Stream snapshot, Action<ulong> rememberId, Action<UploadAttempt> rememberAttempt, Action<UploadProgress>? progress, CancellationToken ct)
    {
        if (op.NewRemoteId != 0 && Items.TryGetValue(op.NewRemoteId, out var recovered)) return Remote(recovered);
        var previous = Items.Values.Where(i => i.Parent == parent).Select(i => i.Id).ToArray();
        rememberAttempt(new(previous, true));
        using var stream = new MemoryStream(); await snapshot.CopyToAsync(stream, ct); var data = stream.ToArray();
        progress?.Invoke(new(UploadStage.Uploading, data.Length, data.Length, data.Length));
        var item = new Item(_next++, parent, op.Name, false, data, DateTime.UtcNow); Items[item.Id] = item;
        rememberAttempt(new(previous, true, item.Id)); rememberId(item.Id);
        progress?.Invoke(new(UploadStage.Verifying, data.Length, data.Length, data.Length, data.Length));
        if (op.OldRemoteId != 0) Items.Remove(op.OldRemoteId);
        return Remote(item);
    }
    public virtual Task<bool> RenameAsync(ulong disk, ulong parent, ulong id, bool directory, string name, ulong? targetParent, CancellationToken ct)
    { Items[id] = Items[id] with { Name = name, Parent = targetParent ?? parent }; return Task.FromResult(true); }
    public virtual Task<bool> DeleteAsync(ulong disk, ulong parent, ulong id, bool directory, CancellationToken ct)
    {
        var ids = new HashSet<ulong> { id };
        while (true) { var n = ids.Count; ids.UnionWith(Items.Values.Where(i => ids.Contains(i.Parent)).Select(i => i.Id)); if (n == ids.Count) break; }
        foreach (var item in ids) Items.Remove(item); return Task.FromResult(true);
    }
    public void Dispose() { }
}
