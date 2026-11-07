using System.Security.Cryptography;
using Xunit;
using YuWangCloudSync.Net;
using YuWangSync.Core;

namespace YuWangSync.Tests;

public sealed class DeferredProtocolTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deferred_upload_preserves_candidate_and_later_verifies_without_resending(bool renamed)
    {
        using var web = new WebServer { AssignUploadSuffix = renamed };
        using var client = new YuWangClient("https://example.invalid", "synthetic", "", _ => { }, httpHandler: web)
        { DeferPostUploadVerification = true };
        byte[] bytes = [1, 2, 3, 4]; using var source = new MemoryStream(bytes);
        UploadAttempt? attempt = null;
        var pending = await Assert.ThrowsAsync<UploadVerificationPendingException>(() => client.UploadNewFileAsync(7, 10, "background.bin",
            source, DateTime.UtcNow, SHA256.HashData(bytes), rememberAttempt: saved => attempt = saved));
        Assert.Equal(renamed, pending.Required);
        Assert.Equal(pending.Candidate.Id, attempt!.CandidateId);
        Assert.DoesNotContain(web.Events, e => e.StartsWith("download:", StringComparison.Ordinal));
        client.DeferPostUploadVerification = false;
        var result = await client.UploadNewFileAsync(7, 10, "background.bin", source, DateTime.UtcNow, SHA256.HashData(bytes), attempt: attempt);
        Assert.Equal(pending.Candidate.Id, result.Id);
        Assert.Equal("background.bin", result.Name);
        Assert.Equal(1, web.TokenCount);
        Assert.Single(web.Ranges);
        Assert.Contains(web.Events, e => e == "download:" + result.Id);
    }

    [Fact]
    public async Task Deferred_replacement_keeps_backup_and_only_recycles_it_after_verification()
    {
        using var web = new WebServer(); web.Add(100, "report.docx", [7, 8]);
        using var client = new YuWangClient("https://example.invalid", "synthetic", "", _ => { }, httpHandler: web)
        { DeferPostUploadVerification = true };
        byte[] bytes = [1, 2, 3]; using var source = new MemoryStream(bytes);
        UploadAttempt? attempt = null;
        var pending = await Assert.ThrowsAsync<UploadVerificationPendingException>(() => client.ReplaceRemoteFileAsync(7, 10, 100,
            "report.docx", "report_back.docx", source, DateTime.UtcNow, SHA256.HashData(bytes), _ => { },
            rememberAttempt: saved => attempt = saved));
        Assert.True(pending.Required);
        Assert.Equal("report_back.docx", web.Items[100].Name);
        Assert.Empty(web.Recycled);
        Assert.DoesNotContain(web.Events, e => e.StartsWith("download:", StringComparison.Ordinal));
        client.DeferPostUploadVerification = false;
        var result = await client.ReplaceRemoteFileAsync(7, 10, 100, "report.docx", "report_back.docx", source,
            DateTime.UtcNow, SHA256.HashData(bytes), _ => { }, attempt: attempt);
        Assert.Equal(pending.Candidate.Id, result.Id);
        Assert.Equal(1, web.TokenCount);
        Assert.False(web.Items.ContainsKey(100));
        Assert.Equal(new byte[] { 7, 8 }, web.Recycled[100].Bytes);
    }
}
