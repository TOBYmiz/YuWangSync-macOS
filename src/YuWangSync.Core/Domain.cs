using YuWangCloudSync.Models;
using YuWangCloudSync.Net;

namespace YuWangSync.Core;

public sealed record Account(Guid Id, string Server, string Username)
{
    public string DisplayName => Username.Length == 11 && Username.All(char.IsDigit)
        ? Username[..3] + "****" + Username[^4..] : Username;
    public override string ToString() => DisplayName + " · " + new Uri(Server).Host;
}

public sealed record Entry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid AccountId { get; init; }
    public Guid? ParentId { get; init; }
    public ulong DiskId { get; init; }
    public ulong RemoteId { get; init; }
    public string Name { get; init; } = "";
    public bool IsDirectory { get; init; }
    public bool IsDiskRoot { get; init; }
    public bool RemoteMissing { get; init; }
    public long? Size { get; init; }
    public DateTime? UploadTime { get; init; }
    public string RemoteStamp { get; init; } = "";
    public string? LocalPath { get; init; }
    public string? ContentHash { get; init; }
    public string? DownloadStamp { get; init; }
    public bool KeepOffline { get; init; }
    public bool Dirty { get; init; }
    public DateTimeOffset LastAccess { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastListed { get; init; }
    public DirectoryPermission Permission { get; init; }
    public string Status { get; init; } = "";
    public string SizeLabel => IsDirectory ? "—" : Size is { } s ? FormatBytes(s) : "未知";
    public static string FormatBytes(long size) => size >= 1073741824 ? $"{size / 1073741824d:F2} GB"
        : size >= 1048576 ? $"{size / 1048576d:F1} MB" : size >= 1024 ? $"{size / 1024d:F1} KB" : $"{size} B";
    public override string ToString() => Name;
    public static string Stamp(YuWangItem item) => $"{item.Id}|{item.Name}|{item.Type}|{item.Size}|{item.UploadTime:O}";
}

public enum OperationKind { CreateFolder, Upload, Rename, Move, Delete }
public enum OperationState { Queued, Running, Uncertain, Blocked, Failed, Completed, Cancelled }
public enum CollisionChoice { Ask, Skip, Replace, Rename }

public sealed record Operation
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid AccountId { get; init; }
    public Guid EntryId { get; init; }
    public OperationKind Kind { get; init; }
    public Guid? TargetParentId { get; init; }
    public string Name { get; init; } = "";
    public OperationState State { get; init; }
    public List<Guid> Dependencies { get; init; } = [];
    public string BaselineStamp { get; init; } = "";
    public string? BaselineHash { get; init; }
    public Dictionary<ulong, string>? BaselineTree { get; init; }
    public string? SnapshotPath { get; init; }
    public string? SnapshotHash { get; init; }
    public long SnapshotLength { get; init; }
    public string? SourcePath { get; init; }
    public CollisionChoice Collision { get; init; }
    public bool Force { get; init; }
    public bool MutationStarted { get; init; }
    public ulong OldRemoteId { get; init; }
    public ulong NewRemoteId { get; init; }
    public ulong[]? BeforeIds { get; init; }
    public string? BackupName { get; init; }
    public UploadAttempt? UploadAttempt { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset Created { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RetryAfter { get; init; }
    public Guid? CancelledBy { get; init; }
    public bool IsTerminal => State is OperationState.Completed or OperationState.Cancelled;
    public string StateLabel => State switch
    {
        OperationState.Queued => "等待同步", OperationState.Running => "处理中",
        OperationState.Uncertain => "结果待核对", OperationState.Blocked => "需要处理",
        OperationState.Failed => "失败", OperationState.Completed => "完成", _ => "已取消"
    };
    public string KindLabel => Kind switch { OperationKind.CreateFolder => "新建目录", OperationKind.Upload => "上传",
        OperationKind.Rename => "重命名", OperationKind.Move => "移动", _ => "删除" };
}

public sealed record TransferProgress(Guid? OperationId, string Name, string Stage, long Completed, long Total)
{
    public double Percent => Total <= 0 ? 0 : Math.Clamp(Completed * 100d / Total, 0, 100);
    public override string ToString() => $"{Name} · {Stage} · {Entry.FormatBytes(Completed)} / {Entry.FormatBytes(Total)}";
}
public sealed record FileSnapshot(string Path, string Hash, long Length);
public sealed record DownloadReceipt(string Hash, long Length);
public sealed record ImportCollision(Guid EntryId, string LocalPath, string CloudPath);
public sealed class ConflictException(string message) : IOException(message);
public sealed class UncertainException(string message) : IOException(message);
public sealed class SessionExpiredException() : IOException("登录已失效，请重新登录；待办内容已保留。");

public interface ISecretStore
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task SetAsync(string key, string value, CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default);
}
public interface IFileSnapshotProvider
{
    Task<FileSnapshot> CaptureAsync(string source, string snapshotDirectory, CancellationToken ct = default);
}
public interface IDesktopIntegration
{
    void OpenFile(string path);
    void RevealFile(string path);
}
