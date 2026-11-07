using System.Security.Cryptography;
using System.Text;
using Xunit;
using YuWangCloudSync.Models;
using YuWangCloudSync.Net;
using YuWangSync.Core;
using YuWangSync.Core.Storage;
using YuWangSync.Engine.Platform;

namespace YuWangSync.Tests;

public sealed class TestCloud : DemoCloudClient
{
    public int UploadCalls, DeleteCalls, ListCalls;
    public bool LoseReply, InvalidListing;
    public DirectoryPermission Permission = DirectoryPermission.FullAccess;
    public Func<Task>? DuringUpload;
    public override async Task<FolderListingResult> ListAsync(ulong disk, ulong folder, CancellationToken ct)
    { ListCalls++; var result = await base.ListAsync(disk, folder, ct); return result with { IsValidPage = !InvalidListing, Permission = Permission }; }
    public override async Task<YuWangItem> UploadAsync(ulong disk, ulong parent, Operation op, Stream snapshot, Action<ulong> rememberId, Action<UploadAttempt> rememberAttempt, Action<UploadProgress>? progress, CancellationToken ct)
    {
        if (op.NewRemoteId == 0) UploadCalls++;
        var result = await base.UploadAsync(disk, parent, op, snapshot, rememberId, rememberAttempt, progress, ct);
        if (DuringUpload is { } action) await action();
        if (LoseReply) { LoseReply = false; throw new HttpRequestException("synthetic lost reply"); }
        return result;
    }
    public override Task<bool> DeleteAsync(ulong disk, ulong parent, ulong id, bool directory, CancellationToken ct)
    { DeleteCalls++; return base.DeleteAsync(disk, parent, id, directory, ct); }
}

public sealed class Fixture : IAsyncDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "YuWangSync-test-" + Guid.NewGuid().ToString("N"));
    public Workspace Workspace { get; private set; } = null!;
    public TestCloud Cloud { get; } = new();
    public Account Account { get; } = new(Guid.NewGuid(), "https://example.invalid", "test-user");
    public LocalFiles Files { get; private set; } = null!;
    public Guid Disk { get; private set; }
    public static async Task<Fixture> CreateAsync()
    {
        var fixture = new Fixture(); fixture.Files = new(Path.Combine(fixture.Root, "data"), Path.Combine(fixture.Root, "cache"));
        await fixture.OpenAsync(); await fixture.Workspace.RefreshTreeAsync();
        fixture.Disk = fixture.Workspace.Store.Entries(fixture.Account.Id).Single(e => e.IsDiskRoot).Id;
        await fixture.Workspace.BrowseAsync(fixture.Disk, true); return fixture;
    }
    private async Task OpenAsync()
    {
        Workspace = new(new OperationStore(Files.DatabasePath), Files, new MemorySecretStore(), new FileSnapshotProvider(), (_, _) => Cloud);
        await Workspace.CompleteLoginAsync(Account, "synthetic", Cloud);
    }
    public async Task ReopenAsync() { await Workspace.DisposeAsync(); await OpenAsync(); }
    public string File(string name, string content)
    { var path = Path.Combine(Root, "input", name); Directory.CreateDirectory(Path.GetDirectoryName(path)!); System.IO.File.WriteAllText(path, content); return path; }
    public Entry Remote(ulong id) => Workspace.Store.Entries(Account.Id).Single(e => e.RemoteId == id && !e.IsDiskRoot);
    public async Task DrainAsync()
    { for (var i = 0; i < 80 && await Workspace.RunOnceAsync(); i++) { } }
    public async ValueTask DisposeAsync()
    {
        await Workspace.DisposeAsync();
        if (!Path.GetFullPath(Root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.Ordinal)) throw new IOException("Unsafe test cleanup");
        Directory.Delete(Root, true);
    }
}

