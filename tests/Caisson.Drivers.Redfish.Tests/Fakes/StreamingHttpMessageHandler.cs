using System.Net;

namespace Caisson.Drivers.Redfish.Tests.Fakes;

/// <summary>
/// An <see cref="HttpMessageHandler"/> that streams <paramref name="totalBytes"/> of content with NO
/// declared <c>Content-Length</c> — <see cref="StreamingContent.TryComputeLength"/> always returns
/// <c>false</c> — mirroring a chunked-transfer response from a compromised/misbehaving BMC. Used to prove
/// <c>RedfishClient</c>'s response cap is enforced by counting bytes as they arrive, not by trusting the
/// (here: absent) <c>Content-Length</c> header alone.
/// </summary>
public sealed class StreamingHttpMessageHandler : HttpMessageHandler
{
    private readonly long _totalBytes;

    public StreamingHttpMessageHandler(long totalBytes) => _totalBytes = totalBytes;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamingContent(_totalBytes),
        });

    private sealed class StreamingContent : HttpContent
    {
        private readonly long _totalBytes;

        public StreamingContent(long totalBytes) => _totalBytes = totalBytes;

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var chunk = new byte[64 * 1024];
            Array.Fill(chunk, (byte)'x');
            var remaining = _totalBytes;
            while (remaining > 0)
            {
                var n = (int)Math.Min(chunk.Length, remaining);
                await stream.WriteAsync(chunk.AsMemory(0, n));
                remaining -= n;
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
