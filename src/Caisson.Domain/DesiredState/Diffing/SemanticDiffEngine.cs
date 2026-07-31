using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Caisson.Domain.NetworkConfig;
using Caisson.Domain.NetworkConfig.Preflight;
using Caisson.Domain.Topology.Diffing;

namespace Caisson.Domain.DesiredState.Diffing;

/// <summary>
/// Computes the deterministic semantic diff between a baseline and a candidate desired-state model
/// (story #171, AC1). Pure and side-effect free: no EF, no IO, no reflection — the diff logic lives in the
/// shared domain so it can be reused by the control-plane API and, later, the appliance agent (technical
/// constraint "keep diff logic in shared domain models"). Compares VLAN catalogue entries by id and
/// access-port intents by <c>(switchStableKey, portName)</c>, emitting an <see cref="DesiredStateChange"/>
/// per add/remove/modify with a reused <see cref="EntityRef"/>, a stable <see cref="DesiredStateChange.ChangeId"/>,
/// and a preformatted human summary matching the story's AC examples verbatim.
/// <para>
/// Output ordering is fully deterministic (NFR3): VLAN changes always precede port changes, VLANs are
/// ordered by id ascending, and ports are ordered by the ordinal escaped <c>(switchStableKey, portName)</c>
/// key. Topology existence and deep-link URLs are deliberately OUT of scope here — the API annotates those.
/// </para>
/// <para>
/// Scope note (ADR 0053): <see cref="PortAccessIntent"/> has no description field in the M1 supported
/// model, so "port description changes if present" is out of the semantic-summary scope; any port
/// description change still surfaces in the raw unified diff.
/// </para>
/// </summary>
public static class SemanticDiffEngine
{
    /// <summary>
    /// Computes the ordered semantic changes transforming <paramref name="baseline"/> into
    /// <paramref name="candidate"/> for rack <paramref name="rackId"/>. A port intent with a <c>null</c>
    /// access VLAN is treated as "no intent" (absent), matching the renderer's "no row = no intent" rule.
    /// </summary>
    public static SemanticDiffResult Diff(
        SupportedDesiredStateModel baseline, SupportedDesiredStateModel candidate, Guid rackId)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);

        var changes = new List<DesiredStateChange>();
        changes.AddRange(DiffVlans(baseline.VlanCatalogue, candidate.VlanCatalogue, rackId));
        changes.AddRange(DiffPorts(baseline.PortIntents, candidate.PortIntents, rackId));
        return new SemanticDiffResult(rackId, changes);
    }

    private static IEnumerable<DesiredStateChange> DiffVlans(
        IReadOnlyList<VlanCatalogueEntry>? baseline, IReadOnlyList<VlanCatalogueEntry>? candidate, Guid rackId)
    {
        var baselineById = ByFirst(baseline, v => v.Id);
        var candidateById = ByFirst(candidate, v => v.Id);

        var ids = new SortedSet<int>(baselineById.Keys);
        ids.UnionWith(candidateById.Keys);

        foreach (var id in ids)
        {
            var hasBaseline = baselineById.TryGetValue(id, out var before);
            var hasCandidate = candidateById.TryGetValue(id, out var after);
            var entityRef = EntityRef.Vlan(rackId, id);

            if (!hasBaseline && hasCandidate)
            {
                yield return VlanChange(
                    DesiredStateChangeKind.Added, entityRef, rackId, id,
                    $"VLAN {Int(id)} added",
                    before: Array.Empty<DesiredStateChangeField>(),
                    after: VlanFields(after!));
            }
            else if (hasBaseline && !hasCandidate)
            {
                yield return VlanChange(
                    DesiredStateChangeKind.Removed, entityRef, rackId, id,
                    $"VLAN {Int(id)} removed",
                    before: VlanFields(before!),
                    after: Array.Empty<DesiredStateChangeField>());
            }
            else if (hasBaseline && hasCandidate)
            {
                var nameChanged = !string.Equals(before!.Name, after!.Name, StringComparison.Ordinal);
                var descriptionChanged = !string.Equals(before.Description, after.Description, StringComparison.Ordinal);
                if (!nameChanged && !descriptionChanged)
                {
                    continue;
                }

                var clauses = new List<string>();
                if (nameChanged)
                {
                    clauses.Add($"name changed {Quote(before.Name)}→{Quote(after.Name)}");
                }

                if (descriptionChanged)
                {
                    clauses.Add($"description changed {Quote(before.Description)}→{Quote(after.Description)}");
                }

                yield return VlanChange(
                    DesiredStateChangeKind.Modified, entityRef, rackId, id,
                    $"VLAN {Int(id)} {string.Join(", ", clauses)}",
                    before: VlanFields(before),
                    after: VlanFields(after));
            }
        }
    }

    private static IEnumerable<DesiredStateChange> DiffPorts(
        IReadOnlyList<PortAccessIntent>? baseline, IReadOnlyList<PortAccessIntent>? candidate, Guid rackId)
    {
        var baselineByKey = PortsByKey(baseline);
        var candidateByKey = PortsByKey(candidate);

        var keys = new List<PortKey>(baselineByKey.Keys);
        foreach (var key in candidateByKey.Keys)
        {
            if (!baselineByKey.ContainsKey(key))
            {
                keys.Add(key);
            }
        }

        keys.Sort(PortKey.OrdinalComparer);

        foreach (var key in keys)
        {
            var hasBaseline = baselineByKey.TryGetValue(key, out var before);
            var hasCandidate = candidateByKey.TryGetValue(key, out var after);
            var entityRef = EntityRef.Port(rackId, key.SwitchStableKey, key.PortName);

            if (!hasBaseline && hasCandidate)
            {
                var vlan = after!.AccessVlanId!.Value;
                yield return PortChange(
                    DesiredStateChangeKind.Added, entityRef, rackId, key,
                    $"Switch {key.SwitchStableKey} Port {key.PortName} accessVlan set to {Int(vlan)}",
                    before: Array.Empty<DesiredStateChangeField>(),
                    after: new[] { new DesiredStateChangeField("accessVlan", Int(vlan)) });
            }
            else if (hasBaseline && !hasCandidate)
            {
                var vlan = before!.AccessVlanId!.Value;
                yield return PortChange(
                    DesiredStateChangeKind.Removed, entityRef, rackId, key,
                    $"Switch {key.SwitchStableKey} Port {key.PortName} accessVlan cleared (was {Int(vlan)})",
                    before: new[] { new DesiredStateChangeField("accessVlan", Int(vlan)) },
                    after: Array.Empty<DesiredStateChangeField>());
            }
            else if (hasBaseline && hasCandidate)
            {
                var beforeVlan = before!.AccessVlanId!.Value;
                var afterVlan = after!.AccessVlanId!.Value;
                if (beforeVlan == afterVlan)
                {
                    continue;
                }

                yield return PortChange(
                    DesiredStateChangeKind.Modified, entityRef, rackId, key,
                    $"Switch {key.SwitchStableKey} Port {key.PortName} accessVlan changed {Int(beforeVlan)}→{Int(afterVlan)}",
                    before: new[] { new DesiredStateChangeField("accessVlan", Int(beforeVlan)) },
                    after: new[] { new DesiredStateChangeField("accessVlan", Int(afterVlan)) });
            }
        }
    }

    private static DesiredStateChange VlanChange(
        DesiredStateChangeKind kind, EntityRef entityRef, Guid rackId, int vlanId, string summary,
        IReadOnlyList<DesiredStateChangeField> before, IReadOnlyList<DesiredStateChangeField> after)
    {
        var changeId = ComputeChangeId(rackId, DesiredStateChangeCategory.Vlan, kind, StableKeys.ForVlan(vlanId), summary);
        return new DesiredStateChange(kind, DesiredStateChangeCategory.Vlan, changeId, entityRef, summary, before, after);
    }

    private static DesiredStateChange PortChange(
        DesiredStateChangeKind kind, EntityRef entityRef, Guid rackId, PortKey key, string summary,
        IReadOnlyList<DesiredStateChangeField> before, IReadOnlyList<DesiredStateChangeField> after)
    {
        var changeId = ComputeChangeId(rackId, DesiredStateChangeCategory.Port, kind, key.OrdinalKey, summary);
        return new DesiredStateChange(kind, DesiredStateChangeCategory.Port, changeId, entityRef, summary, before, after);
    }

    /// <summary>
    /// Derives a stable change id, mirroring <c>DeterministicGuid</c>'s SHA-256/first-16-bytes discipline:
    /// hashes <c>rackId|category|kind|entityKey|summary</c> (each free-form segment escaped so an embedded
    /// separator can never collide two distinct changes) into a <see cref="Guid"/>. The summary encodes the
    /// before→after values, so the same real-world change always hashes to the same id (NFR3).
    /// </summary>
    private static Guid ComputeChangeId(
        Guid rackId, DesiredStateChangeCategory category, DesiredStateChangeKind kind, string entityKey, string summary)
    {
        var canonical = string.Join(
            "|",
            rackId.ToString("N"),
            category.ToString(),
            kind.ToString(),
            entityKey,
            StableKeys.EscapeSegment(summary));

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static IReadOnlyList<DesiredStateChangeField> VlanFields(VlanCatalogueEntry vlan)
        => new[]
        {
            new DesiredStateChangeField("name", vlan.Name),
            new DesiredStateChangeField("description", vlan.Description),
        };

    private static Dictionary<int, VlanCatalogueEntry> ByFirst(
        IReadOnlyList<VlanCatalogueEntry>? source, Func<VlanCatalogueEntry, int> keySelector)
    {
        var map = new Dictionary<int, VlanCatalogueEntry>();
        if (source is null)
        {
            return map;
        }

        foreach (var entry in source)
        {
            map.TryAdd(keySelector(entry), entry);
        }

        return map;
    }

    private static Dictionary<PortKey, PortAccessIntent> PortsByKey(IReadOnlyList<PortAccessIntent>? source)
    {
        var map = new Dictionary<PortKey, PortAccessIntent>();
        if (source is null)
        {
            return map;
        }

        foreach (var intent in source)
        {
            // A null access VLAN is "no intent" and is treated as absent (matches the renderer's "no row = no intent").
            if (intent.AccessVlanId is null)
            {
                continue;
            }

            map.TryAdd(new PortKey(intent.SwitchStableKey, intent.PortName), intent);
        }

        return map;
    }

    private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Renders a value for a summary as single-quoted text; a null value renders as empty quotes.</summary>
    private static string Quote(string? value) => $"'{value ?? string.Empty}'";

    /// <summary>
    /// The identity of a port intent: <c>(switchStableKey, portName)</c>. Carries a precomputed ordinal
    /// escaped composite key so the deterministic port ordering (NFR3) is a single ordinal string compare
    /// and can never confuse the separator with an embedded <c>|</c> in a value (via
    /// <see cref="StableKeys.EscapeSegment(string)"/>).
    /// </summary>
    private readonly record struct PortKey
    {
        public PortKey(string switchStableKey, string portName)
        {
            SwitchStableKey = switchStableKey;
            PortName = portName;
            OrdinalKey = StableKeys.EscapeSegment(switchStableKey) + "|" + StableKeys.EscapeSegment(portName);
        }

        public string SwitchStableKey { get; }

        public string PortName { get; }

        public string OrdinalKey { get; }

        public static IComparer<PortKey> OrdinalComparer { get; } =
            Comparer<PortKey>.Create((left, right) => string.CompareOrdinal(left.OrdinalKey, right.OrdinalKey));
    }
}
