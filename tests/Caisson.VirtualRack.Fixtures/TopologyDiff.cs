using Caisson.Correlation.Results;

namespace Caisson.VirtualRack.Fixtures;

/// <summary>
/// Compares an actual <see cref="TopologyCorrelationResult"/> (from the real drivers/correlation/
/// persistence path) against <see cref="ExpectedTopologyBuilder.Build"/>, producing a human-readable
/// pass/fail diff instead of a single boolean. Reason codes are compared as "every expected code is
/// present" (a superset check) rather than exact-list equality, since an actual result reconstructed from
/// a thinner source (e.g. a single persisted reason code) can legitimately carry fewer entries than the
/// engine's full in-memory reason list.
/// </summary>
public static class TopologyDiff
{
    /// <summary>Returns a human-readable diff; empty when <paramref name="actual"/> matches <paramref name="expected"/>.</summary>
    public static IReadOnlyList<string> Compare(TopologyCorrelationResult actual, TopologyCorrelationResult expected)
    {
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(expected);

        var diff = new List<string>();

        CompareMappings(actual.Mappings, expected.Mappings, diff);
        CompareAmbiguous(actual.AmbiguousMappings, expected.AmbiguousMappings, diff);
        CompareUnmappedNics(actual.UnmappedNics, expected.UnmappedNics, diff);
        CompareUnmappedPorts(actual.UnmappedPorts, expected.UnmappedPorts, diff);

        return diff;
    }

    private static void CompareMappings(
        IReadOnlyList<NicPortMapping> actual, IReadOnlyList<NicPortMapping> expected, List<string> diff)
    {
        var actualByKey = actual.ToDictionary(m => (m.ServerId, m.NicName));
        var expectedKeys = new HashSet<(string, string)>();

        foreach (var e in expected)
        {
            expectedKeys.Add((e.ServerId, e.NicName));
            if (!actualByKey.TryGetValue((e.ServerId, e.NicName), out var a))
            {
                diff.Add($"mapping missing: {e.ServerId}/{e.NicName} expected -> {e.Port.SwitchId}/{e.Port.PortName}");
                continue;
            }

            if (a.Port.SwitchId != e.Port.SwitchId || a.Port.PortName != e.Port.PortName)
            {
                diff.Add($"mapping port mismatch: {e.ServerId}/{e.NicName} expected {e.Port.SwitchId}/{e.Port.PortName}, got {a.Port.SwitchId}/{a.Port.PortName}");
            }

            var missingReasons = e.Port.ReasonCodes.Except(a.Port.ReasonCodes).ToList();
            if (missingReasons.Count > 0)
            {
                diff.Add($"mapping reason codes missing for {e.ServerId}/{e.NicName}: expected {string.Join(", ", missingReasons)}, actual had {string.Join(", ", a.Port.ReasonCodes)}");
            }
        }

        foreach (var a in actual)
        {
            if (!expectedKeys.Contains((a.ServerId, a.NicName)))
            {
                diff.Add($"unexpected mapping: {a.ServerId}/{a.NicName} -> {a.Port.SwitchId}/{a.Port.PortName}");
            }
        }
    }

    private static void CompareAmbiguous(
        IReadOnlyList<AmbiguousNicMapping> actual, IReadOnlyList<AmbiguousNicMapping> expected, List<string> diff)
    {
        var actualByKey = actual.ToDictionary(m => (m.ServerId, m.NicName));
        var expectedKeys = new HashSet<(string, string)>();

        foreach (var e in expected)
        {
            expectedKeys.Add((e.ServerId, e.NicName));
            if (!actualByKey.TryGetValue((e.ServerId, e.NicName), out var a))
            {
                diff.Add($"ambiguous mapping missing: {e.ServerId}/{e.NicName}");
                continue;
            }

            var expectedPorts = e.Candidates.Select(c => (c.SwitchId, c.PortName)).ToHashSet();
            var actualPorts = a.Candidates.Select(c => (c.SwitchId, c.PortName)).ToHashSet();
            if (!expectedPorts.SetEquals(actualPorts))
            {
                diff.Add(
                    $"ambiguous candidate ports mismatch for {e.ServerId}/{e.NicName}: expected [{PortSet(expectedPorts)}], actual [{PortSet(actualPorts)}]");
            }
        }

        foreach (var a in actual)
        {
            if (!expectedKeys.Contains((a.ServerId, a.NicName)))
            {
                diff.Add($"unexpected ambiguous mapping: {a.ServerId}/{a.NicName}");
            }
        }
    }

    private static void CompareUnmappedNics(
        IReadOnlyList<UnmappedNic> actual, IReadOnlyList<UnmappedNic> expected, List<string> diff)
    {
        var actualByKey = actual.ToDictionary(u => (u.ServerId, u.NicName));
        var expectedKeys = new HashSet<(string, string)>();

        foreach (var e in expected)
        {
            expectedKeys.Add((e.ServerId, e.NicName));
            if (!actualByKey.TryGetValue((e.ServerId, e.NicName), out var a))
            {
                diff.Add($"unmapped NIC missing: {e.ServerId}/{e.NicName} expected reasons [{string.Join(", ", e.ReasonCodes)}]");
                continue;
            }

            var missingReasons = e.ReasonCodes.Except(a.ReasonCodes).ToList();
            if (missingReasons.Count > 0)
            {
                diff.Add($"unmapped NIC reason codes missing for {e.ServerId}/{e.NicName}: expected {string.Join(", ", missingReasons)}, actual had {string.Join(", ", a.ReasonCodes)}");
            }
        }

        foreach (var a in actual)
        {
            if (!expectedKeys.Contains((a.ServerId, a.NicName)))
            {
                diff.Add($"unexpected unmapped NIC: {a.ServerId}/{a.NicName} (reasons [{string.Join(", ", a.ReasonCodes)}])");
            }
        }
    }

    private static void CompareUnmappedPorts(
        IReadOnlyList<UnmappedPort> actual, IReadOnlyList<UnmappedPort> expected, List<string> diff)
    {
        var actualByKey = actual.ToDictionary(u => (u.SwitchId, u.PortName));
        var expectedKeys = new HashSet<(string, string)>();

        foreach (var e in expected)
        {
            expectedKeys.Add((e.SwitchId, e.PortName));
            if (!actualByKey.TryGetValue((e.SwitchId, e.PortName), out var a))
            {
                diff.Add($"unmapped port missing: {e.SwitchId}/{e.PortName} expected reasons [{string.Join(", ", e.ReasonCodes)}]");
                continue;
            }

            var missingReasons = e.ReasonCodes.Except(a.ReasonCodes).ToList();
            if (missingReasons.Count > 0)
            {
                diff.Add($"unmapped port reason codes missing for {e.SwitchId}/{e.PortName}: expected {string.Join(", ", missingReasons)}, actual had {string.Join(", ", a.ReasonCodes)}");
            }
        }

        foreach (var a in actual)
        {
            if (!expectedKeys.Contains((a.SwitchId, a.PortName)))
            {
                diff.Add($"unexpected unmapped port: {a.SwitchId}/{a.PortName} (reasons [{string.Join(", ", a.ReasonCodes)}])");
            }
        }
    }

    private static string PortSet(IEnumerable<(string SwitchId, string PortName)> ports)
        => string.Join(", ", ports.Select(p => $"{p.SwitchId}/{p.PortName}").OrderBy(p => p, StringComparer.Ordinal));
}
