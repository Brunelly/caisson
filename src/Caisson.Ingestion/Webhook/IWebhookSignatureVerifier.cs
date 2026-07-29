namespace Caisson.Ingestion.Webhook;

/// <summary>
/// Verifies a Git provider's webhook delivery signature (story #62, NFR1). Kept as an interface so a
/// GitLab/Azure DevOps signature scheme can be added later without touching the webhook controller
/// (ADR 0026 — GitHub's <c>X-Hub-Signature-256</c> ships first).
/// </summary>
public interface IWebhookSignatureVerifier
{
    /// <summary>
    /// Verifies <paramref name="signatureHeader"/> against <paramref name="rawBody"/> using the
    /// configured secret. Returns <c>false</c> for any mismatch, malformed, or missing signature —
    /// never throws for an untrusted/malformed header (NFR1).
    /// </summary>
    bool Verify(ReadOnlySpan<byte> rawBody, string? signatureHeader);
}
