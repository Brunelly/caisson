using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Caisson.Drivers.MikroTik.Transport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Caisson.Drivers.MikroTik.Tests;

/// <summary>
/// CWE-295: the TLS peer-certificate policy is never a silent accept-all. It accepts a fully trusted
/// chain, a matching SHA-256 fingerprint pin, or an explicit per-connection opt-in — and rejects an
/// untrusted certificate otherwise.
/// </summary>
public sealed class TlsCertificateValidationTests
{
    private const SslPolicyErrors Untrusted = SslPolicyErrors.RemoteCertificateChainErrors;

    [Fact]
    public void A_fully_valid_chain_is_accepted_even_without_a_pin_or_opt_in()
    {
        using var cert = SelfSigned();
        var client = ClientFor(SettingsWith(thumbprint: null, allowUntrusted: false));

        client.ValidateServerCertificate(this, cert, chain: null, SslPolicyErrors.None).Should().BeTrue();
    }

    [Fact]
    public void An_untrusted_certificate_is_rejected_by_default()
    {
        using var cert = SelfSigned();
        var client = ClientFor(SettingsWith(thumbprint: null, allowUntrusted: false));

        client.ValidateServerCertificate(this, cert, chain: null, Untrusted).Should().BeFalse();
    }

    [Fact]
    public void A_matching_fingerprint_pin_accepts_the_self_signed_certificate()
    {
        using var cert = SelfSigned();
        // Pin supplied in colon-grouped form to prove separator/case normalization.
        var pin = ColonGrouped(Fingerprint(cert)).ToLowerInvariant();
        var client = ClientFor(SettingsWith(thumbprint: pin, allowUntrusted: false));

        client.ValidateServerCertificate(this, cert, chain: null, Untrusted).Should().BeTrue();
    }

    [Fact]
    public void A_mismatched_fingerprint_pin_rejects_the_certificate()
    {
        using var cert = SelfSigned();
        var wrongPin = new string('a', 64);
        var client = ClientFor(SettingsWith(thumbprint: wrongPin, allowUntrusted: false));

        client.ValidateServerCertificate(this, cert, chain: null, Untrusted).Should().BeFalse();
    }

    [Fact]
    public void An_untrusted_certificate_is_accepted_only_with_the_explicit_opt_in()
    {
        using var cert = SelfSigned();
        var client = ClientFor(SettingsWith(thumbprint: null, allowUntrusted: true));

        client.ValidateServerCertificate(this, cert, chain: null, Untrusted).Should().BeTrue();
    }

    [Fact]
    public void A_pinned_certificate_that_chains_to_a_trusted_root_is_still_rejected_on_a_pin_mismatch()
    {
        // The pin is the SOLE authority once configured (finding #1): a certificate the platform validator
        // would accept outright (SslPolicyErrors.None, as an active MITM presenting a trusted-but-wrong
        // certificate would) must still be rejected when it does not match the configured pin.
        using var cert = SelfSigned();
        var wrongPin = new string('a', 64);
        var client = ClientFor(SettingsWith(thumbprint: wrongPin, allowUntrusted: false));

        client.ValidateServerCertificate(this, cert, chain: null, SslPolicyErrors.None).Should().BeFalse();
    }

    [Fact]
    public void A_pinned_certificate_matching_the_pin_is_accepted_even_with_SslPolicyErrors_None()
    {
        using var cert = SelfSigned();
        var pin = Fingerprint(cert);
        var client = ClientFor(SettingsWith(thumbprint: pin, allowUntrusted: false));

        client.ValidateServerCertificate(this, cert, chain: null, SslPolicyErrors.None).Should().BeTrue();
    }

    private static RouterOsApiClient ClientFor(RouterOsConnectionSettings settings)
        => new(settings, NullLogger.Instance);

    private static RouterOsConnectionSettings SettingsWith(string? thumbprint, bool allowUntrusted)
        => new("192.0.2.1", 8729, UseTls: true, "user", "pass", TimeSpan.FromSeconds(2), thumbprint, allowUntrusted);

    private static X509Certificate2 SelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=chr.test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static string Fingerprint(X509Certificate certificate)
        => Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));

    private static string ColonGrouped(string hex)
        => string.Join(':', Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2)));
}
