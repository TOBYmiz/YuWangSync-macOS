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
    public async Task Current_directory_relists_while_other_branches_use_persisted_entries()
    {
        await using var f = await Fixture.CreateAsync();
        f.Cloud.Items[90] = new(90, 2, "nested", true, [], DateTime.UtcNow);
        f.Cloud.Items[91] = new(91, 90, "deeper", true, [], DateTime.UtcNow);
        var folder = f.Remote(2);
        Assert.Null(folder.LastListed);
        var calls = f.Cloud.ListCalls;
        await f.Workspace.BrowseAsync(folder.Id, probeSizes: false, onlyIfUnlisted: true);
        Assert.Equal(calls + 1, f.Cloud.ListCalls);
        var nested = f.Remote(90);
        Assert.NotNull(f.Workspace.Store.GetEntry(folder.Id)!.LastListed);
        Assert.Null(nested.LastListed);
        Assert.DoesNotContain(f.Workspace.Store.Entries(f.Account.Id), entry => entry.RemoteId == 91);

        calls = f.Cloud.ListCalls;
        await f.Workspace.BrowseAsync(folder.Id, force: true, probeSizes: false);
        Assert.Equal(calls + 1, f.Cloud.ListCalls);
        Assert.Null(nested.LastListed);

        await f.Workspace.BrowseAsync(nested.Id, probeSizes: false, onlyIfUnlisted: true);
        calls = f.Cloud.ListCalls;
        var deeper = f.Remote(91);
        Assert.Equal(nested.Id, deeper.ParentId);
        Assert.NotNull(f.Workspace.Store.GetEntry(nested.Id)!.LastListed);
        await f.Workspace.BrowseAsync(nested.Id, probeSizes: false, onlyIfUnlisted: true);
        Assert.Equal(calls, f.Cloud.ListCalls);
        await f.ReopenAsync();
        Assert.NotNull(f.Workspace.Store.GetEntry(folder.Id)!.LastListed);
        Assert.NotNull(f.Workspace.Store.GetEntry(nested.Id)!.LastListed);
        Assert.Equal(nested.Id, f.Remote(91).ParentId);
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

    [Fact]
    public void Entry_cache_matches_the_database_across_updates_and_reopen()
    {
        var root = Path.Combine(Path.GetTempPath(), "YuWangSync-cache-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "data", "store.db");
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        var first = new Entry { AccountId = a, Name = "first", IsDirectory = true };
        var moved = new Entry { AccountId = a, Name = "moved" };
        try
        {
            using (var store = new OperationStore(path))
            {
                store.SaveAccount(new Account(a, "https://example.invalid", "a")); store.SaveAccount(new Account(b, "https://example.invalid", "b"));
                store.Save(entries: [first, moved]);
                store.UpdateEntry(first.Id, e => e with { Name = "renamed" });
                store.UpdateEntries([new Entry { Id = Guid.NewGuid(), AccountId = b, Name = "inserted" }], [(moved.Id, e => e with { AccountId = b })]);
                Assert.Equal(["renamed"], store.Entries(a).Select(e => e.Name));
                Assert.Equal(["inserted", "moved"], store.Entries(b).Select(e => e.Name).Order());
                // A failed write must not leave a phantom in the cache.
                Assert.ThrowsAny<Exception>(() => store.Save(entries: [new Entry { AccountId = a, Name = "ghost" }, null!]));
                Assert.Equal(["renamed"], store.Entries(a).Select(e => e.Name));
                store.RemoveAccount(b);
                Assert.Empty(store.Entries(b)); Assert.Null(store.GetEntry(moved.Id));
            }
            using (var reopened = new OperationStore(path))
            {
                Assert.Equal(["renamed"], reopened.Entries(a).Select(e => e.Name));
                Assert.Equal("renamed", reopened.GetEntry(first.Id)!.Name);
                Assert.Empty(reopened.Entries(b));
            }
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Filtered_projection_matches_the_full_projection()
    {
        await using var f = await Fixture.CreateAsync(); var w = f.Workspace; w.SetManualOffline(true);
        var folder = f.Remote(2); var file = f.Remote(1);
        var created = w.CreateFolder(f.Disk, "新目录");
        w.Move(file.Id, folder.Id); w.Rename(folder.Id, "改名");
        await w.DeleteAsync(created);
        var full = w.ProjectedEntries(f.Account.Id);
        foreach (var parent in new[] { f.Disk, folder.Id })
            Assert.Equal(full.Where(e => e.ParentId == parent), w.Children(parent));
        Assert.Equal(full.Where(e => e.IsDirectory), w.ProjectedEntries(f.Account.Id, e => e.IsDirectory));
        Assert.Contains(w.Children(folder.Id), e => e.Id == file.Id);
        Assert.DoesNotContain(w.Children(f.Disk), e => e.Id == created || e.Id == file.Id);
    }
}
