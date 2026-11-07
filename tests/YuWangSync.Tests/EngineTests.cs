using System.Diagnostics;
using System.Text.Json;
using Xunit;
using YuWangSync.Core.Storage;
using YuWangSync.Engine;

namespace YuWangSync.Tests;

public sealed class EngineTests
{
    [Fact] public async Task Engine_state_contract_uses_camel_case_and_synthetic_accounts()
    {
        var root = Path.Combine(Path.GetTempPath(), "YuWangSync-engine-state-" + Guid.NewGuid().ToString("N"));
        var files = new LocalFiles(root, Path.Combine(root, "cache"));
        await using (var engine = new EngineHost(files, true))
        {
            await engine.InitializeAsync(); var json = JsonSerializer.SerializeToElement(engine.State(), EngineHost.Json);
            Assert.True(json.GetProperty("demo").GetBoolean()); Assert.Equal("演示账号", json.GetProperty("activeAccount").GetProperty("displayName").GetString());
            Assert.NotEmpty(json.GetProperty("entries").EnumerateArray());
            Assert.DoesNotContain("password", json.GetRawText(), StringComparison.OrdinalIgnoreCase);
        }
        Directory.Delete(root, true);
    }
    [Fact] public async Task Anonymous_pipe_commands_persist_offline_work_and_shutdown_cleanly()
    {
        var root = Path.Combine(Path.GetTempPath(), "YuWangSync-engine-pipe-" + Guid.NewGuid().ToString("N"));
        var executable = typeof(Program).Assembly.Location; var testBase = AppContext.BaseDirectory;
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "exec", "--runtimeconfig", Path.Combine(testBase, "YuWangSync.Tests.runtimeconfig.json"), "--depsfile", Path.Combine(testBase, "YuWangSync.Tests.deps.json"), executable, "--demo", "--data-root", root }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!; var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        long id = 0;
        async Task<JsonElement> Request(string method, object parameters)
        {
            var requestId = ++id;
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { id = requestId, method, @params = parameters })); await process.StandardInput.FlushAsync();
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(deadline.Token) ?? throw new IOException("Engine EOF: " + await stderr);
                using var message = JsonDocument.Parse(line);
                if (!message.RootElement.TryGetProperty("id", out var responseId) || responseId.GetInt64() != requestId) continue;
                Assert.False(message.RootElement.TryGetProperty("error", out _), message.RootElement.ToString());
                return message.RootElement.GetProperty("result").Clone();
            }
        }
        try
        {
            var state = await Request("state", new { }); var folderId = state.GetProperty("folderId").GetGuid();
            await Request("offline", new { value = true });
            var created = await Request("createFolder", new { parentId = folderId, name = "离线管道测试" }); var entry = created.GetProperty("entryId").GetGuid();
            state = await Request("state", new { });
            Assert.Contains(state.GetProperty("entries").EnumerateArray(), e => e.GetProperty("id").GetGuid() == entry);
            await Request("shutdown", new { }); await process.WaitForExitAsync(deadline.Token);
            Assert.Equal(0, process.ExitCode);
            using var store = new OperationStore(Path.Combine(root, "metadata.db")); Assert.Single(store.Operations());
            Assert.Equal(YuWangSync.Core.OperationState.Queued, store.Operations()[0].State);
        }
        finally
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken); Directory.Delete(root, true);
        }
    }
}
