using YuWangCloudSync.Models;
using YuWangCloudSync.Net;

namespace YuWangSync.Core;

public interface ICloudClient : IDisposable
{
    string? SessionId { get; }
    void SetSession(string? session);
    Task<bool> CheckSessionAsync(CancellationToken ct);
    Task<byte[]> CaptchaAsync(CancellationToken ct);
    Task<bool> LoginAsync(string code, CancellationToken ct);
    Task<IReadOnlyList<DiskTreeNode>> TreeAsync(CancellationToken ct);
    Task<FolderListingResult> ListAsync(ulong disk, ulong folder, CancellationToken ct);
    Task<FolderListingResult> ListWithPermissionAsync(ulong disk, ulong folder, CancellationToken ct)
        => ListAsync(disk, folder, ct);
    async Task<DirectoryPermission> ProbePermissionAsync(ulong disk, ulong folder, CancellationToken ct)
        => (await ListWithPermissionAsync(disk, folder, ct)).Permission;
    Task<long?> SizeAsync(Entry entry, CancellationToken ct);
    Task<DownloadReceipt> DownloadAsync(Entry entry, Stream output, Action<long, long>? progress, CancellationToken ct);
    Task<DownloadReceipt> HashAsync(Entry entry, CancellationToken ct);
    Task<YuWangItem> CreateFolderAsync(ulong disk, ulong parent, string name, ulong existingId,
        Action<ulong> rememberId, Action<ulong[]> beforeCreate, CancellationToken ct);
    Task<YuWangItem> UploadAsync(ulong disk, ulong parent, Operation operation, Stream snapshot,
        Action<ulong> rememberId, Action<UploadAttempt> rememberAttempt, Action<UploadProgress>? progress, CancellationToken ct);
    Task<bool> RenameAsync(ulong disk, ulong parent, ulong id, bool directory, string name, ulong? targetParent, CancellationToken ct);
    Task<bool> DeleteAsync(ulong disk, ulong parent, ulong id, bool directory, CancellationToken ct);
}

public sealed class CloudClient : ICloudClient
{
    private readonly YuWangClient _client;
    public CloudClient(Account account, string password, Action<string>? log = null, HttpMessageHandler? handler = null)
        => _client = new(account.Server, account.Username, password, log, httpHandler: handler);
    public string? SessionId => _client.SessionId;
    public void SetSession(string? session) => _client.SetSession(session);
    public Task<bool> CheckSessionAsync(CancellationToken ct) => _client.InCallAsync(ct, () => _client.CheckSessionAsync());
    public Task<byte[]> CaptchaAsync(CancellationToken ct) => _client.InCallAsync(ct, _client.RefreshLoginCaptchaAsync);
    public Task<bool> LoginAsync(string code, CancellationToken ct) => _client.InCallAsync(ct, () => _client.SubmitLoginCaptchaAsync(code));
    public async Task<IReadOnlyList<DiskTreeNode>> TreeAsync(CancellationToken ct)
    {
        var nodes = await _client.InCallAsync(ct, () => _client.FetchDiskTreeAsync(0));
        if (!_client.IsLoggedIn) throw new SessionExpiredException();
        return nodes;
    }
    public async Task<FolderListingResult> ListAsync(ulong disk, ulong folder, CancellationToken ct)
    {
        var result = await _client.InCallAsync(ct, () => _client.ListFolderWithPermissionsAsync(disk, folder, fresh: true, probePermission: false));
        if (!result.IsValidPage) throw new IOException("云端返回的目录不完整或登录已过期，保留缓存并停止写入。");
        return result;
    }
    public async Task<FolderListingResult> ListWithPermissionAsync(ulong disk, ulong folder, CancellationToken ct)
    {
        var result = await _client.InCallAsync(ct, () => _client.ListFolderWithPermissionsAsync(disk, folder, fresh: true, probePermission: true));
        if (!result.IsValidPage) throw new IOException("云端返回的目录不完整或登录已过期，保留缓存并停止写入。");
        return result;
    }
    public Task<DirectoryPermission> ProbePermissionAsync(ulong disk, ulong folder, CancellationToken ct)
        => _client.InCallAsync(ct, () => _client.ProbeDirectoryPermissionAsync(disk, folder));
    public Task<DownloadReceipt> DownloadAsync(Entry entry, Stream output, Action<long, long>? progress, CancellationToken ct)
        => _client.DownloadToAsync(entry.RemoteId, entry.Name, entry.DeclaredSize ?? entry.Size, output, progress, ct);
    public async Task<long?> SizeAsync(Entry entry, CancellationToken ct)
        => (await _client.InCallAsync(ct, () => _client.ProbeRealSizeAsync(entry.RemoteId, entry.Name, entry.DeclaredSize ?? entry.Size)))?.Size;
    public Task<DownloadReceipt> HashAsync(Entry entry, CancellationToken ct) => DownloadAsync(entry, Stream.Null, null, ct);
    public Task<YuWangItem> CreateFolderAsync(ulong disk, ulong parent, string name, ulong existingId,
        Action<ulong> rememberId, Action<ulong[]> beforeCreate, CancellationToken ct)
        => _client.InCallAsync(ct, () => _client.CreateRemoteFolderAsync(disk, parent, name, ct, existingId, rememberId, beforeCreate));
    public Task<YuWangItem> UploadAsync(ulong disk, ulong parent, Operation op, Stream snapshot,
        Action<ulong> rememberId, Action<UploadAttempt> rememberAttempt, Action<UploadProgress>? progress, CancellationToken ct)
    {
        _client.DeferPostUploadVerification = !op.RunVerification;
        return _client.InCallAsync(ct, () => op.OldRemoteId != 0
            ? _client.ReplaceRemoteFileAsync(disk, parent, op.OldRemoteId, op.Name, op.BackupName!, snapshot, op.Created.UtcDateTime,
                Convert.FromHexString(op.SnapshotHash!), rememberId, ct, progress, op.UploadAttempt, rememberAttempt)
            : _client.UploadNewFileAsync(disk, parent, op.Name, snapshot, op.Created.UtcDateTime,
                Convert.FromHexString(op.SnapshotHash!), ct, progress, op.UploadAttempt, rememberAttempt));
    }
    public Task<bool> RenameAsync(ulong disk, ulong parent, ulong id, bool directory, string name, ulong? targetParent, CancellationToken ct)
        => _client.InCallAsync(ct, () => _client.RenameRemoteItemAsync(disk, parent, id, directory, targetParent ?? parent, name));
    public Task<bool> DeleteAsync(ulong disk, ulong parent, ulong id, bool directory, CancellationToken ct)
        => _client.InCallAsync(ct, () => _client.DeleteRemoteItemAsync(disk, parent, id, directory));
    public void Dispose() => _client.Dispose();
}
