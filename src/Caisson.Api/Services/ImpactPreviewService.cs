using System.Diagnostics;
using Caisson.Api.Contracts;
using Caisson.Domain.DesiredState;
using Caisson.Domain.DesiredState.Diffing;
using Caisson.Domain.NetworkConfig.Preflight;
using Caisson.Infrastructure.Persistence;
using Caisson.Infrastructure.Persistence.Queries;
using Caisson.Infrastructure.Persistence.Shaping;
using Caisson.Ingestion.RoundTrip;
using Caisson.Orchestration.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Caisson.Api.Services;

/// <summary>The status of an impact-preview compute.</summary>
public enum ImpactPreviewStatus
{
    /// <summary>A diff was computed or served from cache.</summary>
    Success,

    /// <summary>The candidate YAML was syntactically/schematically invalid — no cache row was written.</summary>
    InvalidYaml,

    /// <summary>The rack has no ingested baseline revision.</summary>
    MissingBaseline,
}

/// <summary>
/// The outcome of an impact-preview compute. On <see cref="ImpactPreviewStatus.Success"/> the cache
/// <see cref="Row"/> (whether freshly computed or a cache/concurrent-conflict hit) plus
/// <see cref="CacheHit"/> and the diff-compute duration are carried; on <see cref="ImpactPreviewStatus.InvalidYaml"/>
/// the accumulated <see cref="Issues"/> are carried and NO row was written.
/// </summary>
public sealed record ImpactPreviewResult(
    ImpactPreviewStatus Status,
    DesiredStateCandidateDiffCache? Row,
    bool CacheHit,
    TimeSpan DiffComputeDuration,
    IReadOnlyList<DesiredStateImportIssue>? Issues);

/// <summary>
/// Computes and caches the impact preview between a rack's latest ingested desired-state revision (baseline)
/// and a candidate YAML (story #171, Tasks #196/#202). The baseline and candidate render through the SAME
/// <see cref="DesiredStateYamlRenderer"/> so the canonical YAML is symmetric and the raw diff carries no
/// formatting noise. Cache lookups key on <c>(rackId, baselineRevisionId, candidateSha256)</c> — a hit
/// returns the stored row byte-for-byte with an unchanged id/timestamp; a miss computes the unified diff +
/// semantic summary, annotates each change against the latest observed topology, and inserts a cache row.
/// Concurrent identical inserts are handled by catching the unique-key conflict and re-reading the winner.
/// </summary>
public sealed class ImpactPreviewService
{
    private readonly CaissonDbContext _context;
    private readonly TimeProvider _time;
    private readonly IOptions<DesiredStateDiffCacheOptions> _options;

