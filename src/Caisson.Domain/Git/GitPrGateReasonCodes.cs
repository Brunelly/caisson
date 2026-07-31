namespace Caisson.Domain.Git;

/// <summary>
/// The stable, operator-safe reason codes for the internal merged-apply gate (story #173, Task #213). Exact
/// PascalCase strings surfaced as the RFC 7807 <c>reasonCode</c> extension on a blocked apply/promote (409) and
/// as the read DTO's gate reason, kept in one place so the API, the gate, and the frontend contract agree. A
/// distinct type from the UPPER_SNAKE <c>Caisson.Api.Contracts.GitPrErrorCodes</c> (PR-creation failures).
/// </summary>
public static class GitPrGateReasonCodes
{
    /// <summary>The exact candidate's PR is merged; apply/promote is allowed (subject to normal RBAC).</summary>
    public const string Allowed = "Allowed";

    /// <summary>No pull request is linked for this exact candidate; a PR must be created first.</summary>
    public const string NoPrLinked = "NoPrLinked";

    /// <summary>A PR is linked for this candidate but it is not merged yet.</summary>
    public const string PrNotMerged = "PrNotMerged";
}
