namespace Caisson.Api.Services;

/// <summary>
/// The shipped <see cref="IDesiredStatePrService"/> for story #170: it performs NO git write and creates no
/// pull request — the real forge/PR pipeline is deferred to #172. It returns a synthetic gate-passed
/// result so the endpoint's gate (server re-validation, run-id match, warning acknowledgement) is fully
/// enforced and audited today, while the write path remains deliberately absent (side-effect-free, honours
/// the M1 read-only guardrails except the audit write done by the controller). See ADR 0052.
/// </summary>
public sealed class NotYetEnabledDesiredStatePrService : IDesiredStatePrService
{
    /// <summary>The stable status string returned while the publisher is stubbed.</summary>
    public const string GatePassedStatus = "gate-passed";

    /// <inheritdoc />
    public Task<DesiredStatePrCreationResult> CreatePullRequestAsync(
        DesiredStatePrCreationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Task.FromResult(new DesiredStatePrCreationResult(
            GatePassed: true,
            Status: GatePassedStatus,
            Detail: "Pre-flight gate passed. Pull-request creation is not yet enabled; the desired-state PR "
                + "pipeline is delivered in a follow-up (#172). No repository changes were made.",
            PullRequestUrl: null));
    }
}
