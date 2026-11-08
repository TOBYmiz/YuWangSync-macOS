using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using YuWangSync.Core;
using YuWangSync.Core.Storage;

namespace YuWangSync.Engine.Platform;

public sealed class MemorySecretStore : ISecretStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _values = new();
    public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult(_values.GetValueOrDefault(key));
    public Task SetAsync(string key, string value, CancellationToken ct = default) { _values[key] = value; return Task.CompletedTask; }
    public Task DeleteAsync(string key, CancellationToken ct = default) { _values.TryRemove(key, out _); return Task.CompletedTask; }
}

public sealed class MacSecretStore : ISecretStore
{
    public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        var pointer = Native.SecretGet(key, out var status);
        if (status == -25300) return null;
        if (status != 0) throw new IOException($"钥匙串读取失败（{status}），请检查钥匙串访问权限。");
        try { return Marshal.PtrToStringUTF8(pointer); } finally { Native.Free(pointer); }
    }, ct);
    public Task SetAsync(string key, string value, CancellationToken ct = default) => Task.Run(() =>
    { ct.ThrowIfCancellationRequested(); var status = Native.SecretSet(key, value); if (status != 0) throw new IOException($"钥匙串保存失败（{status}）。"); }, ct);
    public Task DeleteAsync(string key, CancellationToken ct = default) => Task.Run(() =>
    { ct.ThrowIfCancellationRequested(); var status = Native.SecretDelete(key); if (status is not (0 or -25300)) throw new IOException($"钥匙串删除失败（{status}）。"); }, ct);
}

// The Windows build is a development harness. DPAPI keeps test/server credentials out of JSON.
public sealed class WindowsSecretStore(string directory) : ISecretStore
{
    private string PathFor(string key) => Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".secret");
    public Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); var path = PathFor(key);
        return Task.FromResult(File.Exists(path) ? Encoding.UTF8.GetString(Protect(File.ReadAllBytes(path), false)) : null);
    }
    public Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); Directory.CreateDirectory(directory);
        var path = PathFor(key); var temp = path + ".tmp";
        File.WriteAllBytes(temp, Protect(Encoding.UTF8.GetBytes(value), true)); File.Move(temp, path, true); return Task.CompletedTask;
    }
    public Task DeleteAsync(string key, CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); File.Delete(PathFor(key)); return Task.CompletedTask; }
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
    private static byte[] Protect(byte[] bytes, bool encrypt)
    {
        var input = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            Blob output;
            var ok = encrypt ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new IOException("Windows 开发环境的凭据保护失败。");
            try { var data = new byte[output.Size]; Marshal.Copy(output.Data, data, 0, data.Length); return data; }
            finally { LocalFree(output.Data); }
        }
        finally { Marshal.FreeHGlobal(input.Data); CryptographicOperations.ZeroMemory(bytes); }
    }
}

public sealed class MacSnapshotProvider : FileSnapshotProvider
{
    protected override async Task CopyAsync(string source, string destination, CancellationToken ct)
    {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var status = Native.CopySnapshot(source, destination);
            if (status != 0) throw new IOException($"协调读取文件失败（{status}），稍后重试。");
        }, ct);
        ct.ThrowIfCancellationRequested();
        using var file = new FileStream(destination, FileMode.Open, FileAccess.Write, FileShare.None); file.Flush(true);
    }
}

public sealed class DesktopIntegration : IDesktopIntegration
{
    public void OpenFile(string path)
    {
        path = Path.GetFullPath(path);
        if (OperatingSystem.IsMacOS()) Start("/usr/bin/open", path);
        else Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
    public void RevealFile(string path)
    {
        path = Path.GetFullPath(path);
        if (OperatingSystem.IsMacOS()) Start("/usr/bin/open", "-R", path);
        else if (OperatingSystem.IsWindows()) Start("explorer.exe", "/select,", path);
        else Start("xdg-open", Path.GetDirectoryName(path)!);
    }
    private static void Start(string command, params string[] arguments)
    {
        var start = new ProcessStartInfo(command) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        Process.Start(start)?.Dispose();
    }
}

internal static class Native
{
    private const string Library = "libYuWangMac.dylib";
    [DllImport(Library, EntryPoint = "yw_secret_get", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr SecretGet([MarshalAs(UnmanagedType.LPUTF8Str)] string key, out int status);
    [DllImport(Library, EntryPoint = "yw_secret_set", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SecretSet([MarshalAs(UnmanagedType.LPUTF8Str)] string key, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport(Library, EntryPoint = "yw_secret_delete", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SecretDelete([MarshalAs(UnmanagedType.LPUTF8Str)] string key);
    [DllImport(Library, EntryPoint = "yw_free", CallingConvention = CallingConvention.Cdecl)] internal static extern void Free(IntPtr pointer);
    [DllImport(Library, EntryPoint = "yw_keychain_disable_ui", CallingConvention = CallingConvention.Cdecl)] internal static extern int DisableKeychainUI();
    [DllImport(Library, EntryPoint = "yw_secret_legacy_probe", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int LegacySecretProbe([MarshalAs(UnmanagedType.LPUTF8Str)] string key, [MarshalAs(UnmanagedType.LPUTF8Str)] string? value);
    [DllImport(Library, EntryPoint = "yw_snapshot_copy", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int CopySnapshot([MarshalAs(UnmanagedType.LPUTF8Str)] string source, [MarshalAs(UnmanagedType.LPUTF8Str)] string destination);
}
