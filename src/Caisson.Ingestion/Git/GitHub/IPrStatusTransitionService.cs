using Caisson.Domain.Git;
using Caisson.Infrastructure.Persistence;

namespace Caisson.Ingestion.Git.GitHub;

/// <summary>The last-known status snapshot captured BEFORE an observation, for auditing the transition.</summary>
public sealed record PrStatusTransitionSnapshot(
    GitPullRequestStatus PreviousState,
    GitPullRequestChecksConclusion PreviousChecksConclusion);

/// <summary>
/// The single choke point for a <em>meaningful</em> PR status transition (story #173, Tasks #212/#214). Invoked
/// by the poller ONLY when <c>ApplyObservation</c> reported a real state/checks transition. It appends the
/// tamper-evident audit row(s) DIRECTLY to the scoped <see cref="CaissonDbContext"/> and commits them in the
/// SAME unit of work as the already-tracked status upsert (and any link flip), then publishes the Redis/SignalR
/// event fail-open (a publish failure never throws back into the poller). No-op polls and transient failures
/// never reach here, so they can produce neither an audit row nor an event.
/// </summary>
public interface IPrStatusTransitionService
{
    /// <summary>
    /// Stages audit rows for the transition, commits the whole unit of work (status + link + audit) via
    /// <paramref name="context"/>, then publishes the status-changed event fail-open.
    /// </summary>
    Task OnStatusChangedAsync(
        CaissonDbContext context,
        GitPullRequestStatusRecord record,
        PrStatusTransitionSnapshot previous,
        Guid correlationId,
        CancellationToken cancellationToken);
}
