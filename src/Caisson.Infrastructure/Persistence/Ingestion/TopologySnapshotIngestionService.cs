using System.Text.Json;
using Caisson.Correlation.Input;
using Caisson.Correlation.Results;
using Caisson.Domain.Enums;
using Caisson.Domain.Topology;
using Caisson.Infrastructure.LiveUpdates;
using Caisson.Infrastructure.Persistence.Queries;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Caisson.Infrastructure.Persistence.Ingestion;

/// <summary>
/// Persists one discovery run as an immutable, versioned snapshot together with its per-entity diffs,
/// change summary and a discovery audit event — the only DbContext-touching piece of the bridge. It
/// computes the monotonic per-rack <c>version</c> in-transaction (the unique <c>(rack_id, version)</c>
/// index is the race backstop; a unique-violation triggers a single retry) and performs a single
/// atomic <see cref="DbContext.SaveChangesAsync(CancellationToken)"/> so persistence is all-or-nothing
/// (NFR3). This is a library seam for the future discovery orchestrator (story #8); it is deliberately
/// not wired to any HTTP endpoint (the API surface is strictly read-only, NFR1).
/// </summary>
public sealed class TopologySnapshotIngestionService : ITopologySnapshotIngestionService
{
    private const string VersionUniqueConstraint = "ux_topology_snapshot_rack_id_version";
    private const int MaxAttempts = 2;

    private readonly CaissonDbContext _context;
    private readonly ITopologyIdGenerator _ids;
    private readonly ITopologyEventPublisher _events;
    private readonly ILogger<TopologySnapshotIngestionService> _logger;

    public TopologySnapshotIngestionService(
        CaissonDbContext context,
        ITopologyIdGenerator ids,
        ITopologyEventPublisher events,
        ILogger<TopologySnapshotIngestionService> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<SnapshotIngestionOutcome> IngestAsync(
        TopologyIngestionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await PersistAsync(request, cancellationToken);
            }
            catch (DbUpdateException ex)
                when (attempt < MaxAttempts && IsVersionRaceViolation(ex))
            {
                // Another run won the version race; detach the partial change set and retry once with a
                // freshly-computed version and freshly-minted ids.
                DetachAll();
            }
        }
    }

    private async Task<SnapshotIngestionOutcome> PersistAsync(
        TopologyIngestionRequest request, CancellationToken cancellationToken)
    {
        var previous = await _context.LatestSnapshotWithGraphAsync(request.RackId, cancellationToken);

        var maxVersion = await _context.Snapshots
            .Where(s => s.RackId == request.RackId)
            .Select(s => (int?)s.Version)
            .MaxAsync(cancellationToken) ?? 0;
        var version = maxVersion + 1;

        var runContext = new SnapshotRunContext(
            version,
            request.TriggerType,
            request.TriggeredBy,
            request.Source,
            request.SourceVersion,
            request.CorrelationId,
            request.Status,
            request.StartedAtUtc,
            request.CompletedAtUtc,
            request.CompletedAtUtc);

        var mapped = TopologySnapshotMapper.Map(
            request.RackId, runContext, request.Observed, request.Correlation, _ids.NewId);

        var diffResult = TopologyDiffCalculator.Diff(
            previous, mapped.Snapshot, request.CorrelationId, request.CompletedAtUtc, _ids.NewId);

        var summary = new TopologyChangeSummary(
            _ids.NewId(), request.RackId, mapped.Snapshot.Id, diffResult.ChangeCountsJson, previous?.Id);
        mapped.Snapshot.SetChangeSummary(summary);

        var audit = new TopologyAuditEvent(
            _ids.NewId(),
            request.CompletedAtUtc,
            request.ActorType,
            request.TriggeredBy,
            action: "discovery.persisted",
            targetType: "snapshot",
            correlationId: request.CorrelationId,
            result: "success",
            rackId: request.RackId,
            snapshotId: mapped.Snapshot.Id,
            targetId: mapped.Snapshot.Id.ToString(),
            detailsJson: diffResult.ChangeCountsJson);

        _context.Snapshots.Add(mapped.Snapshot);
        _context.MacAddresses.AddRange(mapped.MacAddresses);
        _context.EntityDiffs.AddRange(diffResult.Diffs);
        _context.AuditEvents.Add(audit);

        // Single implicit transaction → all-or-nothing (NFR3).
        await _context.SaveChangesAsync(cancellationToken);

        // Live update (story #9): emit snapshot-updated from this single atomic choke point so no persist
        // path can be missed. Seq = the DB-monotonic per-rack version. Belt-and-braces around the
        // fail-open publisher, so a publish fault can never fail ingestion (AC4/NFR3).
        await PublishSnapshotUpdatedAsync(request, mapped.Snapshot, version, diffResult.ChangeCountsJson, cancellationToken);

        return new SnapshotIngestionOutcome(mapped.Snapshot.Id, version, diffResult.Diffs.Count);
    }

    private async Task PublishSnapshotUpdatedAsync(
        TopologyIngestionRequest request, TopologySnapshot snapshot, int version, string changeCountsJson,
        CancellationToken cancellationToken)
    {
        try
        {
            var (added, removed, modified) = ParseTotalChangeCounts(changeCountsJson);
            var summary = new SnapshotSummary(
                snapshot.Switches.Count, snapshot.Servers.Count, snapshot.Vlans.Count, added, removed, modified);

            var @event = new SnapshotUpdatedEvent(
                request.RackId,
                JobId: null, // The ingestion request does not carry a jobId; optional per the contract.
                snapshot.Id,
                version,
                summary,
                new DateTimeOffset(DateTime.SpecifyKind(request.CompletedAtUtc, DateTimeKind.Utc)),
                Seq: version,
                request.CorrelationId);

            await _events.PublishSnapshotUpdatedAsync(@event, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "snapshot-updated publish failed (swallowed) rackId={RackId} snapshotId={SnapshotId} correlationId={CorrelationId}",
                request.RackId, snapshot.Id, request.CorrelationId);
        }
    }

    private static (int Added, int Removed, int Modified) ParseTotalChangeCounts(string changeCountsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(changeCountsJson);
            if (document.RootElement.TryGetProperty("total", out var total))
            {
                return (
                    total.TryGetProperty("added", out var a) ? a.GetInt32() : 0,
                    total.TryGetProperty("removed", out var r) ? r.GetInt32() : 0,
                    total.TryGetProperty("modified", out var m) ? m.GetInt32() : 0);
            }
        }
        catch (JsonException)
        {
            // The counts summary is best-effort context for the event; malformed JSON must not fail ingestion.
        }

        return (0, 0, 0);
    }

    private void DetachAll()
    {
        foreach (var entry in _context.ChangeTracker.Entries().ToList())
        {
            entry.State = EntityState.Detached;
        }
    }

    private static bool IsVersionRaceViolation(DbUpdateException ex)
        => ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg
            && string.Equals(pg.ConstraintName, VersionUniqueConstraint, StringComparison.Ordinal);
}

