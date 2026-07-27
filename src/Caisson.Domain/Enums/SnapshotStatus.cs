namespace Caisson.Domain.Enums;

/// <summary>The terminal outcome of a discovery run captured on its <c>TopologySnapshot</c>.</summary>
public enum SnapshotStatus
{
    /// <summary>Discovery completed successfully with no partial failures.</summary>
    Completed = 0,

    /// <summary>Discovery produced usable data but some sources failed.</summary>
    PartialSuccess,

    /// <summary>Discovery failed; the snapshot exists for audit but carries little/no observed data.</summary>
    Failed,
}
