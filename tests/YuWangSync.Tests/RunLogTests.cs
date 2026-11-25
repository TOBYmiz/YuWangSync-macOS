using Xunit;
using YuWangSync.Core.Storage;
using YuWangSync.Engine;

namespace YuWangSync.Tests;

public sealed class RunLogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "YuWangSync-run-log-" + Guid.NewGuid().ToString("N"));

    [Fact] public void Consecutive_runs_keep_only_the_latest_ten_and_never_append_to_previous_runs()
    {
        var paths = new List<string>();
        for (var run = 0; run < 14; run++)
        {
            using var log = new RunLog(_root);
            paths.Add(log.FilePath);
            log.Write($"run={run}");
            Assert.Contains($"run={run}", ReadActiveLog(log.FilePath)); // Flushed before exit/crash.
            Assert.Equal(Math.Min(run + 1, 10), Directory.GetFiles(Path.Combine(_root, "logs")).Length);
        }
        Assert.Equal(paths.Skip(4).Order(), Directory.GetFiles(Path.Combine(_root, "logs")).Order());
        foreach (var path in paths.Skip(4))
        {
            var lines = File.ReadAllLines(path);
            Assert.Equal(3, lines.Length);
            Assert.Contains("运行开始", lines[0]); Assert.Contains("运行结束", lines[2]);
        }
    }

    [Fact] public void Pruning_uses_launch_order_and_removes_only_managed_logs()
    {
        var directory = Path.Combine(_root, "logs"); Directory.CreateDirectory(directory);
        var paths = Enumerable.Range(1, 12).Select(day => Path.Combine(directory,
            $"application-202001{day:00}-000000-0000000-{Guid.NewGuid():N}.log")).ToArray();
        foreach (var path in paths) File.WriteAllText(path, "previous run");
        File.SetLastWriteTimeUtc(paths[0], DateTime.UtcNow.AddDays(1));
        var unrelated = Path.Combine(directory, "notes.log"); File.WriteAllText(unrelated, "keep");
        var similarName = Path.Combine(directory, "application-notes.log"); File.WriteAllText(similarName, "keep too");
        File.WriteAllText(Path.Combine(_root, "application.log"), "old aggregate");
        File.WriteAllText(Path.Combine(_root, "application.log.previous"), "old rotated aggregate");
        var data = Path.Combine(_root, "metadata.db"); File.WriteAllText(data, "keep database");

        using var log = new RunLog(_root);
        Assert.Equal(paths.Skip(3).Append(log.FilePath).Append(similarName).Order(), Directory.GetFiles(directory, "application-*.log").Order());
        Assert.Equal("keep", File.ReadAllText(unrelated)); Assert.Equal("keep database", File.ReadAllText(data));
        Assert.Equal("keep too", File.ReadAllText(similarName));
        Assert.False(File.Exists(Path.Combine(_root, "application.log")));
        Assert.False(File.Exists(Path.Combine(_root, "application.log.previous")));
    }

    [Fact] public void Concurrent_messages_remain_whole_and_large_runs_stay_in_one_file()
    {
        using (var log = new RunLog(_root))
        {
            Parallel.For(0, 200, index => log.Write($"message={index}\r\ncontinuation"));
            log.Write(new string('x', 4 * 1024 * 1024 + 1));
            log.Write("after size threshold");
        }
        var path = Assert.Single(Directory.GetFiles(Path.Combine(_root, "logs")));
        var lines = File.ReadAllLines(path);
        Assert.Equal(204, lines.Length);
        Assert.Equal(200, lines.Count(line => line.EndsWith("  continuation", StringComparison.Ordinal)));
        for (var index = 0; index < 200; index++) Assert.Single(lines, line => line.EndsWith($" message={index}  continuation", StringComparison.Ordinal));
        Assert.Contains("after size threshold", lines[^2]);
    }

    [Fact] public async Task Engine_creates_a_log_even_without_protocol_messages_and_closes_it_on_shutdown()
    {
        var files = new LocalFiles(_root, Path.Combine(_root, "cache"));
        await using (var engine = new EngineHost(files, true))
        {
            var activePath = Assert.Single(Directory.GetFiles(Path.Combine(_root, "logs")));
            Assert.Contains("运行开始", ReadActiveLog(activePath));
        }
        var path = Assert.Single(Directory.GetFiles(Path.Combine(_root, "logs")));
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        using var reader = new StreamReader(exclusive);
        Assert.Contains("运行结束", await reader.ReadToEndAsync());
    }

    private static string ReadActiveLog(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(file);
        return reader.ReadToEnd();
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