public sealed class WorkspaceTests
{
    [Fact] public async Task Offline_create_child_upload_rename_resolves_temporary_ids()
    {
        await using var f = await Fixture.CreateAsync(); var w = f.Workspace; w.SetManualOffline(true);
        var parent = w.CreateFolder(f.Disk, "parent"); var child = w.CreateFolder(parent, "child");
        await w.ImportAsync(child, [f.File("hello.txt", "hello")]); w.Rename(parent, "renamed");
        Assert.Equal("renamed", w.ProjectedEntries(f.Account.Id).Single(e => e.Id == parent).Name);
        Assert.False(await w.RunOnceAsync()); w.SetManualOffline(false); await w.ReconnectAsync(); await f.DrainAsync();
        Assert.All(w.Store.Operations(), o => Assert.Equal(OperationState.Completed, o.State));
        var childRemote = w.Store.GetEntry(child)!.RemoteId;
        Assert.Contains(f.Cloud.Items.Values, i => i.Parent == childRemote && i.Name == "hello.txt" && Encoding.UTF8.GetString(i.Data) == "hello");
    }
    [Fact] public async Task Directory_import_merges_existing_folder_and_summarizes_file_collisions()
    {
        await using var f = await Fixture.CreateAsync(); var w = f.Workspace;
        var existingFolder = f.Remote(2); await w.BrowseAsync(existingFolder.Id, true);
        var input = f.File("资料/示例.txt", "replacement"); f.File("资料/new.txt", "new");
        var ids = await w.ImportAsync(f.Disk, [Path.GetDirectoryName(input)!]);
        Assert.Equal(2, ids.Count); Assert.DoesNotContain(w.Store.Operations(), o => o.Kind == OperationKind.CreateFolder);
        var collision = w.Store.Operations().Single(o => o.State == OperationState.Blocked);
        await w.ResolveAsync(collision.Id, CollisionChoice.Rename, "示例副本.txt"); await f.DrainAsync();
        Assert.Contains(f.Cloud.Items.Values, i => i.Name == "示例.txt" && Encoding.UTF8.GetString(i.Data) == "示例内容\n");
        Assert.Contains(f.Cloud.Items.Values, i => i.Parent == 2 && i.Name == "示例副本.txt");
    }
    [Fact] public async Task Same_name_file_does_not_overwrite_without_explicit_resolution()
    {
        await using var f = await Fixture.CreateAsync(); var w = f.Workspace;
        var ids = await w.ImportAsync(f.Disk, [f.File("使用说明.txt", "new content")]); await f.DrainAsync();
        Assert.Equal(0, f.Cloud.UploadCalls); Assert.Equal(OperationState.Blocked, w.Store.GetOperation(ids[0])!.State);
        await w.ResolveAsync(ids[0], CollisionChoice.Replace); await f.DrainAsync();
        Assert.Equal(OperationState.Completed, w.Store.GetOperation(ids[0])!.State);
        Assert.Contains(f.Cloud.Items.Values, i => i.Name == "使用说明.txt" && Encoding.UTF8.GetString(i.Data) == "new content");
    }
    [Fact] public async Task Same_metadata_but_changed_cloud_bytes_blocks_edit_writeback()
    {
        await using var f = await Fixture.CreateAsync(); var w = f.Workspace; var entry = f.Remote(1);
        var local = await w.OpenAsync(entry.Id); await System.IO.File.WriteAllTextAsync(local, "my edit");
        await w.ScanEditsAsync(); await Task.Delay(2100); await w.ScanEditsAsync();
        var old = f.Cloud.Items[1]; f.Cloud.Items[1] = old with { Data = Enumerable.Repeat((byte)'x', old.Data.Length).ToArray() };
        await f.DrainAsync();
        Assert.Equal(0, f.Cloud.UploadCalls); Assert.Equal(OperationState.Blocked, Assert.Single(w.Store.Operations()).State);
        Assert.Equal("my edit", await System.IO.File.ReadAllTextAsync(local));
    }
    [Fact] public async Task Rename_followed_by_edit_uses_own_completed_version_as_baseline()
    {
        await using var f = await Fixture.CreateAsync(); var w = f.Workspace; var entry = f.Remote(1);
        var local = await w.OpenAsync(entry.Id); w.Rename(entry.Id, "renamed.txt");
        await System.IO.File.WriteAllTextAsync(local, "edited"); await w.ScanEditsAsync(); await Task.Delay(2100); await w.ScanEditsAsync();
        await f.DrainAsync(); Assert.All(w.Store.Operations(), o => Assert.Equal(OperationState.Completed, o.State));
        Assert.Contains(f.Cloud.Items.Values, i => i.Name == "renamed.txt" && Encoding.UTF8.GetString(i.Data) == "edited");
    }
    [Fact] public async Task Lost_upload_response_reopens_receipt_and_does_not_upload_again()
    {
        await using var f = await Fixture.CreateAsync(); var ids = await f.Workspace.ImportAsync(f.Disk, [f.File("new.txt", "once")]);
        f.Cloud.LoseReply = true; await f.Workspace.RunOnceAsync();
        Assert.Equal(OperationState.Uncertain, f.Workspace.Store.GetOperation(ids[0])!.State);
        Assert.NotEqual(0ul, f.Workspace.Store.GetOperation(ids[0])!.NewRemoteId);
        await f.ReopenAsync(); f.Workspace.Retry(ids[0]); await f.DrainAsync();
        Assert.Equal(1, f.Cloud.UploadCalls); Assert.Equal(OperationState.Completed, f.Workspace.Store.GetOperation(ids[0])!.State);
    }
    [Theory] [InlineData(DirectoryPermission.ReadOnly)] [InlineData(DirectoryPermission.Unknown)]
    public async Task Unconfirmed_permission_never_uploads(DirectoryPermission permission)
    {
        await using var f = await Fixture.CreateAsync(); var ids = await f.Workspace.ImportAsync(f.Disk, [f.File("new.txt", "keep")]);
        f.Cloud.Permission = permission; await f.Workspace.RunOnceAsync();
        var op = f.Workspace.Store.GetOperation(ids[0])!;
        Assert.Equal(OperationState.Blocked, op.State); Assert.False(op.MutationStarted); Assert.Equal(0, f.Cloud.UploadCalls); Assert.True(System.IO.File.Exists(op.SnapshotPath));
    }
    [Fact] public async Task Invalid_listing_cannot_erase_metadata()
    {
        await using var f = await Fixture.CreateAsync(); var before = f.Workspace.Children(f.Disk).Select(i => i.Id).ToArray();
        f.Cloud.InvalidListing = true;
        await Assert.ThrowsAsync<IOException>(() => f.Workspace.BrowseAsync(f.Disk, true));
        Assert.Equal(before, f.Workspace.Children(f.Disk).Select(i => i.Id));
    }
    [Fact] public async Task Explicit_refresh_bypasses_cache()
    {
        await using var f = await Fixture.CreateAsync(); var count = f.Cloud.ListCalls;
        await f.Workspace.BrowseAsync(f.Disk); Assert.Equal(count, f.Cloud.ListCalls);
        await f.Workspace.BrowseAsync(f.Disk, true); Assert.Equal(count + 1, f.Cloud.ListCalls);
    }
    [Fact] public async Task Offline_delete_with_unknown_descendants_requires_manual_recheck()
    {
        await using var f = await Fixture.CreateAsync(); var w = f.Workspace; var folder = f.Remote(2);
        w.SetManualOffline(true); await w.DeleteAsync(folder.Id); w.SetManualOffline(false); await w.ReconnectAsync(); await f.DrainAsync();
        var op = Assert.Single(w.Store.Operations()); Assert.Equal(OperationState.Blocked, op.State); Assert.Equal(0, f.Cloud.DeleteCalls);
        await w.ResolveAsync(op.Id, CollisionChoice.Replace); await f.DrainAsync();
        Assert.Equal(OperationState.Completed, w.Store.GetOperation(op.Id)!.State); Assert.DoesNotContain(2ul, f.Cloud.Items.Keys);
    }
    [Fact] public async Task Remote_child_added_after_delete_plan_stops_directory_delete()
    {
        await using var f = await Fixture.CreateAsync(); var w = f.Workspace; var folder = f.Remote(2);
        await w.DeleteAsync(folder.Id); f.Cloud.Items[99] = new(99, 2, "new child.txt", false, [1], DateTime.UtcNow);
        await f.DrainAsync(); Assert.Equal(OperationState.Blocked, Assert.Single(w.Store.Operations()).State); Assert.Equal(0, f.Cloud.DeleteCalls);
    }
    [Fact] public async Task Cancelling_unsent_parent_cancels_descendants_without_cloud_writes()
    {
        await using var f = await Fixture.CreateAsync(); var w = f.Workspace;
        var folder = w.CreateFolder(f.Disk, "pending"); await w.ImportAsync(folder, [f.File("x.txt", "x")]);
        var creator = w.Store.Operations().Single(o => o.Kind == OperationKind.CreateFolder); w.Cancel(creator.Id);
        Assert.All(w.Store.Operations(), o => Assert.Equal(OperationState.Cancelled, o.State));
        await f.DrainAsync(); Assert.Equal(0, f.Cloud.UploadCalls); Assert.DoesNotContain(w.Children(f.Disk), i => i.Id == folder);
    }
    [Fact] public async Task Account_switch_does_not_send_another_accounts_queue()
    {
        await using var f = await Fixture.CreateAsync(); await f.Workspace.ImportAsync(f.Disk, [f.File("x.txt", "x")]);
        var other = new Account(Guid.NewGuid(), "https://other.invalid", "test-user");
        await f.Workspace.CompleteLoginAsync(other, "unused", new TestCloud());
        Assert.False(await f.Workspace.RunOnceAsync()); Assert.Equal(0, f.Cloud.UploadCalls);
        Assert.Empty(f.Workspace.Store.Entries(other.Id));
    }
    [Fact] public async Task Pending_task_blocks_account_removal_without_breaking_active_session()
    {
        await using var f = await Fixture.CreateAsync(); await f.Workspace.ImportAsync(f.Disk, [f.File("x.txt", "x")]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Workspace.RemoveAccountAsync(f.Account));
        await f.DrainAsync(); Assert.Equal(OperationState.Completed, Assert.Single(f.Workspace.Store.Operations()).State);
    }
    [Fact] public async Task Cleanup_rechecks_bytes_and_preserves_unobserved_edits()
    {
        await using var f = await Fixture.CreateAsync(); var entry = f.Remote(1);
        var path = await f.Workspace.EnsureLocalAsync(entry.Id); await System.IO.File.WriteAllTextAsync(path, "unobserved edit");
        Assert.Equal(0, await f.Workspace.CleanupAsync(true)); Assert.True(System.IO.File.Exists(path)); Assert.True(f.Workspace.Store.GetEntry(entry.Id)!.Dirty);
    }
    [Fact] public async Task Cleanup_skips_currently_open_and_pending_files()
    {
        await using var f = await Fixture.CreateAsync(); var entry = f.Remote(1); var path = await f.Workspace.OpenAsync(entry.Id);
        Assert.Equal(0, await f.Workspace.CleanupAsync(true)); Assert.True(System.IO.File.Exists(path));
    }
    [Fact] public async Task Recursive_download_preserves_offline_files_without_network_tracking()
    {
        await using var f = await Fixture.CreateAsync(); var folder = f.Remote(2);
        Assert.Empty(await f.Workspace.DownloadRecursiveAsync([folder.Id])); var child = f.Remote(3);
        Assert.True(child.KeepOffline); f.Workspace.SetManualOffline(true);
        Assert.Equal("示例内容\n", await System.IO.File.ReadAllTextAsync(await f.Workspace.EnsureLocalAsync(child.Id)));
        Assert.Equal(0, await f.Workspace.CleanupAsync(false));
    }
    [Fact] public async Task Export_does_not_overwrite_an_existing_file_without_permission()
    {
        await using var f = await Fixture.CreateAsync(); var path = f.File("export.txt", "precious");
        await Assert.ThrowsAsync<IOException>(() => f.Workspace.ExportAsync(f.Remote(1).Id, path));
        Assert.Equal("precious", await System.IO.File.ReadAllTextAsync(path));
    }
    [Fact] public async Task Move_into_descendant_is_rejected()
    {
        await using var f = await Fixture.CreateAsync(); var parent = f.Workspace.CreateFolder(f.Disk, "p"); var child = f.Workspace.CreateFolder(parent, "c");
        Assert.Throws<IOException>(() => f.Workspace.Move(parent, child));
    }
    [Fact] public async Task Crash_recovers_running_operation_as_uncertain()
    {
        await using var f = await Fixture.CreateAsync(); var folder = f.Workspace.CreateFolder(f.Disk, "p"); var op = f.Workspace.Store.Operations().Single();
        f.Workspace.Store.UpdateOperation(op.Id, o => o with { State = OperationState.Running, MutationStarted = true, BeforeIds = [1, 2] });
        await f.ReopenAsync(); Assert.Equal(OperationState.Uncertain, f.Workspace.Store.GetOperation(op.Id)!.State);
        await f.Workspace.RunOnceAsync(); Assert.Equal(2, f.Cloud.Items.Count(i => i.Value.Parent == 0));
        Assert.Equal(0ul, f.Workspace.Store.GetEntry(folder)!.RemoteId);
    }
    [Fact] public async Task Snapshot_rejects_concurrent_source_changes()
    {
        await using var f = await Fixture.CreateAsync(); var file = f.File("source.txt", "original");
        await Assert.ThrowsAsync<IOException>(() => new MutatingSnapshot().CaptureAsync(file, f.Files.SnapshotDirectory(f.Account.Id)));
        Assert.Empty(Directory.GetFiles(f.Files.SnapshotDirectory(f.Account.Id)));
    }
    private sealed class MutatingSnapshot : FileSnapshotProvider
    {
        protected override async Task CopyAsync(string source, string destination, CancellationToken ct)
        { await base.CopyAsync(source, destination, ct); await System.IO.File.WriteAllTextAsync(source, "changed", ct); }
    }
}
