using System.Text.Json;
using Caisson.Domain.Enums;
using Caisson.Domain.Git;
using Caisson.Domain.Topology;
using Caisson.Infrastructure.LiveUpdates;
using Caisson.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace Caisson.Ingestion.Git.GitHub;

/// <summary>
/// The single choke point for a meaningful PR status transition (story #173, Tasks #212/#214). Because the
/// registered <c>IAuditEventWriter</c> is the channel-backed writer (needs a request <c>ClaimsPrincipal</c> +
/// correlation scope a system poll lacks, and may drop under pressure), this service appends
/// <see cref="TopologyAuditEvent"/> rows DIRECTLY to the scoped <see cref="CaissonDbContext"/> and commits them
/// in the SAME transaction as the already-tracked status upsert (and any link flip) — a transactional,
/// tamper-evident audit the channel writer cannot guarantee. After commit it publishes the status-changed
/// event fail-open, so a Redis outage never throws back into the poller.
/// </summary>
public sealed class PrStatusTransitionService : IPrStatusTransitionService
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    private readonly ITopologyEventPublisher _events;
    private readonly ITopologyEventSequencer _sequencer;
    private readonly TimeProvider _time;
    private readonly ILogger<PrStatusTransitionService> _logger;

    public PrStatusTransitionService(
        ITopologyEventPublisher events,
        ITopologyEventSequencer sequencer,
        TimeProvider time,
        ILogger<PrStatusTransitionService> logger)
    {
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _sequencer = sequencer ?? throw new ArgumentNullException(nameof(sequencer));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task OnStatusChangedAsync(
        CaissonDbContext context,
        GitPullRequestStatusRecord record,
        PrStatusTransitionSnapshot previous,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(previous);

        var now = _time.GetUtcNow().UtcDateTime;
        var details = BuildDetailsJson(record, previous, correlationId);

        // One audit per relevant transition kind (state and/or checks), all in the same unit of work as the
        // status upsert. Only meaningful transitions reach here, so no-op polls never produce a row.
        if (previous.PreviousState != record.State)
        {
            context.AuditEvents.Add(NewAudit(record, GitPrStatusAuditActions.StatusChanged, now, correlationId, details));
        }

        if (previous.PreviousChecksConclusion != record.ChecksConclusion)
        {
            context.AuditEvents.Add(NewAudit(record, GitPrStatusAuditActions.ChecksChanged, now, correlationId, details));
        }

        await context.SaveChangesAsync(cancellationToken);

        // Fail-open publish AFTER commit (the narrow pre-publish loss window is covered by REST reconciliation).
        await _events.PublishGitPullRequestStatusChangedAsync(_sequencer, _logger, record, correlationId, cancellationToken);
    }

    private static TopologyAuditEvent NewAudit(
        GitPullRequestStatusRecord record, string action, DateTime now, Guid correlationId, string details)
        => new(
            id: Guid.NewGuid(),
            occurredAtUtc: now,
            actorType: ActorType.System,
            actorId: "system",
            action: action,
            targetType: GitPrStatusAuditActions.TargetType,
            correlationId: correlationId,
            result: "success",
            rackId: record.RackId,
            snapshotId: null,
            targetId: record.PullRequestLinkId.ToString(),
            detailsJson: details);

    private static string BuildDetailsJson(
        GitPullRequestStatusRecord record, PrStatusTransitionSnapshot previous, Guid correlationId)
    {
        var payload = new
        {
            rackId = record.RackId,
            prNumber = record.PullRequestNumber,
            repo = $"{record.RepoOwner}/{record.RepoName}",
            previousState = previous.PreviousState.ToString(),
            newState = record.State.ToString(),
            previousChecks = previous.PreviousChecksConclusion.ToString(),
            newChecks = record.ChecksConclusion.ToString(),
            headSha = record.HeadSha,
            correlationId,
        };
        return JsonSerializer.Serialize(payload, JsonOptions);
    }
}
