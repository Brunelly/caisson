using System.Security.Cryptography;
using System.Text;
using Caisson.Ingestion.Security;

namespace Caisson.Ingestion.Webhook;

/// <summary>
/// GitHub's <c>X-Hub-Signature-256</c> webhook signature scheme (ADR 0026, Q1): the header is
/// <c>sha256=</c> + hex(HMACSHA256(secret, rawBody)), compared in constant time
/// (<see cref="CryptographicOperations.FixedTimeEquals(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/>) —
/// modelled directly on <c>Caisson.Infrastructure.LiveUpdates.TopologyEventAuthenticity</c>.
/// </summary>
public sealed class GitHubHmacSignatureVerifier : IWebhookSignatureVerifier
{
    private const string SignaturePrefix = "sha256=";

    private readonly IGitIngestionSecretsResolver _secrets;

    public GitHubHmacSignatureVerifier(IGitIngestionSecretsResolver secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        _secrets = secrets;
    }

    public bool Verify(ReadOnlySpan<byte> rawBody, string? signatureHeader)
    {
        if (string.IsNullOrEmpty(signatureHeader)
            || !signatureHeader.StartsWith(SignaturePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!_secrets.TryResolveWebhookSecret(out var secret))
        {
            return false;
        }

        var suppliedHex = signatureHeader[SignaturePrefix.Length..];
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), rawBody);
        var expectedHex = Convert.ToHexString(expected);

        if (suppliedHex.Length != expectedHex.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expectedHex.ToUpperInvariant()),
            Encoding.ASCII.GetBytes(suppliedHex.ToUpperInvariant()));
    }
}
