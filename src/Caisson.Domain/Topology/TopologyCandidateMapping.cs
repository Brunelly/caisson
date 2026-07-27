using Caisson.Domain.Enums;
using Caisson.Domain.ValueObjects;

namespace Caisson.Domain.Topology;

/// <summary>
/// A single candidate correlation of a <see cref="Nic"/> to a <see cref="SwitchPort"/> within a
/// snapshot. Multiple candidates may exist for the same NIC (an ambiguous/conflicting result), ordered
/// by <see cref="Confidence"/> descending. <see cref="SwitchPortId"/> is nullable to represent an
/// <b>unmapped</b> NIC. Each candidate carries a <see cref="ReasonCode"/> and a bounded
/// <see cref="ConfidenceScore"/>, plus optional bounded evidence JSON for debugging.
/// </summary>
public sealed class TopologyCandidateMapping : ISnapshotScoped
{
    /// <summary>Maximum length of the bounded <see cref="EvidenceJson"/> payload.</summary>
    public const int MaxEvidenceJsonLength = 8192;

    private TopologyCandidateMapping()
    {
        // EF Core materialization constructor.
    }

    /// <summary>Creates a candidate mapping record.</summary>
    /// <exception cref="ArgumentException">Thrown when the evidence payload exceeds the bound.</exception>
    public TopologyCandidateMapping(
        Guid id,
        Guid rackId,
        Guid snapshotId,
        Guid nicId,
        ConfidenceScore confidence,
        ReasonCode reasonCode,
        Guid? switchPortId = null,
        string? evidenceJson = null)
    {
        if (evidenceJson is { Length: > MaxEvidenceJsonLength })
        {
            throw new ArgumentException(
                $"Evidence JSON exceeds the {MaxEvidenceJsonLength}-character bound.", nameof(evidenceJson));
        }

        Id = id;
        RackId = rackId;
        SnapshotId = snapshotId;
        NicId = nicId;
        Confidence = confidence;
        ReasonCode = reasonCode;
        SwitchPortId = switchPortId;
        EvidenceJson = evidenceJson;
    }

    /// <summary>Primary key.</summary>
    public Guid Id { get; private set; }

    /// <inheritdoc />
    public Guid RackId { get; private set; }

    /// <inheritdoc />
    public Guid SnapshotId { get; private set; }

    /// <summary>The NIC this candidate maps.</summary>
    public Guid NicId { get; private set; }

    /// <summary>The candidate switch port, or <c>null</c> when the NIC is unmapped.</summary>
    public Guid? SwitchPortId { get; private set; }

    /// <summary>The bounded confidence of this candidate (<c>[0.0, 1.0]</c>).</summary>
    public ConfidenceScore Confidence { get; private set; }

    /// <summary>Why the candidate is unmapped/ambiguous/noteworthy.</summary>
    public ReasonCode ReasonCode { get; private set; }

    /// <summary>Optional bounded evidence JSON (stored as <c>jsonb</c>) for debugging correlation.</summary>
    public string? EvidenceJson { get; private set; }
}
