using System.Net;
using System.Security.Cryptography;
using Xunit;
using YuWangCloudSync.Net;

namespace YuWangSync.Tests;

public sealed class StreamingTests
{
    [Fact] public async Task Download_above_int32_limit_uses_bounded_buffers_and_can_cancel()
    {
        using var server = new DownloadServer(3L * 1024 * 1024 * 1024);
        using var client = new YuWangClient("https://example.invalid", "synthetic", "", _ => { }, httpHandler: server);
        using var destination = new CountingStream(); using var cancel = new CancellationTokenSource();
        long reportedTotal = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.DownloadToAsync(1, "huge.bin", server.Length, destination,
            (done, total) => { reportedTotal = total; if (done >= 1048576) cancel.Cancel(); }, cancel.Token));
        Assert.Equal(server.Length, reportedTotal); Assert.InRange(destination.LargestWrite, 1, 128 * 1024);
        Assert.InRange(destination.Total, 1048576, 2 * 1048576);
    }
    [Fact] public async Task Full_download_checks_length_and_hash()
    {
        using var server = new DownloadServer(1048576 + 3);
        using var client = new YuWangClient("https://example.invalid", "synthetic", "", _ => { }, httpHandler: server);
        using var destination = new CountingStream();
        var result = await client.DownloadToAsync(1, "file.bin", server.Length, destination, null, TestContext.Current.CancellationToken);
        Assert.Equal(server.Length, result.Length); Assert.Equal(server.Length, destination.Total);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(new byte[server.Length])), result.Hash);
    }
    [Fact] public async Task Truncated_body_is_not_a_completed_file()
    {
        using var server = new DownloadServer(1000) { ActualLength = 12 };
        using var client = new YuWangClient("https://example.invalid", "synthetic", "", _ => { }, httpHandler: server);
        using var destination = new CountingStream();
        await Assert.ThrowsAsync<EndOfStreamException>(() => client.DownloadToAsync(1, "file.bin", 1000, destination, null, TestContext.Current.CancellationToken));
    }
    [Theory] [InlineData(0, true)] [InlineData(100, false)]
    public async Task Missing_direct_content_is_empty_only_with_zero_declaration(long declared, bool empty)
    {
        using var server = new DownloadServer(0) { Missing = true };
        using var client = new YuWangClient("https://example.invalid", "synthetic", "", _ => { }, httpHandler: server);
        using var destination = new CountingStream();
        if (empty) Assert.Equal(0, (await client.DownloadToAsync(1, "empty.bin", declared, destination, null, TestContext.Current.CancellationToken)).Length);
        else await Assert.ThrowsAsync<IOException>(() => client.DownloadToAsync(1, "file.bin", declared, destination, null, TestContext.Current.CancellationToken));
        Assert.Equal(0, server.ContentGets);
    }
    private sealed class DownloadServer(long length) : HttpMessageHandler
    {
        public long Length = length;
        public long? ActualLength;
        public bool Missing;
        public int ContentGets;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath == "/fmsPicPlayerAction.jsx")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("upload_dir/sketch/202601/0123456789abcdef0123456789abcdef.png") });
            if (Missing) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            HttpContent content;
            if (request.Method == HttpMethod.Head) content = new ByteArrayContent([]);
            else { ContentGets++; content = new StreamContent(new ZeroStream(ActualLength ?? Length)); }
            content.Headers.ContentLength = Length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
    private sealed class ZeroStream(long length) : Stream
    {
        private long _position;
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => length; public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        { var read = (int)Math.Min(count, length - _position); Array.Clear(buffer, offset, read); _position += read; return read; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); var read = (int)Math.Min(buffer.Length, length - _position); buffer.Span[..read].Clear(); _position += read; return ValueTask.FromResult(read); }
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class CountingStream : Stream
    {
        public long Total; public int LargestWrite;
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => Total; public override long Position { get => Total; set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { LargestWrite = Math.Max(LargestWrite, count); Total += count; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); LargestWrite = Math.Max(LargestWrite, buffer.Length); Total += buffer.Length; return ValueTask.CompletedTask; }
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
