using System.Net;

namespace Caisson.Drivers.Redfish.Tests.Fakes;

/// <summary>
/// A minimal <see cref="HttpMessageHandler"/> that returns a fixed status/body and records the request, so
/// the real <c>RedfishClient</c> request path (including its per-GET log line) can be driven without a
/// socket. Used to prove the transport never logs the Authorization header.
/// </summary>
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _status;
    private readonly string _body;

    public StubHttpMessageHandler(HttpStatusCode status, string body)
    {
        _status = status;
        _body = body;
    }

    /// <summary>The Authorization header value seen on the last request (to prove Basic auth was actually sent).</summary>
    public string? SeenAuthorization { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        SeenAuthorization = request.Headers.Authorization?.ToString();
        return Task.FromResult(new HttpResponseMessage(_status)
        {
            Content = new StringContent(_body),
        });
    }
}
