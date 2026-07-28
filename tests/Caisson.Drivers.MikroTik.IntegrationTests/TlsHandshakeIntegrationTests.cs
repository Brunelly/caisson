using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Caisson.Drivers.MikroTik.Transport;
using Caisson.Drivers.Simulators;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Caisson.Drivers.MikroTik.IntegrationTests;

/// <summary>
/// CWE-295 end-to-end: exercises the real 8729 TLS transport by pointing the driver's
/// <see cref="RouterOsApiClient"/> at a simulator that performs a genuine server-side
/// <see cref="System.Net.Security.SslStream"/> handshake with a self-signed certificate. Unlike the
/// unit tests — which invoke <c>ValidateServerCertificate</c> directly — these prove the whole client
/// path: the callback actually runs inside <c>AuthenticateAsClientAsync</c>, a rejection aborts the
/// handshake, and a fingerprint pin lets a full read-only command complete over TLS.
/// </summary>
public sealed class TlsHandshakeIntegrationTests
{
    private const string Username = "caisson-ro";
    private const string Password = "sim-only-password";

    [Fact]
    public async Task A_pinned_fingerprint_completes_the_tls_handshake_and_a_read_command()
    {
        using var certificate = SelfSigned();
        await using var simulator = StartTlsSimulator(certificate);

        var settings = SettingsFor(simulator, pin: FingerprintOf(certificate), allowUntrusted: false);
        await using var client = new RouterOsApiClient(settings, NullLogger.Instance);

        await client.ConnectAsync(CancellationToken.None);
        var rows = await client.SendCommandAsync("/interface/print", CancellationToken.None);

        rows.Should().NotBeEmpty("the read-only command must complete over the pinned TLS transport");
    }

    [Fact]
    public async Task An_untrusted_certificate_aborts_the_handshake_when_not_pinned_or_opted_in()
    {
        using var certificate = SelfSigned();
        await using var simulator = StartTlsSimulator(certificate);

        var settings = SettingsFor(simulator, pin: null, allowUntrusted: false);
        await using var client = new RouterOsApiClient(settings, NullLogger.Instance);

        // The driver's ValidateServerCertificate returns false for the untrusted self-signed cert, so the
        // real client handshake aborts — the exact failure RouterOsSwitchDriver maps to a non-retryable
        // ProtocolError.
        var connect = async () => await client.ConnectAsync(CancellationToken.None);
        await connect.Should().ThrowAsync<AuthenticationException>();
    }

    [Fact]
    public async Task An_explicit_opt_in_accepts_the_untrusted_certificate_over_a_real_handshake()
    {
        using var certificate = SelfSigned();
        await using var simulator = StartTlsSimulator(certificate);

        var settings = SettingsFor(simulator, pin: null, allowUntrusted: true);
        await using var client = new RouterOsApiClient(settings, NullLogger.Instance);

        await client.ConnectAsync(CancellationToken.None);
        var rows = await client.SendCommandAsync("/interface/print", CancellationToken.None);

        rows.Should().NotBeEmpty();
    }

    private static RouterOsApiSimulator StartTlsSimulator(X509Certificate2 certificate)
    {
        var simulator = new RouterOsApiSimulator(
            RouterOsApiSimulator.LoadProfile("v7"), Username, Password, certificate);
        simulator.Start();
        return simulator;
    }

    private static RouterOsConnectionSettings SettingsFor(
        RouterOsApiSimulator simulator, string? pin, bool allowUntrusted)
        => new(simulator.Host, simulator.Port, UseTls: true, Username, Password,
            TimeSpan.FromSeconds(5), pin, allowUntrusted);

    private static X509Certificate2 SelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=chr.test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        // Export/re-import so the private key is usable for the server-side handshake on all platforms.
        using var ephemeral = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
#pragma warning disable SYSLIB0057 // net8 has no X509CertificateLoader; the constructor is the supported path here.
        return new X509Certificate2(ephemeral.Export(X509ContentType.Pfx));
#pragma warning restore SYSLIB0057
    }

    private static string FingerprintOf(X509Certificate2 certificate)
        => Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
}
