namespace Caisson.Api.Security;

/// <summary>Named rate-limiting policies (finding #5), applied via <c>[EnableRateLimiting]</c>.</summary>
public static class RateLimitPolicies
{
    /// <summary>
    /// A tighter fixed window for the discovery trigger/cancel endpoints — the only control-plane
    /// writes in the API — layered on top of the global per-subject limiter.
    /// </summary>
    public const string DiscoveryTrigger = "DiscoveryTrigger";

    /// <summary>
    /// A fixed window for the anonymous Git webhook endpoint (story #62), partitioned by remote IP
    /// rather than the "oid" claim (the caller is never authenticated) — same shape as
    /// <see cref="DiscoveryTrigger"/>, layered on top of the global limiter.
    /// </summary>
    public const string GitWebhook = "GitWebhook";
}
