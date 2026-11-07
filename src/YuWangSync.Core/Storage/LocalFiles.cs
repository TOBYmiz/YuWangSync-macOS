using System.Security.Cryptography;
using System.Text;

namespace YuWangSync.Core.Storage;

public sealed class LocalFiles
{
    public string DataRoot { get; }
    public string CacheRoot { get; }
    public LocalFiles(string? dataRoot = null, string? cacheRoot = null)
    {
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        DataRoot = Path.GetFullPath(dataRoot ?? (OperatingSystem.IsMacOS()
            ? Path.Combine(user, "Library", "Application Support", "YuWangSync")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YuWangSync-Mac-Development")));
        CacheRoot = Path.GetFullPath(cacheRoot ?? (OperatingSystem.IsMacOS()
            ? Path.Combine(user, "Library", "Caches", "YuWangSync") : Path.Combine(DataRoot, "cache")));
        Directory.CreateDirectory(DataRoot); Directory.CreateDirectory(CacheRoot);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(DataRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(CacheRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
    public string DatabasePath => Path.Combine(DataRoot, "metadata.db");
    public string SnapshotDirectory(Guid account) => Path.Combine(DataRoot, "snapshots", account.ToString("N"));
    // Opened documents live durably here: they may acquire unsaved/offline edits later.
    public string WorkingPath(Entry entry) => Path.Combine(DataRoot, "working", entry.AccountId.ToString("N"), entry.Id.ToString("N"), SafeName(entry.Name));
    public static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', '\0', ':']).ToHashSet();
        var result = new string(name.Normalize().Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).TrimEnd(' ', '.');
        if (string.IsNullOrWhiteSpace(result) || result is "." or "..") result = "file";
        if (OperatingSystem.IsWindows() && System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileNameWithoutExtension(result), @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) result = "_" + result;
        if (Encoding.UTF8.GetByteCount(result) <= 240) return result;
        static string Fit(string value, int budget)
        {
            var output = new StringBuilder(); var bytes = 0;
            foreach (var rune in value.EnumerateRunes()) { if (bytes + rune.Utf8SequenceLength > budget) break; output.Append(rune.ToString()); bytes += rune.Utf8SequenceLength; }
            return output.ToString();
        }
        var extension = Fit(Path.GetExtension(result), 40);
        return Fit(Path.GetFileNameWithoutExtension(result), 240 - Encoding.UTF8.GetByteCount(extension)) + extension;
    }
    public static void ValidateCloudName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.Length > 255 || name.Any(c => c is '/' or '\\' or '\0' || char.IsControl(c)))
            throw new ArgumentException("名称不能为空、超过 255 字符或包含路径分隔符和控制字符。");
    }
    public static void RejectLinks(string path)
    {
        var info = new FileInfo(Path.GetFullPath(path));
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || info.LinkTarget is not null)
            throw new IOException("不跟随符号链接或目录链接传输文件。");
        for (var parent = info.Directory; parent is not null; parent = parent.Parent)
            if (parent.LinkTarget is not null || (parent.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                // macOS standard /var and /tmp aliases precede its per-user temporary directory.
                if (OperatingSystem.IsMacOS() && parent.FullName is "/var" or "/tmp"
                    && parent.ResolveLinkTarget(true)?.FullName == "/private" + parent.FullName) continue;
                throw new IOException("传输路径包含目录链接。");
            }
    }
    public bool Owns(string path) => Within(path, DataRoot) || Within(path, CacheRoot);
    public static bool Within(string path, string root) => Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    public static async Task<string> HashAsync(string path, CancellationToken ct = default)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 128 * 1024, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, ct));
    }
}

public class FileSnapshotProvider : IFileSnapshotProvider
{
    protected virtual Task CopyAsync(string source, string destination, CancellationToken ct) => CopyManagedAsync(source, destination, ct);
    private static async Task CopyManagedAsync(string source, string destination, CancellationToken ct)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await input.CopyToAsync(output, ct); await output.FlushAsync(ct); output.Flush(true);
    }
    public async Task<FileSnapshot> CaptureAsync(string source, string snapshotDirectory, CancellationToken ct = default)
    {
        LocalFiles.RejectLinks(source);
        Directory.CreateDirectory(snapshotDirectory);
        var target = Path.Combine(snapshotDirectory, Guid.NewGuid().ToString("N") + ".snapshot");
        var temporary = target + ".part";
        try
        {
            var before = new FileInfo(source); var length = before.Length; var written = before.LastWriteTimeUtc;
            await CopyAsync(source, temporary, ct);
            var hash = await LocalFiles.HashAsync(temporary, ct);
            var sourceHash = await LocalFiles.HashAsync(source, ct);
            var after = new FileInfo(source);
            if (after.Length != length || after.LastWriteTimeUtc != written || hash != sourceHash)
                throw new IOException("文件仍在变化，稍后重新读取快照。");
            File.Move(temporary, target);
            return new(target, hash, length);
        }
        catch { if (File.Exists(temporary)) File.Delete(temporary); throw; }
    }
}