/// <summary>Ingests a completed discovery run into the observed-state store (story #7).</summary>
public interface ITopologySnapshotIngestionService
{
    /// <summary>Persists the run's snapshot, diffs, change summary and audit event atomically.</summary>
    Task<SnapshotIngestionOutcome> IngestAsync(
        TopologyIngestionRequest request, CancellationToken cancellationToken = default);
}

/// <summary>The completed discovery run to persist.</summary>
/// <param name="RackId">The stable rack the run observed.</param>
/// <param name="Observed">The raw observed input (story-3 info records).</param>
/// <param name="Correlation">The pure correlation result (story-6).</param>
/// <param name="TriggerType">How the run was initiated.</param>
/// <param name="TriggeredBy">The service account or user that initiated the run.</param>
/// <param name="ActorType">The kind of principal that initiated the run.</param>
/// <param name="Source">The source driver that produced the observations.</param>
/// <param name="SourceVersion">Optional source-driver version.</param>
/// <param name="CorrelationId">Correlation id of the run.</param>
/// <param name="Status">The terminal outcome of the run.</param>
/// <param name="StartedAtUtc">When the run started.</param>
/// <param name="CompletedAtUtc">When the run completed (also the snapshot creation/sort time).</param>
public sealed record TopologyIngestionRequest(
    Guid RackId,
    TopologyCorrelationInput Observed,
    TopologyCorrelationResult Correlation,
    TriggerType TriggerType,
    string TriggeredBy,
    ActorType ActorType,
    string Source,
    string? SourceVersion,
    Guid CorrelationId,
    SnapshotStatus Status,
    DateTime StartedAtUtc,
    DateTime CompletedAtUtc);

/// <summary>The result of persisting a discovery run.</summary>
/// <param name="SnapshotId">The new snapshot's id.</param>
/// <param name="Version">The monotonic per-rack version assigned.</param>
/// <param name="DiffCount">The number of per-entity diff rows written.</param>
public sealed record SnapshotIngestionOutcome(Guid SnapshotId, int Version, int DiffCount);

/// <summary>Mints the Guids the mapper/differ assign at persist time (injected for deterministic tests).</summary>
public interface ITopologyIdGenerator
{
    /// <summary>Returns a new unique id.</summary>
    Guid NewId();
}

/// <summary>The production <see cref="ITopologyIdGenerator"/> — random v4 Guids.</summary>
public sealed class GuidTopologyIdGenerator : ITopologyIdGenerator
{
    /// <inheritdoc />
    public Guid NewId() => Guid.NewGuid();
}
