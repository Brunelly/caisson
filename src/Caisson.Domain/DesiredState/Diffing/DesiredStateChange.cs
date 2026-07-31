using Caisson.Domain.NetworkConfig.Preflight;

namespace Caisson.Domain.DesiredState.Diffing;

/// <summary>The kind of change a <see cref="DesiredStateChange"/> represents (story #171, AC1).</summary>
public enum DesiredStateChangeKind
{
    /// <summary>The entity is present in the candidate but absent in the baseline.</summary>
    Added,

    /// <summary>The entity is present in the baseline but absent in the candidate.</summary>
    Removed,

    /// <summary>The entity exists in both, but one or more of its fields changed.</summary>
    Modified,
}

/// <summary>Which category of authored entity a <see cref="DesiredStateChange"/> concerns (story #171, AC1).</summary>
public enum DesiredStateChangeCategory
{
    /// <summary>A VLAN catalogue entry.</summary>
    Vlan,

    /// <summary>A per-port access-VLAN intent.</summary>
    Port,
}

/// <summary>
/// One semantic change between a baseline and a candidate desired-state model (story #171, AC1). A pure
/// value carrier: it captures WHAT changed (kind + category), the before/after field snapshots, a stable
/// machine identity (<see cref="ChangeId"/>), a reused <see cref="Preflight.EntityRef"/> for topology
/// deep-linking, and a preformatted human <see cref="Summary"/> string matching the story's AC examples
/// verbatim. Topology existence and URLs are deliberately NOT modelled here — those are an API/UI concern
/// annotated later.
/// </summary>
/// <param name="Kind">Whether the entity was added, removed, or modified.</param>
/// <param name="Category">Whether the change concerns a VLAN or a port.</param>
/// <param name="ChangeId">
/// A stable, deterministic identifier for this change, derived from the change's identity via
/// <see cref="Drift.Diffing.DeterministicGuid"/>. Identical inputs always yield the identical id, so
/// automation can key off it across re-runs (NFR3).
/// </param>
/// <param name="EntityRef">A reused pointer to the rack entity this change concerns (VLAN by id / port by switch+name).</param>
/// <param name="Summary">The preformatted, human-readable one-line summary (e.g. <c>"VLAN 100 added"</c>).</param>
/// <param name="Before">The before-state field tuples (empty for an add), each <c>(field, value)</c>.</param>
/// <param name="After">The after-state field tuples (empty for a remove), each <c>(field, value)</c>.</param>
public sealed record DesiredStateChange(
    DesiredStateChangeKind Kind,
    DesiredStateChangeCategory Category,
    Guid ChangeId,
    EntityRef EntityRef,
    string Summary,
    IReadOnlyList<DesiredStateChangeField> Before,
    IReadOnlyList<DesiredStateChangeField> After);

/// <summary>One named field snapshot within a <see cref="DesiredStateChange"/>'s before/after state.</summary>
/// <param name="Field">The field name (e.g. <c>"name"</c>, <c>"accessVlan"</c>).</param>
/// <param name="Value">The field value as a string, or <c>null</c> when absent.</param>
public sealed record DesiredStateChangeField(string Field, string? Value);
