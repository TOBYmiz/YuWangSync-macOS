using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace YuWangSync.Engine;

public sealed class RunLog : IDisposable
{
    private const int RetainedRuns = 10;
    private readonly object _gate = new();
    private readonly StreamWriter _writer;
    private bool _disposed;
    public string FilePath { get; }

    public RunLog(string dataRoot)
    {
        var directory = Path.Combine(dataRoot, "logs");
        Directory.CreateDirectory(directory);
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff", CultureInfo.InvariantCulture);
        FilePath = Path.Combine(directory, $"application-{timestamp}-{Guid.NewGuid():N}.log");
        _writer = new StreamWriter(new FileStream(FilePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false))
        { AutoFlush = true };
        Write($"运行开始 pid={Environment.ProcessId}");

        // Order by launch time in the name, never by the last write to an older run.
        // The current run always occupies one of the ten slots.
        try
        {
            foreach (var path in Directory.GetFiles(directory, "application-*.log")
                .Where(path => path != FilePath && Regex.IsMatch(Path.GetFileName(path),
                    @"\Aapplication-[0-9]{8}-[0-9]{6}-[0-9]{7}-[0-9a-f]{32}\.log\z"))
                .OrderByDescending(Path.GetFileName, StringComparer.Ordinal).Skip(RetainedRuns - 1))
                DeleteOldLog(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Write("历史日志清理暂未完成：" + error.Message); }

        // These two aggregate files belonged to the previous size-based rotation.
        DeleteOldLog(Path.Combine(dataRoot, "application.log"));
        DeleteOldLog(Path.Combine(dataRoot, "application.log.previous"));
    }

    private void DeleteOldLog(string path)
    {
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Write("历史日志暂未删除：" + error.Message); }
    }

    public void Write(string message)
    {
        lock (_gate)
        {
            if (_disposed) return;
            try { _writer.WriteLine($"{DateTimeOffset.Now:O} {message.Replace('\r', ' ').Replace('\n', ' ')}"); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { Console.Error.WriteLine("运行日志写入失败：" + error.Message); }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            try { Write("运行结束"); }
            finally { _disposed = true; _writer.Dispose(); }
        }
    }
}
