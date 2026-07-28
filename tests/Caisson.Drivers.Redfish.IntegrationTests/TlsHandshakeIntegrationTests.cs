using System.Net.Http;
using Caisson.Drivers.Redfish.Transport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Caisson.Drivers.Redfish.IntegrationTests;

/// <summary>
/// CWE-295 end-to-end: exercises the real HTTPS transport by pointing the driver's <see cref="RedfishClient"/>
/// at the in-process simulator, which performs a genuine server-side TLS handshake with its self-signed
/// certificate. A matching SHA-256 pin completes a read-only GET; an untrusted certificate aborts the
/// handshake unless the explicit allow-untrusted opt-in (scoped to integration tests) is set.
/// </summary>
public sealed class TlsHandshakeIntegrationTests : IClassFixture<RedfishBmcFixture>
{
    private const string Username = "ilo-ro";
    private const string Password = "sim-only-password";

    private readonly RedfishBmcFixture _fixture;

    public TlsHandshakeIntegrationTests(RedfishBmcFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_pinned_fingerprint_completes_the_tls_handshake_and_a_read_command()
    {
        if (_fixture.UsingRealHardware)
        {
            return; // Uses the simulator's generated certificate.
        }

        var endpoint = _fixture.ResolveEndpoint("ilo-success");
        var settings = SettingsFor(endpoint, pin: _fixture.Fingerprint, allowUntrusted: false);
        using var client = new RedfishClient(settings, NullLogger.Instance);

        var body = await client.GetAsync("/redfish/v1", CancellationToken.None);

        body.Should().Contain("Systems", "the read-only GET must complete over the pinned TLS transport");
    }

    [Fact]
    public async Task An_untrusted_certificate_aborts_the_handshake_when_not_pinned_or_opted_in()
    {
        if (_fixture.UsingRealHardware)
        {
            return;
        }

        var endpoint = _fixture.ResolveEndpoint("ilo-success");
        var settings = SettingsFor(endpoint, pin: null, allowUntrusted: false);
        using var client = new RedfishClient(settings, NullLogger.Instance);

        // The driver's ValidateServerCertificate returns false for the untrusted self-signed cert, so the
        // real handshake aborts — HttpClient surfaces it as an HttpRequestException.
        var act = () => client.GetAsync("/redfish/v1", CancellationToken.None);
        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task An_explicit_opt_in_accepts_the_untrusted_certificate_over_a_real_handshake()
    {
        if (_fixture.UsingRealHardware)
        {
            return;
        }

        var endpoint = _fixture.ResolveEndpoint("ilo-success");
        var settings = SettingsFor(endpoint, pin: null, allowUntrusted: true);
        using var client = new RedfishClient(settings, NullLogger.Instance);

        var body = await client.GetAsync("/redfish/v1", CancellationToken.None);

        body.Should().Contain("Systems");
    }

    private static RedfishConnectionSettings SettingsFor(RedfishEndpoint endpoint, string? pin, bool allowUntrusted)
        => new(endpoint.Host, endpoint.Port, Username, Password, TimeSpan.FromSeconds(5), pin, allowUntrusted);
}
