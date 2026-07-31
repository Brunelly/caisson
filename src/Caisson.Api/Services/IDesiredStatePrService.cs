using Caisson.Domain.NetworkConfig;

namespace Caisson.Api.Services;

/// <summary>
/// The request to create a desired-state pull request from an authored candidate that has already passed
/// the controller's server-side re-validation gate (story #170, AC5). The publisher never re-validates;
/// gating is the controller's job.
/// </summary>
/// <param name="RackId">The rack the candidate belongs to.</param>
/// <param name="ValidationRunId">The server-derived, content-bound run id the gate matched.</param>
/// <param name="VlanCatalogue">The authored VLAN catalogue.</param>
/// <param name="PortIntents">The authored port intents.</param>
/// <param name="AcknowledgedWarningCodes">The safety-warning codes the user acknowledged.</param>
public sealed record DesiredStatePrCreationRequest(
    Guid RackId,
    string ValidationRunId,
    IReadOnlyList<VlanCatalogueEntry> VlanCatalogue,
    IReadOnlyList<PortAccessIntent> PortIntents,
    IReadOnlyList<string> AcknowledgedWarningCodes);

/// <summary>
/// The outcome of a (stubbed today) PR creation. <see cref="GatePassed"/> is always true here — the
/// controller only calls the publisher after the gate passes; <see cref="PullRequestUrl"/> is null until
/// the real forge/PR pipeline lands (#172).
/// </summary>
/// <param name="GatePassed">Whether the pre-flight gate passed (always true when the publisher is invoked).</param>
/// <param name="Status">A stable status string (e.g. <c>gate-passed</c>).</param>
/// <param name="Detail">A human-readable explanation.</param>
/// <param name="PullRequestUrl">The created PR's URL, or null while the publisher is stubbed (#172).</param>
public sealed record DesiredStatePrCreationResult(
    bool GatePassed,
    string Status,
    string Detail,
    string? PullRequestUrl);

/// <summary>
/// The seam that turns a gate-passed desired-state candidate into a Git pull request (story #170, AC3).
/// The concrete forge/PR pipeline is deferred to #172; today only the read-only, side-effect-free
/// <see cref="NotYetEnabledDesiredStatePrService"/> ships. Deliberately NOT the read-only
/// <c>LibGit2SharpRepositoryProvider</c> — this is a distinct write seam.
/// </summary>
public interface IDesiredStatePrService
{
    /// <summary>Creates the pull request for a gate-passed candidate (no git write while stubbed).</summary>
    Task<DesiredStatePrCreationResult> CreatePullRequestAsync(
        DesiredStatePrCreationRequest request, CancellationToken cancellationToken);
}
