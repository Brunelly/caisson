namespace Caisson.Correlation.Results;

/// <summary>
/// The complete, deterministic output of a correlation run. Every discovered NIC and every
/// correlation-relevant port is accounted for across the four collections — nothing with signal is
/// silently dropped (AC4). Ordering and scores are stable for identical inputs (NFR2).
/// </summary>
/// <param name="Mappings">Confident 1:1 NIC→port mappings, ordered by (ServerId, NicName).</param>
/// <param name="AmbiguousMappings">NICs with &gt;1 candidate port, ordered by (ServerId, NicName).</param>
/// <param name="UnmappedNics">NICs that could not be correlated, ordered by (ServerId, NicName).</param>
/// <param name="UnmappedPorts">Ports with signal but no NIC, ordered by (SwitchId, PortName).</param>
public sealed record TopologyCorrelationResult(
    IReadOnlyList<NicPortMapping> Mappings,
    IReadOnlyList<AmbiguousNicMapping> AmbiguousMappings,
    IReadOnlyList<UnmappedNic> UnmappedNics,
    IReadOnlyList<UnmappedPort> UnmappedPorts);
