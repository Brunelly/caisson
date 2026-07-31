namespace Caisson.Domain.DesiredState.Diffing;

/// <summary>
/// The deterministic, ordered set of semantic changes between a baseline and a candidate desired-state
/// model (story #171, AC1). Ordering is fully stable across repeated runs with identical inputs (NFR3):
/// VLAN changes always precede port changes, VLANs are ordered by id ascending, and ports are ordered by
/// the ordinal <c>(switchStableKey, portName)</c> tuple. The single flat <see cref="Changes"/> list carries
/// that canonical order; the API layer groups it into VLAN/port buckets for the wire without re-sorting.
/// </summary>
/// <param name="RackId">The rack these changes were computed for.</param>
/// <param name="Changes">The ordered changes (VLANs first, then ports).</param>
public sealed record SemanticDiffResult(Guid RackId, IReadOnlyList<DesiredStateChange> Changes)
{
    /// <summary>Whether the baseline and candidate are semantically identical (no changes).</summary>
    public bool IsEmpty => Changes.Count == 0;
}
