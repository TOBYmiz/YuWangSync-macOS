using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;
using YuWangCloudSync.Net;
using YuWangSync.Core;
using YuWangSync.Core.Storage;
namespace YuWangSync.Tests;
public sealed class ProtocolTests
{
    [Fact]
    public async Task InheritedWindowsUploadProtocolScenarios()
    {
var passed = 0;
async Task Test(string name, Func<Task> test)
{
    await test();
    Console.WriteLine("PASS " + name);
    passed++;
}
static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
static async Task Fails(Func<Task> action)
{
    try { await action(); }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException or HttpRequestException or OperationCanceledException) { return; }
    throw new InvalidOperationException("Expected an unsuccessful write.");
}
static YuWangClient Client(WebServer server) => new("http://localhost", "test-user", "unused", _ => { }, httpHandler: server);
static async Task<YuWangItem> Upload(YuWangClient client, byte[] data, string name = "新建 & 测试.pptx", Action<UploadProgress>? progress = null,
    UploadAttempt? attempt = null, Action<UploadAttempt>? rememberAttempt = null)
{
    using var source = new MemoryStream(data);
    return await client.UploadNewFileAsync(7, 10, name, source, DateTime.UtcNow, SHA256.HashData(data), progress: progress,
        attempt: attempt, rememberAttempt: rememberAttempt);
}
static async Task<YuWangItem> Replace(YuWangClient client, byte[] data, Action<ulong>? remember = null, CancellationToken ct = default)
{
    using var source = new MemoryStream(data);
    return await client.ReplaceRemoteFileAsync(7, 10, 100, "report.docx", "report_back.docx",
        source, DateTime.UtcNow, SHA256.HashData(data), remember ?? (_ => { }), ct);
}
var oldBytes = Encoding.UTF8.GetBytes("old document");
var newBytes = Encoding.UTF8.GetBytes("new document content");

await Test("cloud names retain closing parentheses and HTML entities", () =>
{
    var parsed = HtmlParser.ExtractItems("""<div onclick="showItem('D','21')"><span title="文件名：新建文件夹 (2)"></span></div><div onclick="showItem('F','22')"><span title="文件名：答案 (&amp;)).docx<br>文件大小：12 B"></span></div>""");
    Check(parsed.Count == 2 && parsed[0].Name == "新建文件夹 (2)" && parsed[1].Name == "答案 (&)).docx", "file name parser trimmed literal characters");
    return Task.CompletedTask;
});
await Test("new file: exact 10 MiB raw chunks, encoded name, verified bytes", async () =>
{
    using var web = new WebServer();
    using var client = Client(web);
    var data = new byte[YuWangClient.UploadChunkSize + 17];
    RandomNumberGenerator.Fill(data);
    var progress = new List<UploadProgress>();
    var item = await Upload(client, data, progress: progress.Add);
    Check(web.Items[item.Id].Bytes.SequenceEqual(data), "upload content changed");
    Check(web.Ranges.SequenceEqual(new[] { $"bytes 0-10485760/{data.Length}", $"bytes 10485760-{data.Length}/{data.Length}" }), "exclusive Web range boundaries");
    Check(web.Events.IndexOf("download:" + item.Id) > web.Events.IndexOf("chunk"), "missing content verification");
    Check(progress.Any(p => p.Stage == UploadStage.Uploading && p.SentBytes > 0 && p.SentBytes < YuWangClient.UploadChunkSize && p.ConfirmedBytes == 0), "missing progress inside a chunk before server ACK");
    Check(progress.All(p => p.ConfirmedBytes <= p.SentBytes && p.SentBytes <= data.Length && p.TotalBytes == data.Length), "invalid progress counters");
    var sent = progress.Where(p => p.Stage == UploadStage.Uploading).Select(p => p.SentBytes).ToArray();
    Check(sent.SequenceEqual(sent.Order()), "upload progress moved backwards");
    Check(progress.Any(p => p.Stage == UploadStage.Verifying && p.VerifiedBytes == data.Length)
        && progress[^1].Stage == UploadStage.Finalizing, "upload completed before hash verification");
});
await Test("zero-byte new file receives a final confirmation", async () =>
{
    using var web = new WebServer();
    using var client = Client(web);
    var item = await Upload(client, [], "empty.txt");
    Check(web.Items[item.Id].Bytes.Length == 0 && web.Ranges.Single() == "bytes 0-0/0", "empty upload");
});
await Test("read-only destination performs no write", async () =>
{
    using var web = new WebServer { ReadOnly = true };
    using var client = Client(web);
    await Fails(() => Upload(client, newBytes));
    Check(web.TokenCount == 0 && web.Items.Count == 0, "readonly uploaded");
});
await Test("wrong target page performs no upload", async () =>
{
    using var web = new WebServer { WrongPage = true };
    using var client = Client(web);
    await Fails(() => Upload(client, newBytes));
    Check(web.TokenCount == 0, "wrong target uploaded");
});
await Test("malformed partial listing is never a write snapshot", async () =>
{
    using var web = new WebServer { PartialPage = true };
    using var client = Client(web);
    await Fails(() => Upload(client, newBytes));
    Check(web.TokenCount == 0, "partial listing uploaded");
});
await Test("bad chunk acknowledgement fails", async () =>
{
    using var web = new WebServer { BadOffset = true };
    using var client = Client(web);
    var progress = new List<UploadProgress>();
    await Fails(() => Upload(client, newBytes, progress: progress.Add));
    Check(!progress.Any(p => p.Stage == UploadStage.Finalizing || p.ConfirmedBytes != 0), "bad acknowledgement reported confirmed/success progress");
});
await Test("progress observer exceptions do not abort a verified upload", async () =>
{
    using var web = new WebServer();
    using var client = Client(web);
    var item = await Upload(client, newBytes, progress: _ => throw new InvalidOperationException("fixture observer"));
    Check(web.Items[item.Id].Bytes.SequenceEqual(newBytes), "observer broke upload");
});
await Test("partial upload resumes at the returned byte offset and verifies the entire file", async () =>
{
    var data = new byte[YuWangClient.UploadChunkSize + 19];
    RandomNumberGenerator.Fill(data);
    const int offset = 13; // Neither zero nor a chunk boundary.
    using var web = new WebServer { InitialOffset = offset, InitialBytes = data[..offset], UseResumeIncompleteStatus = true };
    using var client = Client(web);
    var progress = new List<UploadProgress>();
    var item = await Upload(client, data, progress: progress.Add);
    Check(web.Ranges.SequenceEqual(new[] { $"bytes {offset}-{offset + YuWangClient.UploadChunkSize}/{data.Length}",
        $"bytes {offset + YuWangClient.UploadChunkSize}-{data.Length}/{data.Length}" }), "resume did not slice at the server offset");
    Check(web.Items[item.Id].Bytes.SequenceEqual(data), "resumed content changed");
    Check(progress.Where(p => p.Stage != UploadStage.Preparing).All(p => p.ResumedBytes == offset), "resumed byte count was lost");
    Check(progress.Any(p => p.Stage == UploadStage.Uploading && p.SentBytes > offset && p.ConfirmedBytes == offset), "resume hid writes before ACK");
    Check(progress.Any(p => p.Stage == UploadStage.Verifying && p.VerifiedBytes == data.Length)
        && progress[^1].Stage == UploadStage.Finalizing, "resumed prefix was not fully verified");
});
await Test("completed token skips POST and verifies the resulting directory entry", async () =>
{
    using var web = new WebServer { InitialOffset = newBytes.Length, InitialBytes = newBytes };
    using var client = Client(web);
    var progress = new List<UploadProgress>();
    var item = await Upload(client, newBytes, progress: progress.Add);
    Check(web.Ranges.Count == 0 && web.Items[item.Id].Bytes.SequenceEqual(newBytes), "completed token sent duplicate content");
    Check(web.Events.Contains("download:" + item.Id) && progress[^1].Stage == UploadStage.Finalizing
        && progress[^1].ResumedBytes == newBytes.Length, "completed token bypassed verification");
});
await Test("out-of-range and malformed initial offsets never send data", async () =>
{
    foreach (var offset in new object[] { -1L, newBytes.Length + 1L, "1", 0.5, true })
    {
        using var web = new WebServer { InitialOffsetReply = offset };
        using var client = Client(web);
        await Fails(() => Upload(client, newBytes));
        Check(web.Ranges.Count == 0, "invalid resume wrote data");
    }
});
await Test("completed token without a directory entry is not accepted", async () =>
{
    using var web = new WebServer { InitialOffset = newBytes.Length, InitialBytes = newBytes, PublishCompletedResume = false };
    using var client = Client(web);
    var progress = new List<UploadProgress>();
    await Fails(() => Upload(client, newBytes, progress: progress.Add));
    Check(web.Ranges.Count == 0 && !progress.Any(p => p.Stage == UploadStage.Finalizing), "missing completed entry accepted");
});
await Test("stale resumed bytes cannot pass full-file verification", async () =>
{
    foreach (var offset in new[] { 1, newBytes.Length })
    {
        var stale = newBytes[..offset];
        stale[0] ^= 1;
        using var web = new WebServer { InitialOffset = offset, InitialBytes = stale };
        using var client = Client(web);
        var progress = new List<UploadProgress>();
        await Fails(() => Upload(client, newBytes, progress: progress.Add));
        Check(!progress.Any(p => p.Stage == UploadStage.Finalizing), "stale token reported verified success");
        Check(web.Items.Count == 1 && web.Recycled.Count == 0, "unverified remote content was deleted");
    }
});
await Test("replacement with a stale resumed prefix restores the original backup", async () =>
{
    using var web = new WebServer { InitialOffset = 1, InitialBytes = [0xff] };
    web.Add(100, "report.docx", oldBytes);
    using var client = Client(web);
    await Fails(() => Replace(client, newBytes));
    Check(web.Items[100].Name == "report.docx" && web.Items[100].Bytes.SequenceEqual(oldBytes)
        && web.Recycled.Count == 0, "invalid resumed replacement lost the original");
    Check(web.Items.Values.Any(i => i.Id != 100 && i.Name.StartsWith("report_failed_")), "invalid replacement was not preserved");
});
await Test("same-name retry verifies and reuses uploaded ID", async () =>
{
    using var web = new WebServer();
    using var client = Client(web);
    var first = await Upload(client, newBytes);
    var second = await Upload(client, newBytes);
    Check(first.Id == second.Id && web.TokenCount == 1, "retry duplicated upload");
});
await Test("server-assigned six-digit suffix is verified by ID and renamed once", async () =>
{
    using var web = new WebServer { AssignUploadSuffix = true };
    web.Add(90, "新建 & 测试_000090.pptx", oldBytes);
    using var client = Client(web);
    var item = await Upload(client, newBytes);
    Check(item.Name == "新建 & 测试.pptx" && web.Items[item.Id].Bytes.SequenceEqual(newBytes), "assigned name was not restored");
    Check(web.Items[90].Name == "新建 & 测试_000090.pptx" && web.Items[90].Bytes.SequenceEqual(oldBytes), "pre-existing suffix was changed");
    Check(web.TokenCount == 1 && web.Events.IndexOf("download:" + item.Id)
        < web.Events.IndexOf("rename:" + item.Id + ":" + item.Name), "renamed before verifying uploaded content");
});
await Test("final byte ACK waits for fresh delayed directory publication without reuploading", async () =>
{
    using var web = new WebServer { AssignUploadSuffix = true, PublishedListingDelay = 2, StaleUploadListings = true };
    using var client = Client(web);
    UploadAttempt? attempt = null;
    var item = await Upload(client, newBytes, rememberAttempt: value => attempt = value);
    Check(web.TokenCount == 1 && web.Ranges.Count == 1 && web.FreshListRequests >= 3, "directory wait duplicated upload or used stale list");
    Check(web.Items.Count == 1 && item.Name == "新建 & 测试.pptx" && web.Items[item.Id].Bytes.SequenceEqual(newBytes), "delayed suffixed upload was not verified and renamed");
    Check(attempt is { MayHaveCommitted: true, CandidateId: > 0 }, "directory wait lost its durable upload evidence");
});
await Test("cancellation during delayed directory publication preserves upload evidence", async () =>
{
    using var stop = new CancellationTokenSource();
    using var web = new WebServer { AssignUploadSuffix = true, HidePublishedUploads = true };
    web.AfterPublishedListing = () => stop.Cancel();
    using var client = Client(web);
    UploadAttempt? attempt = null;
    using var source = new MemoryStream(newBytes);
    await Fails(() => client.UploadNewFileAsync(7, 10, "cancelled.txt", source, DateTime.UtcNow,
        SHA256.HashData(newBytes), stop.Token, rememberAttempt: value => attempt = value));
    Check(stop.IsCancellationRequested && attempt is { MayHaveCommitted: true }, "cancelled publication wait discarded its receipt");
    Check(web.TokenCount == 1 && web.Items.Count == 1 && !web.Events.Any(e => e.StartsWith("rename:")), "cancelled wait wrote another file or renamed an unverified candidate");
});
await Test("lost final reply with assigned suffix recovers without another upload", async () =>
{
    using var web = new WebServer { AssignUploadSuffix = true, LoseFinalReply = true };
    UploadAttempt? attempt = null;
    using (var client = Client(web))
        await Fails(() => Upload(client, newBytes, rememberAttempt: value => attempt = value));
    Check(attempt is { MayHaveCommitted: true }, "final POST was sent without durable uncertainty marker");
    web.LoseFinalReply = false;
    using var retry = Client(web);
    var item = await Upload(retry, newBytes, attempt: attempt, rememberAttempt: value => attempt = value);
    Check(web.TokenCount == 1 && web.Items.Count == 1 && item.Name == "新建 & 测试.pptx", "lost reply duplicated suffixed upload");
});
await Test("unpublished partial upload can still resume without losing uncertainty protection", async () =>
{
    var data = new byte[YuWangClient.UploadChunkSize + 7];
    RandomNumberGenerator.Fill(data);
    using var web = new WebServer { FailPartialChunkOnce = true, AssignUploadSuffix = true };
    UploadAttempt? attempt = null;
    using var client = Client(web);
    await Fails(() => Upload(client, data, rememberAttempt: value => attempt = value));
    Check(attempt is { MayHaveCommitted: false } && web.Items.Count == 0, "partial chunk incorrectly marked as published");
    web.InitialOffset = YuWangClient.UploadChunkSize;
    web.InitialBytes = data[..YuWangClient.UploadChunkSize];
    var item = await Upload(client, data, attempt: attempt, rememberAttempt: value => attempt = value);
    Check(web.Items.Count == 1 && web.Items[item.Id].Bytes.SequenceEqual(data), "partial retry failed to resume");
});
await Test("missing or ambiguous published entries never start another token", async () =>
{
    foreach (var ambiguous in new[] { false, true })
    {
        using var web = new WebServer { AssignUploadSuffix = true, HidePublishedUploads = !ambiguous, PublishExtraUpload = ambiguous };
        using var client = Client(web);
        UploadAttempt? attempt = null;
        await Fails(() => Upload(client, newBytes, rememberAttempt: value => attempt = value));
        await Fails(() => Upload(client, newBytes, attempt: attempt, rememberAttempt: value => attempt = value));
        Check(web.TokenCount == 1 && web.Items.Count == (ambiguous ? 2 : 1), "ambiguous outcome created another duplicate");
        if (!ambiguous)
        {
            web.HidePublishedUploads = false;
            var item = await Upload(client, newBytes, attempt: attempt, rememberAttempt: value => attempt = value);
            Check(web.TokenCount == 1 && web.Items[item.Id].Name == "新建 & 测试.pptx", "late directory entry was not recovered");
        }
    }
});
await Test("candidate survives new operation-store reopen without retransmission", async () =>
{
    var root = Path.Combine(Path.GetTempPath(), "YuWangSync-receipt-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
    var db = Path.Combine(root, "attempt.db"); var op = new Operation { AccountId = Guid.NewGuid(), Name = "新建 & 测试.pptx" };
    using var web = new WebServer { AssignUploadSuffix = true, RefuseUploadedRename = true };
    using var client = Client(web);
    try
    {
        using (var cache = new OperationStore(db))
        {
            cache.Save(operations: [op]);
            await Fails(() => Upload(client, newBytes, rememberAttempt: value => cache.UpdateOperation(op.Id, item => item with { UploadAttempt = value })));
            Check(cache.GetOperation(op.Id)?.UploadAttempt is { CandidateId: > 0, MayHaveCommitted: true }, "candidate not persisted");
        }
        web.RefuseUploadedRename = false;
        using (var cache = new OperationStore(db))
        {
            var item = await Upload(client, newBytes, attempt: cache.GetOperation(op.Id)!.UploadAttempt,
                rememberAttempt: value => cache.UpdateOperation(op.Id, stored => stored with { UploadAttempt = value }));
            Check(web.TokenCount == 1 && web.Items.Count == 1 && item.Name == op.Name, "reopened receipt duplicated content");
        }
    }
    finally { Directory.Delete(root, true); }
});
await Test("cancelled final reply retains publish evidence and resumes without a second token", async () =>
{
    using var web = new WebServer(); using var client = Client(web); using var cancel = new CancellationTokenSource();
    var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    web.BeforeChunkReply = () => { reached.TrySetResult(); return release.Task; };
    UploadAttempt? attempt = null;
    using var data = new MemoryStream(newBytes);
    var upload = client.UploadNewFileAsync(7, 10, "cancel.txt", data, DateTime.UtcNow, SHA256.HashData(newBytes), cancel.Token,
        rememberAttempt: value => attempt = value);
    await reached.Task.WaitAsync(TimeSpan.FromSeconds(10)); cancel.Cancel(); await Fails(() => upload);
    Check(attempt is { MayHaveCommitted: true }, "cancel lost publication evidence");
    web.BeforeChunkReply = null; release.TrySetResult();
    await Upload(client, newBytes, "cancel.txt", attempt: attempt);
    Check(web.TokenCount == 1 && web.Items.Count == 1, "cancel recovery duplicated upload");
});
await Test("corrupt suffixed candidate is never renamed or blindly uploaded again", async () =>
{
    using var web = new WebServer { AssignUploadSuffix = true, CorruptDownload = true };
    using var client = Client(web);
    UploadAttempt? attempt = null;
    await Fails(() => Upload(client, newBytes, rememberAttempt: value => attempt = value));
    await Fails(() => Upload(client, newBytes, attempt: attempt, rememberAttempt: value => attempt = value));
    Check(web.TokenCount == 1 && web.Items.Count == 1 && web.Items.Values.Single().Name.EndsWith("_001000.pptx"), "invalid candidate was renamed or duplicated");
});
await Test("new-file API rejects different same-name content", async () =>
{
    using var web = new WebServer();
    web.Add(100, "新建 & 测试.pptx", oldBytes);
    using var client = Client(web);
    await Fails(() => Upload(client, newBytes));
    Check(web.TokenCount == 0 && web.Items[100].Bytes.SequenceEqual(oldBytes), "original overwritten");
});
await Test("overwrite: backup, upload, hash, persist ID, recycle old item", async () =>
{
    using var web = new WebServer();
    web.Add(100, "report.docx", oldBytes);
    using var client = Client(web);
    var item = await Replace(client, newBytes, id => web.Events.Add("remember:" + id));
    Check(web.Items[item.Id].Name == "report.docx" && !web.Items.ContainsKey(100), "replacement not committed");
    Check(web.Recycled[100].Name == "report_back.docx" && web.Recycled[100].Bytes.SequenceEqual(oldBytes), "old content not retained in recycle bin");
    Check(web.Events.IndexOf("rename:100:report_back.docx") < web.Events.IndexOf("token"), "backup must precede upload");
    Check(web.Events.IndexOf("download:" + item.Id) < web.Events.IndexOf("remember:" + item.Id)
        && web.Events.IndexOf("remember:" + item.Id) < web.Events.IndexOf("delete:100"), "old deleted before verified persistent new ID");
});
await Test("token failure restores original filename and bytes", async () =>
{
    using var web = new WebServer { FailToken = true };
    web.Add(100, "report.docx", oldBytes);
    using var client = Client(web);
    await Fails(() => Replace(client, newBytes));
    Check(web.Items[100].Name == "report.docx" && web.Recycled.Count == 0, "rollback failed");
});
await Test("hash mismatch preserves new item and restores original", async () =>
{
    using var web = new WebServer { CorruptDownload = true };
    web.Add(100, "report.docx", oldBytes);
    using var client = Client(web);
    await Fails(() => Replace(client, newBytes));
    Check(web.Items[100].Name == "report.docx" && web.Recycled.Count == 0, "corrupt upload deleted old");
    Check(web.Items.Values.Any(i => i.Id != 100 && i.Name.StartsWith("report_failed_")), "unverified upload not preserved");
});
await Test("lost final upload reply preserves data and restores original", async () =>
{
    using var web = new WebServer { LoseFinalReply = true };
    web.Add(100, "report.docx", oldBytes);
    using var client = Client(web);
    await Fails(() => Replace(client, newBytes));
    Check(web.Items[100].Name == "report.docx" && web.Items.Count == 2 && web.Recycled.Count == 0, "ambiguous upload rollback");
});
await Test("cleanup failure resumes without another upload", async () =>
{
    using var web = new WebServer { RefuseDelete = true };
    web.Add(100, "report.docx", oldBytes);
    using var client = Client(web);
    await Fails(() => Replace(client, newBytes));
    Check(web.Items[100].Name == "report_back.docx", "cleanup failure lost backup");
    web.RefuseDelete = false;
    await Replace(client, newBytes);
    Check(web.TokenCount == 1 && web.Items.Count == 1 && web.Recycled.ContainsKey(100), "cleanup reuploaded");
});
await Test("restart after backup rename resumes replacement", async () =>
{
    using var web = new WebServer();
    web.Add(100, "report_back.docx", oldBytes);
    using var client = Client(web);
    await Replace(client, newBytes);
    Check(web.Items.Count == 1 && web.Recycled.ContainsKey(100), "backup restart failed");
});
await Test("restart after old deletion reuses verified new item", async () =>
{
    using var web = new WebServer();
    web.Add(200, "report.docx", newBytes);
    using var client = Client(web);
    var result = await Replace(client, newBytes);
    Check(result.Id == 200 && web.TokenCount == 0, "completed restart duplicated upload");
});
await Test("new folder uses addSave and verified rename", async () =>
{
    using var web = new WebServer();
    using var client = Client(web);
    var result = await client.CreateRemoteFolderAsync(7, 10, "新建文件夹 & 目录 (2)");
    Check(result.IsFolder && web.Items[result.Id].Name == "新建文件夹 & 目录 (2)", "folder not renamed");
});
await Test("directory rename failure resumes its recorded ID without another addSave", async () =>
{
    using var web = new WebServer { RenameHttpError = true };
    using var client = Client(web);
    ulong created = 0;
    await Fails(() => client.CreateRemoteFolderAsync(7, 10, "resumed", rememberCreated: id => created = id));
    Check(created != 0 && web.Items.Count == 1, "created ID not persisted before rename");
    web.RenameHttpError = false;
    var result = await client.CreateRemoteFolderAsync(7, 10, "resumed", createdId: created);
    Check(result.Id == created && web.Items.Count == 1, "directory recovery duplicated addSave");
});
await Test("move and rename verifies both source and destination", async () =>
{
    using var web = new WebServer();
    web.Add(100, "report.docx", oldBytes);
    using var client = Client(web);
    Check(await client.RenameRemoteItemAsync(7, 10, 100, false, 20, "moved.docx"), "move failed");
    Check(web.Items[100].Folder == 20 && web.Items[100].Name == "moved.docx", "move missed new name");
});
await Test("HTTP 500 with true body never confirms rename", async () =>
{
    using var web = new WebServer { RenameHttpError = true };
    web.Add(100, "report.docx", oldBytes);
    using var client = Client(web);
    await Fails(async () => { await client.RenameRemoteItemAsync(7, 10, 100, false, 10, "other.docx"); });
});

Assert.Equal(36, passed);
    }
}
sealed class WebServer : HttpMessageHandler
{
    public sealed record Item(ulong Id, string Name, byte[] Bytes, bool Directory = false, ulong Folder = 10);
    public Dictionary<ulong, Item> Items { get; } = [];
    public Dictionary<ulong, Item> Recycled { get; } = [];
    public List<string> Events { get; } = [];
    public List<string> Ranges { get; } = [];
    public bool ReadOnly, WrongPage, PartialPage, BadOffset, FailToken, CorruptDownload, LoseFinalReply, RefuseDelete, RenameHttpError;
    public long InitialOffset;
    public byte[] InitialBytes = [];
    public object? InitialOffsetReply;
    public bool PublishCompletedResume = true;
    public bool UseResumeIncompleteStatus;
    public bool AssignUploadSuffix, RefuseUploadedRename, HidePublishedUploads, PublishExtraUpload, FailPartialChunkOnce;
    public bool StaleUploadListings;
    public int PublishedListingDelay, FreshListRequests;
    public Action? AfterPublishedListing;
    private int _hiddenUploadReads;
    private readonly HashSet<ulong> _publishedUploads = [];
    public int TokenCount;
    public Func<Task>? BeforeChunkReply;
    private ulong _nextId = 1000, _currentFolder = 10, _cut;
    private readonly Dictionary<string, (string Name, ulong Folder, long Size, MemoryStream Data)> _uploads = [];
    public void Add(ulong id, string name, byte[] data) => Items[id] = new(id, name, data);
    static HttpResponseMessage Text(string text, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(text) };
    static Dictionary<string, string> Fields(string query) => query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0].Replace('+', ' ')),
            p => p.Length == 1 ? "" : Uri.UnescapeDataString(p[1].Replace('+', ' ')));
    string Page(ulong folder)
    {
        var b = new StringBuilder($"<form id='pageForm'><input name='diskid' value='{(WrongPage ? 999 : 7)}'><input name='folderid' value='{folder}'><input name='userid' value='144'><input name='parentid' value='0'>");
        foreach (var action in new[] { "new", "delete", "cut", "paste", "upload" })
            b.Append(ReadOnly ? $"<span class='toolbar_{action}1'>disabled</span>" : $"<a href='javascript:write()'><span class='toolbar_{action}'>write</span></a>");
        foreach (var item in Items.Values.Where(i => i.Folder == folder && (!HidePublishedUploads || !_publishedUploads.Contains(i.Id))))
            b.Append($"<div onclick=\"showItem('{(item.Directory ? 'D' : 'F')}','{item.Id}')\"><span title=\"文件名：{WebUtility.HtmlEncode(item.Name)}<br>文件大小：{item.Bytes.Length} B<br>上传时间:2026-11-05 12:00:00\"></span></div>");
        if (PartialPage) b.Append("<div onclick=\"showItem('F','99999')\"></div>");
        return b.Append("</form>").ToString();
    }
    private void PublishUpload((string Name, ulong Folder, long Size, MemoryStream Data) upload)
    {
        _hiddenUploadReads = PublishedListingDelay;
        for (var count = 0; count < (PublishExtraUpload ? 2 : 1); count++)
        {
            var id = _nextId++;
            var name = AssignUploadSuffix ? Path.GetFileNameWithoutExtension(upload.Name) + "_" + id.ToString("D6") + Path.GetExtension(upload.Name) : upload.Name;
            Items[id] = new(id, name, upload.Data.ToArray(), false, upload.Folder);
            _publishedUploads.Add(id);
        }
    }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var uri = request.RequestUri!;
        var q = Fields(uri.Query);
        var method = q.GetValueOrDefault("method", "");
        if (method == "list")
        {
            _currentFolder = ulong.Parse(q["folderid"]);
            var fresh = q.ContainsKey("_");
            if (fresh)
            {
                FreshListRequests++;
                CheckRange(request.Headers.CacheControl is { NoCache: true, NoStore: true });
            }
            var previousHide = HidePublishedUploads;
            if (_publishedUploads.Count > 0)
            {
                HidePublishedUploads |= (StaleUploadListings && !fresh) || _hiddenUploadReads-- > 0;
                AfterPublishedListing?.Invoke();
            }
            try { return Text(Page(_currentFolder)); }
            finally { HidePublishedUploads = previousHide; }
        }
        if (method == "beforePaste") return Text(ReadOnly ? "forbidden" : _cut == 0 ? "false" : "true");
        if (method == "beforeUpload") return Text("<html>Stream uploader</html>");
        if (uri.AbsolutePath == "/TokenServlet")
        {
            CheckRange(q.ContainsKey("_"));
            Events.Add("token");
            TokenCount++;
            if (FailToken) return Text("failure", HttpStatusCode.ServiceUnavailable);
            var token = "test-token-" + TokenCount;
            var data = new MemoryStream();
            data.Write(InitialBytes);
            _uploads[token] = (q["name"], ulong.Parse(q["folderid"]), long.Parse(q["size"]), data);
            return Text(JsonSerializer.Serialize(new { success = true, token }));
        }
        if (uri.AbsolutePath == "/StreamServlet")
        {
            var upload = _uploads[q["token"]];
            if (request.Method == HttpMethod.Get)
            {
                CheckRange(q.ContainsKey("_"));
                if (PublishCompletedResume && InitialOffset > 0 && InitialOffset == upload.Size && InitialOffsetReply is null)
                {
                    PublishUpload(upload);
                }
                return Text(JsonSerializer.Serialize(new { success = true, start = InitialOffsetReply ?? InitialOffset }),
                    UseResumeIncompleteStatus && InitialOffset < upload.Size ? (HttpStatusCode)308 : HttpStatusCode.OK);
            }
            Events.Add("chunk");
            var bytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            var range = request.Content.Headers.GetValues("Content-Range").Single();
            Ranges.Add(range);
            if (request.Content.Headers.ContentType?.MediaType != "application/octet-stream") throw new InvalidOperationException("multipart upload");
            var start = upload.Data.Length;
            CheckRange(range == $"bytes {start}-{start + bytes.Length}/{upload.Size}");
            upload.Data.Write(bytes);
            if (upload.Data.Length == upload.Size)
            {
                PublishUpload(upload);
                if (LoseFinalReply) throw new HttpRequestException("Simulated lost final reply");
            }
            else if (FailPartialChunkOnce)
            {
                FailPartialChunkOnce = false;
                throw new HttpRequestException("Simulated interrupted partial upload");
            }
            if (BeforeChunkReply is { } beforeReply) await beforeReply().WaitAsync(cancellationToken);
            return Text(JsonSerializer.Serialize(new { success = true, start = upload.Data.Length + (BadOffset ? 1 : 0) }),
                UseResumeIncompleteStatus && upload.Data.Length < upload.Size ? (HttpStatusCode)308 : HttpStatusCode.OK);
        }
        var f = request.Content is null ? new Dictionary<string, string>() : Fields(await request.Content.ReadAsStringAsync(cancellationToken));
        if (method == "downSelectFile")
        {
            var id = ulong.Parse(f["selectitemid"]);
            Events.Add("download:" + id);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(CorruptDownload ? [1, 2, 3] : Items[id].Bytes) };
        }
        if (method is "changeFileName" or "changeFolderName")
        {
            if (RenameHttpError) return Text("true", HttpStatusCode.InternalServerError);
            var id = ulong.Parse(f["fid"]);
            if (RefuseUploadedRename && _publishedUploads.Contains(id)) return Text("false");
            Events.Add("rename:" + id + ":" + f["newname"]);
            Items[id] = Items[id] with { Name = f["newname"] };
            return Text("true");
        }
        if (method == "delBatchRecord")
        {
            var id = ulong.Parse(f["checkid"][2..]);
            Events.Add("delete:" + id);
            if (!RefuseDelete) { Recycled[id] = Items[id]; Items.Remove(id); }
            return Text("true");
        }
        if (method == "addSave")
        {
            var id = _nextId++;
            Items[id] = new(id, "新建文件夹", [], true, _currentFolder);
            return Text(Page(_currentFolder));
        }
        if (method == "beforeCut")
        {
            _cut = ulong.Parse(f["checkids"].TrimEnd(';')[2..]);
            return Text("true");
        }
        if (method == "pasteRecord")
        {
            Items[_cut] = Items[_cut] with { Folder = ulong.Parse(f["folderid"]) };
            _cut = 0;
            return Text(Page(_currentFolder));
        }
        throw new InvalidOperationException("Unexpected fixture request: " + uri.AbsolutePath + " method=" + method);
    }
    static void CheckRange(bool condition) { if (!condition) throw new InvalidOperationException("wrong upload Content-Range"); }
    protected override void Dispose(bool disposing)
    {
        if (disposing) foreach (var upload in _uploads.Values) upload.Data.Dispose();
        base.Dispose(disposing);
    }
}
