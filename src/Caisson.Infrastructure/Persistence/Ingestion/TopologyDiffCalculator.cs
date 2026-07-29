using System.Text.Json;
using Caisson.Domain.Enums;
using Caisson.Domain.Topology;
using Caisson.Domain.Topology.Diffing;

namespace Caisson.Infrastructure.Persistence.Ingestion;

/// <summary>
/// Pure, DB-free diff engine (AC2). Compares a rack's previous snapshot with the new one by
/// <see cref="StableKeys"/> and emits one <see cref="TopologyEntityDiff"/> per added/removed/modified
/// entity — unchanged entities produce no row. A rack's first snapshot (<paramref name="previous"/> is
/// <c>null</c>) yields all-<see cref="ChangeType.Added"/> rows. Deterministic and idempotent by
/// construction: identical inputs produce the same set of diffs and the same
/// <see cref="TopologyDiffResult.ChangeCountsJson"/> rollup, and each entity's stable key appears at
/// most once per snapshot so the unique <c>(snapshot_id, entity_type, entity_stable_key)</c> index is
/// never violated on re-persist.
/// </summary>
public static class TopologyDiffCalculator
{
    private static readonly TopologyEntityType[] DiffedTypes =
    {
        TopologyEntityType.Switch,
        TopologyEntityType.SwitchPort,
        TopologyEntityType.Server,
        TopologyEntityType.Nic,
        TopologyEntityType.Vlan,
        TopologyEntityType.Lldp,
    };

    private static readonly IReadOnlyDictionary<string, string?> Empty =
        new Dictionary<string, string?>();

    /// <summary>Computes the per-entity diffs and the change-count rollup between two snapshots.</summary>
    public static TopologyDiffResult Diff(
        TopologySnapshot? previous,
        TopologySnapshot current,
        Guid correlationId,
        DateTime createdAtUtc,
        Func<Guid> newId)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(newId);

        var currentFields = TopologyEntityFields.Extract(current, out var currentCollisions);
        IReadOnlyDictionary<TopologyEntityType, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>>>? previousFields = null;
        IReadOnlyList<StableKeyCollision> previousCollisions = Array.Empty<StableKeyCollision>();
        if (previous is not null)
        {
            previousFields = TopologyEntityFields.Extract(previous, out previousCollisions);
        }

        var previousSnapshotId = previous?.Id;
        var diagnostics = currentCollisions.Concat(previousCollisions)
            .Select(c => $"[{ReasonCode.StableKeyCollision}] A second {c.EntityType} entity computed the stable key '{c.StableKey}'; it was skipped rather than overwriting the first.")
            .ToList();

        var diffs = new List<TopologyEntityDiff>();
        var counts = new Dictionary<TopologyEntityType, ChangeCounts>();

        foreach (var type in DiffedTypes)
        {
            var curr = currentFields[type];
            var prev = previousFields is null
                ? new Dictionary<string, IReadOnlyDictionary<string, string?>>()
                : previousFields[type];

            var tally = new ChangeCounts();

            foreach (var key in curr.Keys.Union(prev.Keys, StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal))
            {
                var inCurr = curr.TryGetValue(key, out var currValue);
                var inPrev = prev.TryGetValue(key, out var prevValue);

                if (inCurr && !inPrev)
                {
                    diffs.Add(BuildDiff(
                        newId(), current, previousSnapshotId, type, key, ChangeType.Added,
                        AddedPayload(currValue!), createdAtUtc, correlationId));
                    tally.Added++;
                }
                else if (!inCurr && inPrev)
                {
                    diffs.Add(BuildDiff(
                        newId(), current, previousSnapshotId, type, key, ChangeType.Removed,
                        RemovedPayload(prevValue!), createdAtUtc, correlationId));
                    tally.Removed++;
                }
                else
                {
                    var changed = ChangedFields(prevValue ?? Empty, currValue ?? Empty);
                    if (changed.Count > 0)
                    {
                        diffs.Add(BuildDiff(
                            newId(), current, previousSnapshotId, type, key, ChangeType.Modified,
                            ModifiedPayload(changed), createdAtUtc, correlationId));
                        tally.Modified++;
                    }
                }
            }

            counts[type] = tally;
        }

        return new TopologyDiffResult(diffs, SerializeCounts(counts), diagnostics);
    }

    private static TopologyEntityDiff BuildDiff(
        Guid id,
        TopologySnapshot current,
        Guid? previousSnapshotId,
        TopologyEntityType type,
        string stableKey,
        ChangeType changeType,
        string payloadJson,
        DateTime createdAtUtc,
        Guid correlationId)
        => new(
            id, current.RackId, current.Id, type, stableKey, changeType, payloadJson,
            createdAtUtc, correlationId, previousSnapshotId);

    private static string AddedPayload(IReadOnlyDictionary<string, string?> fields)
        => JsonSerializer.Serialize(new Dictionary<string, object?> { ["new"] = fields });

    private static string RemovedPayload(IReadOnlyDictionary<string, string?> fields)
        => JsonSerializer.Serialize(new Dictionary<string, object?> { ["old"] = fields });

    private static string ModifiedPayload(IReadOnlyDictionary<string, string?[]> changed)
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (field, oldNew) in changed)
        {
            body[field] = new Dictionary<string, string?> { ["old"] = oldNew[0], ["new"] = oldNew[1] };
        }

        return JsonSerializer.Serialize(new Dictionary<string, object?> { ["changed"] = body });
    }

    private static SortedDictionary<string, string?[]> ChangedFields(
        IReadOnlyDictionary<string, string?> prev, IReadOnlyDictionary<string, string?> curr)
    {
        var changed = new SortedDictionary<string, string?[]>(StringComparer.Ordinal);
        foreach (var field in prev.Keys.Union(curr.Keys, StringComparer.Ordinal))
        {
            prev.TryGetValue(field, out var oldValue);
            curr.TryGetValue(field, out var newValue);
            if (!string.Equals(oldValue, newValue, StringComparison.Ordinal))
            {
                changed[field] = new[] { oldValue, newValue };
            }
        }

        return changed;
    }

    private static string SerializeCounts(Dictionary<TopologyEntityType, ChangeCounts> counts)
    {
        var body = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        var total = new ChangeCounts();
        foreach (var (type, tally) in counts)
        {
            body[type.ToString()] = tally.ToObject();
            total.Added += tally.Added;
            total.Removed += tally.Removed;
            total.Modified += tally.Modified;
        }

        body["total"] = total.ToObject();
        return JsonSerializer.Serialize(body);
    }

    private sealed class ChangeCounts
    {
        public int Added { get; set; }

        public int Removed { get; set; }

        public int Modified { get; set; }

        public object ToObject() => new { added = Added, removed = Removed, modified = Modified };
    }
}

/// <summary>The output of a diff run: the per-entity diff rows and the change-count rollup JSON.</summary>
/// <param name="Diffs">One row per added/removed/modified entity; unchanged entities are omitted.</param>
/// <param name="ChangeCountsJson">The bounded per-type-and-total change-count rollup for the summary.</param>
/// <param name="Diagnostics">
/// Human-readable, secret-free notes about a stable-key collision detected while extracting either
/// snapshot's fields (finding #3) — folded into the discovery audit event so they are visible rather
/// than silent.
/// </param>
public sealed record TopologyDiffResult(
    IReadOnlyList<TopologyEntityDiff> Diffs,
    string ChangeCountsJson,
    IReadOnlyList<string> Diagnostics);
