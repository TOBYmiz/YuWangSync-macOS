using System.Security.Cryptography;
using System.Text.Json;
using Xunit;
using YuWangCloudSync.Net;
using YuWangSync.Core;
using YuWangSync.Core.Storage;
using YuWangSync.Engine;

namespace YuWangSync.Tests;

public sealed class InteractionTests
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task Directory_preload_builds_unvisited_branches_and_skips_unavailable_folders()
    {
        await using var f = await Fixture.CreateAsync();
        f.Cloud.Items[90] = new(90, 2, "nested", true, [], DateTime.UtcNow);
        f.Cloud.Items[91] = new(91, 90, "deeper", true, [], DateTime.UtcNow);
        f.Cloud.Items[92] = new(92, 0, "unavailable", true, [], DateTime.UtcNow);
        f.Cloud.FailingFolder = 92;
        await f.Workspace.BrowseAsync(f.Disk, true);
        var sizeCalls = f.Cloud.SizeCalls;
        await f.Workspace.PreloadDirectoriesAsync();
        Assert.NotNull(f.Remote(2).LastListed);
        Assert.NotNull(f.Remote(90).LastListed);
        Assert.NotNull(f.Remote(91).LastListed);
        Assert.Equal(f.Remote(90).Id, f.Remote(91).ParentId);
        Assert.Null(f.Remote(92).LastListed);
        Assert.Equal(sizeCalls, f.Cloud.SizeCalls); // Building the tree does not probe every file in the account.
    }

    [Fact]
    public async Task Precise_size_survives_refresh_and_restart_but_invalidates_with_remote_version()
    {
        await using var f = await Fixture.CreateAsync();
        f.Cloud.DeclaredSizeOverride = 0;
        await f.Workspace.BrowseAsync(f.Disk, true);
        var entry = f.Remote(1); var stamp = entry.RemoteStamp; var calls = f.Cloud.SizeCalls;
        Assert.Equal(0, entry.DeclaredSize);
        Assert.Equal(f.Cloud.Items[1].Data.LongLength, entry.Size);
        Assert.Equal(entry.RemoteStamp, entry.SizeStamp);
        Assert.DoesNotContain("约", entry.SizeLabel);
        await f.Workspace.BrowseAsync(f.Disk, true);
        Assert.Equal(calls, f.Cloud.SizeCalls);
        Assert.Equal(stamp, f.Remote(1).RemoteStamp); // Size correction must not change the edit/writeback baseline.
        await f.ReopenAsync(); await f.Workspace.BrowseAsync(f.Disk);
        Assert.Equal(calls, f.Cloud.SizeCalls);
        f.Cloud.Items[1] = f.Cloud.Items[1] with { Data = [1, 2, 3], Modified = f.Cloud.Items[1].Modified.AddSeconds(1) };
        await f.Workspace.BrowseAsync(f.Disk, true);
        Assert.Equal(3, f.Remote(1).Size);
        Assert.True(f.Cloud.SizeCalls > calls);
        Assert.NotEqual(stamp, f.Remote(1).RemoteStamp);
    }

    [Fact]
    public async Task Upload_does_not_block_browsing_download_connection_checks_or_queueing()
    {
        await using var f = await Fixture.CreateAsync(); var w = f.Workspace;
        var ids = await w.ImportAsync(f.Disk, [f.File("parallel.txt", "upload data")]);
        var started = Signal(); var release = Signal();
        f.Cloud.DuringUpload = () => { started.TrySetResult(); return release.Task; };
        var upload = w.RunOnceAsync();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAll(w.BrowseAsync(f.Disk, true), w.ReconnectAsync(), w.RefreshTreeAsync(), w.EnsureLocalAsync(f.Remote(1).Id))
                .WaitAsync(TimeSpan.FromSeconds(5));
            w.CreateFolder(f.Disk, "queued while uploading");
            Assert.False(upload.IsCompleted);
            Assert.False(w.Offline);
            Assert.Single(w.Children(f.Disk), e => e.Name == "parallel.txt");
            var second = w.RunOnceAsync();
            Assert.False(second.IsCompleted); // Writes still execute one at a time.
            release.TrySetResult(); await Task.WhenAll(upload, second).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(OperationState.Completed, w.Store.GetOperation(ids.Single())!.State);
            Assert.Single(w.Children(f.Disk), e => e.Name == "parallel.txt");
        }
        finally { release.TrySetResult(); await upload; }
    }

    [Fact]
    public async Task Account_switch_cancels_upload_and_keeps_its_recovery_record_in_original_account()
    {
        await using var f = await Fixture.CreateAsync(); var w = f.Workspace;
        var ids = await w.ImportAsync(f.Disk, [f.File("switch.txt", "unsynced")]);
        var started = Signal(); var release = Signal();
        f.Cloud.DuringUpload = () => { started.TrySetResult(); return release.Task; };
        var upload = w.RunOnceAsync();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var other = new Account(Guid.NewGuid(), "https://other.invalid", "other");
            await w.CompleteLoginAsync(other, "synthetic", new TestCloud()).WaitAsync(TimeSpan.FromSeconds(5));
            await upload.WaitAsync(TimeSpan.FromSeconds(5));
            var operation = w.Store.GetOperation(ids.Single())!;
            Assert.Equal(OperationState.Uncertain, operation.State);
            Assert.Equal(f.Account.Id, operation.AccountId);
            Assert.True(File.Exists(operation.SnapshotPath));
            Assert.Equal(other.Id, w.ActiveAccount!.Id);
            Assert.False(w.Offline);
        }
        finally { release.TrySetResult(); await upload; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Protocol_allows_navigation_during_transfer_and_verification_but_serializes_mutations(bool verifying)
    {
        using var web = new WebServer();
        using var client = new YuWangClient("https://example.invalid", "synthetic", "", _ => { }, httpHandler: web);
        var started = Signal(); var release = Signal();
        Func<Task> hold = () => { started.TrySetResult(); return release.Task; };
        if (verifying) web.BeforeDownloadReply = hold; else web.BeforeChunkReply = hold;
        byte[] data = [1, 2, 3, 4]; using var source = new MemoryStream(data);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var upload = client.UploadNewFileAsync(7, 10, "parallel.bin", source, DateTime.UtcNow, SHA256.HashData(data), cancel.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var list = await client.ListFolderWithPermissionsAsync(7, 20, true).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(list.IsValidPage);
            Assert.True(await client.CheckSessionAsync().WaitAsync(TimeSpan.FromSeconds(3)));
            var create = client.CreateRemoteFolderAsync(7, 20, "parallel folder", cancel.Token);
            Assert.False(create.IsCompleted);
            Assert.False(upload.IsCompleted);
            release.TrySetResult();
            var file = await upload; var folder = await create;
            Assert.Equal(10ul, web.Items[file.Id].Folder);
            Assert.Equal(20ul, web.Items[folder.Id].Folder);
            Assert.Equal(data, web.Items[file.Id].Bytes);
        }
        finally { release.TrySetResult(); await upload; }
    }

    [Fact]
    public async Task Expanding_a_directory_loads_children_without_changing_current_folder()
    {
        var root = Path.Combine(Path.GetTempPath(), "YuWangSync-expand-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var engine = new EngineHost(new LocalFiles(root, Path.Combine(root, "cache")), true);
            await engine.InitializeAsync();
            var before = JsonSerializer.SerializeToElement(engine.State(), EngineHost.Json);
            var folder = before.GetProperty("entries").EnumerateArray().Single(e => e.GetProperty("name").GetString() == "资料").GetProperty("id").GetGuid();
            await engine.DispatchAsync("loadFolder", JsonSerializer.SerializeToElement(new { folderId = folder }));
            var after = JsonSerializer.SerializeToElement(engine.State(), EngineHost.Json);
            Assert.Equal(before.GetProperty("folderId").GetGuid(), after.GetProperty("folderId").GetGuid());
            Assert.NotEqual(JsonValueKind.Null, after.GetProperty("entries").EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == folder).GetProperty("lastListed").ValueKind);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Browse_reply_leaves_state_to_the_state_event()
    {
        var root = Path.Combine(Path.GetTempPath(), "YuWangSync-browse-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var engine = new EngineHost(new LocalFiles(root, Path.Combine(root, "cache")), true);
            await engine.InitializeAsync();
            var before = JsonSerializer.SerializeToElement(engine.State(), EngineHost.Json);
            var folder = before.GetProperty("entries").EnumerateArray().Single(e => e.GetProperty("name").GetString() == "资料").GetProperty("id").GetGuid();
            var reply = JsonSerializer.SerializeToElement(await engine.DispatchAsync("browse", JsonSerializer.SerializeToElement(new { folderId = folder })), EngineHost.Json);
            // Navigation replies stay small; a full snapshot here doubled client-side parsing.
            Assert.False(reply.TryGetProperty("entries", out _));
            Assert.Equal(folder, JsonSerializer.SerializeToElement(engine.State(), EngineHost.Json).GetProperty("folderId").GetGuid());
        }
        finally { Directory.Delete(root, true); }
    }
}
