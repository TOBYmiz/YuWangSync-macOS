using System.Security.Cryptography;
using YuWangSync.Core;

namespace YuWangCloudSync.Net;

public sealed partial class YuWangClient
{
    private readonly AsyncLocal<CancellationToken> _callCancellation = new();
    public async Task<T> InCallAsync<T>(CancellationToken ct, Func<Task<T>> action)
    {
        var previous = _callCancellation.Value; _callCancellation.Value = ct;
        try { ct.ThrowIfCancellationRequested(); return await action().ConfigureAwait(false); }
        finally { _callCancellation.Value = previous; }
    }

    public async Task<DownloadReceipt> DownloadToAsync(ulong id, string name, long? declaredSize,
        Stream output, Action<long, long>? progress, CancellationToken ct)
    {
        return await InCallAsync(ct, async () =>
        {
            var probe = await ProbeRealSizeAsync(id, name, declaredSize).ConfigureAwait(false);
            if (probe is null) throw new IOException("无法确认文件直链及真实大小，未生成完成文件。");
            if (probe.Value.IsContentless)
                return new DownloadReceipt(Convert.ToHexString(SHA256.HashData([])), 0);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetimeToken);
            deadline.CancelAfter(_downloadTimeout);
            using var request = MakeRequest(HttpMethod.Get, probe.Value.DirectUrl);
            using var response = await SendAsync(request, _downloadTimeout, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var expected = probe.Value.Size;
            if (response.Content.Headers.ContentLength is { } length && length != expected)
                throw new IOException("云端文件大小在下载前发生变化。");
            await using var source = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[128 * 1024]; long total = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer, deadline.Token).ConfigureAwait(false);
                if (read == 0) break;
                total += read;
                if (total > expected) throw new IOException("下载长度超过已探测大小。");
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), deadline.Token).ConfigureAwait(false);
                progress?.Invoke(total, expected);
            }
            if (total != expected) throw new EndOfStreamException("下载尚未完整，保留为失败任务。");
            return new DownloadReceipt(Convert.ToHexString(hash.GetHashAndReset()), total);
        });
    }

    public async Task<DownloadReceipt> HashRemoteAsync(ulong id, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetimeToken);
        deadline.CancelAfter(_downloadTimeout);
        using var request = MakeRequest(HttpMethod.Post, "/fmsFileInfoAction.do?method=downSelectFile",
            new FormUrlEncodedContent([new KeyValuePair<string, string>("selectitemid", id.ToString())]));
        using var response = await SendAsync(request, _downloadTimeout, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024]; long length = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false);
            if (read == 0) break;
            hash.AppendData(buffer, 0, read); length += read;
        }
        return new(Convert.ToHexString(hash.GetHashAndReset()), length);
    }
}
