using Xunit;
using YuWangSync.Core;

namespace YuWangSync.Tests;

public sealed class VerificationTests
{
    [Fact]
    public async Task Defaults_and_secondary_setting_persist_across_restart()
    {
        await using var f = await Fixture.CreateAsync();
        Assert.True(f.Workspace.Settings.VerifyAfterUpload);
        Assert.False(f.Workspace.Settings.VerifyOnExpensiveNetwork);
        f.Workspace.SetSettings(new() { VerifyAfterUpload = false, VerifyOnExpensiveNetwork = true });
        await f.ReopenAsync();
        Assert.False(f.Workspace.Settings.VerifyAfterUpload);
        Assert.True(f.Workspace.Settings.VerifyOnExpensiveNetwork);
    }

    [Theory]
    [InlineData(NetworkCost.Unknown, false, false)]
    [InlineData(NetworkCost.Expensive, false, false)]
    [InlineData(NetworkCost.Expensive, true, true)]
    [InlineData(NetworkCost.Unmetered, false, true)]
    public async Task Verification_waits_for_allowed_network(NetworkCost cost, bool expensiveAllowed, bool runs)
    {
        await using var f = await Fixture.CreateAsync(); f.Cloud.DeferVerification = true;
        var w = f.Workspace; w.SetNetworkCost(cost);
        w.SetSettings(new() { VerifyOnExpensiveNetwork = expensiveAllowed });
        var id = (await w.ImportAsync(f.Disk, [f.File("policy.txt", "policy")])).Single();
        await w.RunOnceAsync();
        Assert.Equal(OperationState.AwaitingVerification, w.Store.GetOperation(id)!.State);
        Assert.Equal(0, f.Cloud.HashCalls);
        Assert.Equal(runs, await w.RunOnceAsync());
        Assert.Equal(runs ? OperationState.Completed : OperationState.AwaitingVerification, w.Store.GetOperation(id)!.State);
        Assert.Equal(1, f.Cloud.UploadCalls);
    }

    [Fact]
    public async Task Disabled_optional_verification_completes_fresh_upload_without_downloading_it()
    {
        await using var f = await Fixture.CreateAsync(); f.Cloud.DeferVerification = true;
        var w = f.Workspace; w.SetSettings(new() { VerifyAfterUpload = false });
        var id = (await w.ImportAsync(f.Disk, [f.File("skip.txt", "skip verification")])).Single();
        await w.RunOnceAsync();
        Assert.Equal(OperationState.Completed, w.Store.GetOperation(id)!.State);
        Assert.Equal(0, f.Cloud.HashCalls);
        Assert.Contains("未下载校验", w.Store.GetEntry(w.Store.GetOperation(id)!.EntryId)!.Status);
    }

    [Fact]
    public async Task Overwrite_keeps_backup_until_required_verification_and_manual_verification_can_run_on_hotspot()
    {
        await using var f = await Fixture.CreateAsync(); f.Cloud.DeferVerification = true;
        var w = f.Workspace; var original = f.Remote(1);
        var id = (await w.ImportAsync(f.Disk, [f.File(original.Name, "replacement")])).Single();
        await w.ResolveAsync(id, CollisionChoice.Replace);
        w.SetNetworkCost(NetworkCost.Expensive);
        await w.RunOnceAsync();
        Assert.Equal(OperationState.AwaitingVerification, w.Store.GetOperation(id)!.State);
        Assert.True(w.Store.GetOperation(id)!.VerificationRequired);
        Assert.True(f.Cloud.Items.ContainsKey(1));
        Assert.False(await w.RunOnceAsync());
        w.VerifyUploadNow(id); await w.RunOnceAsync();
        Assert.Equal(OperationState.Completed, w.Store.GetOperation(id)!.State);
        Assert.False(f.Cloud.Items.ContainsKey(1));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Verification_yields_to_new_uploads_and_downloads(bool upload)
    {
        await using var f = await Fixture.CreateAsync(); f.Cloud.DeferVerification = true;
        var w = f.Workspace;
        var first = (await w.ImportAsync(f.Disk, [f.File("first.txt", "first")])).Single();
        await w.RunOnceAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Cloud.DuringVerification = async ct => { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); };
        var verification = w.RunOnceAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Guid? second = null;
            if (upload) second = (await w.ImportAsync(f.Disk, [f.File("second.txt", "second")]).WaitAsync(TimeSpan.FromSeconds(5))).Single();
            else Assert.True(File.Exists(await w.EnsureLocalAsync(f.Remote(1).Id).WaitAsync(TimeSpan.FromSeconds(5))));
            await verification.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(OperationState.AwaitingVerification, w.Store.GetOperation(first)!.State);
            Assert.True(File.Exists(w.Store.GetOperation(first)!.SnapshotPath));
            f.Cloud.DuringVerification = null;
            if (second is { } next)
            {
                await w.RunOnceAsync();
                Assert.Equal(OperationState.AwaitingVerification, w.Store.GetOperation(next)!.State);
                Assert.Equal(OperationState.AwaitingVerification, w.Store.GetOperation(first)!.State);
            }
            await f.DrainAsync();
            Assert.Equal(OperationState.Completed, w.Store.GetOperation(first)!.State);
            Assert.Equal(upload ? 2 : 1, f.Cloud.UploadCalls);
        }
        finally { w.PreemptVerification(); await verification; }
    }

    [Fact]
    public async Task Queued_dependent_rename_does_not_starve_the_verification_it_needs()
    {
        await using var f = await Fixture.CreateAsync(); f.Cloud.DeferVerification = true;
        var w = f.Workspace; var id = (await w.ImportAsync(f.Disk, [f.File("before.txt", "content")])).Single();
        await w.RunOnceAsync();
        w.Rename(w.Store.GetOperation(id)!.EntryId, "after.txt");
        await f.DrainAsync();
        Assert.All(w.Store.Operations(), o => Assert.Equal(OperationState.Completed, o.State));
        Assert.Contains(f.Cloud.Items.Values, i => i.Name == "after.txt");
    }

    [Fact]
    public async Task Moving_to_hotspot_interrupts_running_verification_without_losing_receipt()
    {
        await using var f = await Fixture.CreateAsync(); f.Cloud.DeferVerification = true;
        var w = f.Workspace; var id = (await w.ImportAsync(f.Disk, [f.File("network.txt", "content")])).Single();
        await w.RunOnceAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Cloud.DuringVerification = async ct => { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); };
        var verification = w.RunOnceAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            w.SetNetworkCost(NetworkCost.Expensive);
            await verification.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(OperationState.AwaitingVerification, w.Store.GetOperation(id)!.State);
            Assert.NotNull(w.Store.GetOperation(id)!.UploadAttempt);
            Assert.False(await w.RunOnceAsync());
        }
        finally { w.PreemptVerification(); await verification; }
    }
}
