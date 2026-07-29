using Caisson.Drivers.Abstractions.Results;
using Caisson.Drivers.Redfish.Tests.Fakes;
using Caisson.Drivers.Redfish.Transport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Caisson.Drivers.Redfish.Tests;

/// <summary>
/// Finding #9: an unbounded Redfish response body is a memory-exhaustion DoS vector from a compromised or
/// misbehaving BMC. <see cref="RedfishClient"/> must reject a response once it exceeds
/// <see cref="RedfishClient.MaxResponseBytes"/>, and must do so even when the response declares no
/// <c>Content-Length</c> (a chunked-transfer response, which the cap must not silently trust away).
/// </summary>
public sealed class ResponseCapTests : IDisposable
{
    private readonly RedfishDriverHarness _harness = new();

    [Fact]
    public async Task Driver_maps_an_over_cap_response_to_ParseError()
    {
        var client = new FakeRedfishClient();
        client.SetThrows(RedfishReadPaths.ServiceRoot,
            () => new RedfishException($"The Redfish response exceeded the {RedfishClient.MaxResponseBytes}-byte cap and was rejected."));

        var result = await _harness.Build(client, new StubIpmiCommandRunner())
            .GetSystemInventoryAsync(CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error!.Code.Should().Be(DriverErrorCode.ParseError);
    }

    public void Dispose() => _harness.Dispose();
}

/// <summary>Transport-level (non-driver) response-cap enforcement tests.</summary>
public sealed class RedfishClientResponseCapTests
{
    [Fact]
    public async Task A_response_streaming_past_the_cap_with_no_declared_length_throws_RedfishException()
    {
        var settings = new RedfishConnectionSettings("10.0.0.1", 443, "user", "pass", TimeSpan.FromSeconds(10));
        var handler = new StreamingHttpMessageHandler(RedfishClient.MaxResponseBytes + 1024 * 1024);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://10.0.0.1:443") };
        using var client = new RedfishClient(settings, NullLogger.Instance, http);

        var act = () => client.GetAsync(RedfishReadPaths.ServiceRoot, CancellationToken.None);

        await act.Should().ThrowAsync<RedfishException>().WithMessage("*exceeded*");
    }

    [Fact]
    public async Task A_response_with_a_declared_Content_Length_over_the_cap_is_rejected_before_reading_the_body()
    {
        var settings = new RedfishConnectionSettings("10.0.0.1", 443, "user", "pass", TimeSpan.FromSeconds(10));
        var handler = new StubHttpMessageHandlerWithLength(RedfishClient.MaxResponseBytes + 1);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://10.0.0.1:443") };
        using var client = new RedfishClient(settings, NullLogger.Instance, http);

        var act = () => client.GetAsync(RedfishReadPaths.ServiceRoot, CancellationToken.None);

        await act.Should().ThrowAsync<RedfishException>().WithMessage("*declared a Content-Length*");
    }

    private sealed class StubHttpMessageHandlerWithLength : HttpMessageHandler
    {
        private readonly long _declaredLength;

        public StubHttpMessageHandlerWithLength(long declaredLength) => _declaredLength = declaredLength;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{}"),
            };
            response.Content.Headers.ContentLength = _declaredLength;
            return Task.FromResult(response);
        }
    }
}
