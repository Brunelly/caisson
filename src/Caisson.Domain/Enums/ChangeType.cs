namespace Caisson.Domain.Enums;

/// <summary>
/// How an observed entity changed between two consecutive snapshots for the same rack. Unchanged
/// entities produce no diff record (AC2). Persisted as a bounded string.
/// </summary>
public enum ChangeType
{
    /// <summary>The entity is present in the new snapshot but was absent in the previous one.</summary>
    Added = 0,

    /// <summary>The entity was present in the previous snapshot but is absent in the new one.</summary>
    Removed,

    /// <summary>The entity exists in both snapshots but one or more of its fields changed.</summary>
    Modified,
}
