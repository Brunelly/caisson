using System.Globalization;
using System.Text;
using System.Text.Json;
using Caisson.Domain.DesiredState;
using Caisson.Domain.Drift;
using Caisson.Domain.Drift.Diffing;
using Caisson.Domain.Enums;
using Caisson.Domain.Topology;

namespace Caisson.Drift;

/// <summary>
/// The pure, deterministic drift computation engine (story #64, AC1). Joins a rack's desired-state tree
/// against its latest observed topology snapshot on NATURAL attributes — never on the persisted
/// <c>StableKey</c> columns, which are not string-comparable across the desired/observed boundary (ADR
/// 0029): rack via <c>DesiredStateVersion.RackSlug == Rack.ExternalKey</c>, switch via
/// <c>DesiredSwitchIntent.SwitchName == Switch.ExternalDeviceKey</c>, port via
/// <c>DesiredPortIntent.PortName == SwitchPort.PortName</c>. The caller is expected to have already
/// confirmed the rack/switch identifiers align (e.g. by resolving both sides for the same rack); this
/// engine only performs the join, it does not validate the alignment itself.
/// </summary>
public static class DriftEngine
{
    /// <summary>
    /// Computes drift between <paramref name="desired"/> and <paramref name="observed"/> for one rack.
    /// Deterministic and idempotent (NFR1): identical inputs always yield identical items, in identical
    /// order, with identical <see cref="DriftItemResult.DriftItemId"/> values.
    /// </summary>
    public static DriftComputationResult Compute(
        DesiredStateTree desired,
        TopologySnapshot observed,
        Guid rackId,
        DateTime computedAtUtc,
        DriftComputationOptions options)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(observed);
        ArgumentNullException.ThrowIfNull(options);

        var rackKey = desired.Version.RackSlug;
        var diagnostics = new List<string>();

        var items = new List<DriftItemResult>();
        items.AddRange(ComputePortDrift(desired, observed, rackId, rackKey, diagnostics));
        items.AddRange(ComputeNicAmbiguityDrift(observed, rackId, rackKey, diagnostics));

        // Canonical ordering BEFORE truncation, so capping stays deterministic (AC1/NFR1).
        var ordered = items
            .OrderBy(i => i.SubjectType.ToString(), StringComparer.Ordinal)
            .ThenBy(i => i.SubjectKey, StringComparer.Ordinal)
            .ThenBy(i => i.DriftType.ToString(), StringComparer.Ordinal)
            .ToList();

        var isTruncated = ordered.Count > options.MaxItemsPerReport;
        var final = isTruncated ? ordered.Take(options.MaxItemsPerReport).ToList() : ordered;

        if (isTruncated)
        {
            diagnostics.Add(
                $"Drift item volume ({ordered.Count}) exceeded the {options.MaxItemsPerReport}-item cap; " +
                $"the report was truncated to the first {options.MaxItemsPerReport} items in canonical order.");
        }

        var hasAmbiguities = final.Any(i => i.DriftType == DriftType.UnknownTopologyMapping);
        var countsJson = SerializeCounts(final);