    public ImpactPreviewService(
        CaissonDbContext context,
        TimeProvider time,
        IOptions<DesiredStateDiffCacheOptions> options)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// Computes (or serves from cache) the impact preview for <paramref name="yaml"/> against rack
    /// <paramref name="rackId"/>'s baseline. Assumes the caller has already verified rack access + existence.
    /// </summary>
    public async Task<ImpactPreviewResult> PreviewAsync(
        Guid rackId, string yaml, string actorId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentException.ThrowIfNullOrEmpty(actorId);

        var import = DesiredStateYamlImporter.Import(yaml);
        if (!import.IsSuccess)
        {
            // Invalid YAML: return the accumulated issues and write NO cache row (AC5).
            return new ImpactPreviewResult(ImpactPreviewStatus.InvalidYaml, null, false, TimeSpan.Zero, import.Issues);
        }

        // rackSlug is server-authoritative — resolved from the rack's ExternalKey, never the imported metadata.
        var rackSlug = await _context.Racks
            .Where(r => r.Id == rackId)
            .Select(r => r.ExternalKey)
            .FirstOrDefaultAsync(cancellationToken);
        if (rackSlug is null)
        {
            // The rack vanished between the controller's existence check and here — treat as missing baseline.
            return MissingBaseline();
        }

        var baseline = await _context.ActiveVersionForRackAsync(rackSlug, cancellationToken);
        if (baseline is null)
        {
            return MissingBaseline();
        }

        var candidateModel = new SupportedDesiredStateModel(
            rackSlug, import.Envelope!.SupportedModel.VlanCatalogue, import.Envelope.SupportedModel.PortIntents);
        var baselineModel = BaselineIntentProjection.Project(rackSlug, baseline.DesiredStateJson);

        var candidateYaml = DesiredStateYamlRenderer.Render(candidateModel).Yaml;
        var baselineYaml = DesiredStateYamlRenderer.Render(baselineModel).Yaml;
        var candidateSha = DesiredStateContentHash.Compute(candidateYaml);
        var baselineSha = DesiredStateContentHash.Compute(baselineYaml);

        var cached = await _context.FindAsync(rackId, baseline.Id, candidateSha, cancellationToken);
        if (cached is not null)
        {
            return new ImpactPreviewResult(ImpactPreviewStatus.Success, cached, CacheHit: true, TimeSpan.Zero, null);
        }

        var stopwatch = Stopwatch.StartNew();
        var rawDiff = UnifiedDiffFormatter.Format(baselineYaml, candidateYaml);
        var semantic = SemanticDiffEngine.Diff(baselineModel, candidateModel, rackId);
        stopwatch.Stop();

        var snapshot = await _context.LatestSnapshotWithGraphAsync(rackId, cancellationToken);
        var inventory = RackInventoryProjector.Project(rackId, snapshot);
        var observedVlanIds = ObservedVlanIds(inventory);

        var payload = ImpactPreviewContractMappers.ToStoredPayload(
            baseline.CommitSha, semantic.Changes, change => ExistsInTopology(change, inventory, observedVlanIds));
        var summaryJson = ImpactPreviewContractMappers.SerializeSummary(payload);

        var now = _time.GetUtcNow().UtcDateTime;
        var expiresAt = now.AddMinutes(_options.Value.TtlMinutes);
        var row = new DesiredStateCandidateDiffCache(
            Guid.NewGuid(), rackId, baseline.Id, candidateSha, baselineSha, rawDiff, summaryJson, actorId, now, expiresAt);

        try
        {
            _context.DesiredStateCandidateDiffCaches.Add(row);
            await _context.SaveChangesAsync(cancellationToken);
            return new ImpactPreviewResult(ImpactPreviewStatus.Success, row, CacheHit: false, stopwatch.Elapsed, null);
        }
        catch (DbUpdateException)
        {
            // A concurrent identical request won the unique (rack, baseline, candidate) key: drop our row,
            // re-read the winner, and serve it byte-for-byte (AC2 "one artifact for concurrent requests").
            _context.Entry(row).State = EntityState.Detached;
            var winner = await _context.FindAsync(rackId, baseline.Id, candidateSha, cancellationToken);
            if (winner is not null)
            {
                return new ImpactPreviewResult(ImpactPreviewStatus.Success, winner, CacheHit: true, TimeSpan.Zero, null);
            }

            throw;
        }
    }

    /// <summary>Resolves a cached preview by id, scoped to the rack (leak-safe GET, story #171).</summary>
    public Task<DesiredStateCandidateDiffCache?> GetByIdAsync(
        Guid rackId, Guid candidateId, CancellationToken cancellationToken)
        => _context.FindByIdForRackAsync(rackId, candidateId, cancellationToken);

    private static ImpactPreviewResult MissingBaseline()
        => new(ImpactPreviewStatus.MissingBaseline, null, false, TimeSpan.Zero, null);

    /// <summary>
    /// The set of VLAN ids observed in the latest topology (union of every port's PVID and tagged VLANs).
    /// Used to annotate whether a VLAN change references an entity present in the observed topology.
    /// </summary>
    private static HashSet<int> ObservedVlanIds(RackInventory inventory)
    {
        var ids = new HashSet<int>();
        foreach (var @switch in inventory.Switches)
        {
            foreach (var port in @switch.Ports)
            {
                if (port.Pvid is { } pvid)
                {
                    ids.Add(pvid);
                }

                foreach (var vlan in port.TaggedVlans)
                {
                    ids.Add(vlan);
                }
            }
        }

        return ids;
    }

    /// <summary>
    /// Whether the entity a change references exists in the latest observed topology. Ports resolve via
    /// <c>FindSwitch</c>/<c>FindPort</c>; VLANs resolve against the observed VLAN id set. When false the UI
    /// renders a non-blocking "not found in topology" badge instead of a deep link (AC3).
    /// </summary>
    private static bool ExistsInTopology(
        DesiredStateChange change, RackInventory inventory, HashSet<int> observedVlanIds)
    {
        var entityRef = change.EntityRef;
        return entityRef.Kind switch
        {
            EntityKind.Vlan => entityRef.VlanId is { } vlanId && observedVlanIds.Contains(vlanId),
            EntityKind.Port => entityRef.SwitchStableKey is { } switchKey
                && entityRef.PortName is { } portName
                && inventory.FindSwitch(switchKey)?.FindPort(portName) is not null,
            _ => false,
        };
    }
}