        return new DriftComputationResult(final, computedAtUtc, countsJson, hasAmbiguities, isTruncated, diagnostics);
    }

    private static IEnumerable<DriftItemResult> ComputePortDrift(
        DesiredStateTree desired, TopologySnapshot observed, Guid rackId, string rackKey, List<string> diagnostics)
    {
        var desiredPorts = IndexDesiredPorts(desired, diagnostics);
        var observedPorts = IndexObservedPorts(observed, diagnostics);

        var allKeys = new HashSet<(string SwitchName, string PortName)>(desiredPorts.Keys);
        allKeys.UnionWith(observedPorts.Keys);

        foreach (var key in allKeys)
        {
            var hasDesired = desiredPorts.TryGetValue(key, out var desiredPort);
            var hasObserved = observedPorts.TryGetValue(key, out var observedSlot) && observedSlot is not null;
            var observedPort = observedSlot?.Port;

            var subjectKey = DriftSubjectKeys.ForSwitchPort(rackKey, key.SwitchName, key.PortName);

            if (hasDesired && !hasObserved)
            {
                yield return BuildPortItem(
                    rackId, DriftType.MissingDesiredEntity, subjectKey,
                    expectedValue: desiredPort!.AccessVlan.ToString(CultureInfo.InvariantCulture),
                    actualValue: null,
                    why: $"Port '{key.PortName}' on switch '{key.SwitchName}' is declared in desired state but was not observed in the latest topology snapshot.");
                continue;
            }

            if (!hasDesired && hasObserved)
            {
                yield return BuildPortItem(
                    rackId, DriftType.ExtraObservedEntity, subjectKey,
                    expectedValue: null,
                    actualValue: FormatPvid(observedPort!.Pvid),
                    why: $"Port '{key.PortName}' on switch '{key.SwitchName}' was observed in the latest topology snapshot but is not declared in desired state.");
                continue;
            }

            if (!hasDesired || !hasObserved)
            {
                continue; // Unreachable: one of the two branches above always fires when only one side is present.
            }

            if (desiredPort!.AccessVlan != observedPort!.Pvid)
            {
                yield return BuildPortItem(
                    rackId, DriftType.AccessVlanMismatch, subjectKey,
                    expectedValue: desiredPort.AccessVlan.ToString(CultureInfo.InvariantCulture),
                    actualValue: FormatPvid(observedPort.Pvid),
                    why: $"Port '{key.PortName}' on switch '{key.SwitchName}' has desired access VLAN {desiredPort.AccessVlan} but observed Pvid {FormatPvid(observedPort.Pvid)}.");
            }

            if (observedPort.TaggedVlans.Length > 0)
            {
                var taggedVlans = JoinBounded(
                    observedPort.TaggedVlans.OrderBy(v => v).Select(v => v.ToString(CultureInfo.InvariantCulture)),
                    ",");
                yield return BuildPortItem(
                    rackId, DriftType.UnexpectedTrunkConfig, subjectKey,
                    expectedValue: "none",
                    actualValue: taggedVlans,
                    why: $"Port '{key.PortName}' on switch '{key.SwitchName}' carries tagged VLAN(s) [{taggedVlans}], but desired state declares no trunk configuration for this port.");
            }

            if ((desiredPort.NeighborSystemName is { Length: > 0 } || desiredPort.NeighborPortId is { Length: > 0 })
                && !HasMatchingNeighbour(desiredPort, observedPort))
            {
                var expectedNeighbour = FormatNeighbour(desiredPort.NeighborSystemName, desiredPort.NeighborPortId);
                var actualNeighbours = observedPort.LldpNeighbours.Count == 0
                    ? "none"
                    : JoinBounded(
                        observedPort.LldpNeighbours
                            .Select(n => FormatNeighbour(n.SystemName, n.PortId))
                            .OrderBy(s => s, StringComparer.Ordinal),
                        "; ");

                yield return BuildPortItem(
                    rackId, DriftType.UnexpectedNeighbour, subjectKey,
                    expectedValue: expectedNeighbour,
                    actualValue: actualNeighbours,
                    why: $"Port '{key.PortName}' on switch '{key.SwitchName}' declares an expected LLDP neighbour ({expectedNeighbour}) that does not match any observed neighbour ({actualNeighbours}).");
            }
        }
    }

    private static IEnumerable<DriftItemResult> ComputeNicAmbiguityDrift(
        TopologySnapshot observed, Guid rackId, string rackKey, List<string> diagnostics)
    {
        if (observed.CandidateMappings.Count == 0)
        {
            yield break;
        }

        var nicsById = observed.Servers.SelectMany(s => s.Nics).ToDictionary(n => n.Id);
        var portNamesById = observed.Switches
            .SelectMany(sw => sw.Ports.Select(p => (SwitchName: sw.ExternalDeviceKey, Port: p)))
            .ToDictionary(t => t.Port.Id, t => (t.SwitchName, t.Port.PortName));

        foreach (var group in observed.CandidateMappings.GroupBy(m => m.NicId))
        {
            if (!nicsById.TryGetValue(group.Key, out var nic))
            {
                diagnostics.Add($"Candidate mapping references unknown NicId '{group.Key}'; skipped.");
                continue;
            }

            var distinctPorts = group
                .Where(m => m.SwitchPortId is not null)
                .Select(m => m.SwitchPortId!.Value)
                .Distinct()
                .ToList();

            var hasConflictReason = group.Any(m => m.ReasonCode is
                ReasonCode.MultipleMacPorts or ReasonCode.DuplicateMac or ReasonCode.ConflictingMacEvidence);

            if (distinctPorts.Count == 1 && !hasConflictReason)
            {
                continue; // A single, uncontested candidate: no M1 desired NIC-level intent to compare against.
            }

            var subjectKey = DriftSubjectKeys.ForServerNic(rackKey, nic.MacPrimary.Value);
            var candidateNames = distinctPorts
                .Select(portId => portNamesById.TryGetValue(portId, out var name)
                    ? $"{name.SwitchName}/{name.PortName}"
                    : portId.ToString())
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();

            var why = distinctPorts.Count == 0
                ? $"NIC '{nic.MacPrimary.Value}' has no candidate switch port (unmapped)."
                : $"NIC '{nic.MacPrimary.Value}' could not be uniquely correlated to a switch port ({candidateNames.Count} candidate port(s)).";

            var details = JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["candidatePorts"] = candidateNames,
                ["reasonCodes"] = group.Select(m => m.ReasonCode.ToString()).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList(),
            });

            yield return new DriftItemResult(
                DeterministicGuid.Compute(rackId, DriftType.UnknownTopologyMapping, DriftSubjectType.ServerNic, subjectKey, null, null),
                DriftType.UnknownTopologyMapping,
                DriftSeverityRules.For(DriftType.UnknownTopologyMapping),
                Actionable: false,
                DriftSubjectType.ServerNic,
                subjectKey,
                ExpectedValue: null,
                ActualValue: null,
                Why: why,
                DetailsJson: details);
        }
    }

    private static DriftItemResult BuildPortItem(
        Guid rackId, DriftType driftType, string subjectKey, string? expectedValue, string? actualValue, string why)
        => new(
            DeterministicGuid.Compute(rackId, driftType, DriftSubjectType.SwitchPort, subjectKey, expectedValue, actualValue),
            driftType,
            DriftSeverityRules.For(driftType),
            Actionable: true,
            DriftSubjectType.SwitchPort,
            subjectKey,
            expectedValue,
            actualValue,
            why,
            DetailsJson: null);

    private static Dictionary<(string SwitchName, string PortName), DesiredPortIntent> IndexDesiredPorts(
        DesiredStateTree desired, List<string> diagnostics)
    {
        var switchNameById = desired.Switches.ToDictionary(s => s.Id, s => s.SwitchName);
        var result = new Dictionary<(string, string), DesiredPortIntent>();

        foreach (var port in desired.Ports)
        {
            if (!switchNameById.TryGetValue(port.DesiredSwitchIntentId, out var switchName))
            {
                diagnostics.Add($"Desired port '{port.PortName}' references unknown switch intent '{port.DesiredSwitchIntentId}'; skipped.");
                continue;
            }

            var key = (switchName, port.PortName);
            if (!result.TryAdd(key, port))
            {
                diagnostics.Add($"Duplicate desired port key ('{switchName}', '{port.PortName}'); the first occurrence was kept.");
            }
        }

        return result;
    }

    private static Dictionary<(string SwitchName, string PortName), (Switch Switch, SwitchPort Port)?> IndexObservedPorts(
        TopologySnapshot observed, List<string> diagnostics)
    {
        var result = new Dictionary<(string, string), (Switch, SwitchPort)?>();

        foreach (var sw in observed.Switches)
        {
            foreach (var port in sw.Ports)
            {
                var key = (sw.ExternalDeviceKey, port.PortName);
                if (!result.TryAdd(key, (sw, port)))
                {
                    diagnostics.Add($"Duplicate observed port key ('{sw.ExternalDeviceKey}', '{port.PortName}'); the first occurrence was kept.");
                }
            }
        }

        return result;
    }

    private static bool HasMatchingNeighbour(DesiredPortIntent desiredPort, SwitchPort observedPort)
        => observedPort.LldpNeighbours.Any(n =>
            (string.IsNullOrEmpty(desiredPort.NeighborSystemName) || string.Equals(n.SystemName, desiredPort.NeighborSystemName, StringComparison.Ordinal))
            && (string.IsNullOrEmpty(desiredPort.NeighborPortId) || string.Equals(n.PortId, desiredPort.NeighborPortId, StringComparison.Ordinal)));

    private static string FormatNeighbour(string? systemName, string? portId)
        => $"systemName={systemName ?? "(any)"},portId={portId ?? "(any)"}";

    private static string FormatPvid(int? pvid) => pvid?.ToString(CultureInfo.InvariantCulture) ?? "none";

    /// <summary>
    /// Joins device-controlled, unbounded-cardinality observed values (e.g. a trunk port's tagged VLANs,
    /// or a port's LLDP neighbours) into a single string that is guaranteed to stay well under
    /// <see cref="DriftSchema.MaxActualValueLength"/>, appending a "+N more" summary instead of the
    /// remaining items when the full list would overflow. Without this, a single legitimate
    /// high-cardinality port (e.g. a "trunk all VLANs 1-4094" uplink) would make the
    /// <c>DriftItem</c> constructor throw, failing the WHOLE rack's report (M1 device-controlled-volume
    /// invariant) instead of just degrading this one item.
    /// </summary>
    private static string JoinBounded(IEnumerable<string> items, string separator)
    {
        const int maxJoinedLength = 900; // Margin under MaxActualValueLength (1024) for the "+N more" suffix,
                                          // and — combined with the surrounding fixed why-text — under MaxWhyLength (2048).
        var all = items.ToList();
        var builder = new StringBuilder();
        var includedCount = 0;

        foreach (var item in all)
        {
            var prefixLength = builder.Length == 0 ? 0 : separator.Length;
            if (builder.Length + prefixLength + item.Length > maxJoinedLength)
            {
                break;
            }

            if (builder.Length > 0)
            {
                builder.Append(separator);
            }

            builder.Append(item);
            includedCount++;
        }

        if (includedCount < all.Count)
        {
            if (builder.Length > 0)
            {
                builder.Append(separator);
            }

            builder.Append($"...(+{all.Count - includedCount} more, {all.Count} total)");
        }

        return builder.ToString();
    }

    private static string SerializeCounts(IReadOnlyList<DriftItemResult> items)
    {
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var severity in Enum.GetValues<DriftSeverity>())
        {
            counts[severity.ToString()] = 0;
        }

        foreach (var item in items)
        {
            counts[item.Severity.ToString()]++;
        }

        var body = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["total"] = items.Count,
        };
        foreach (var (severity, count) in counts)
        {
            body[severity] = count;
        }

        return JsonSerializer.Serialize(body);
    }
}
