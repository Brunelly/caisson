using Caisson.Domain.Enums;

namespace Caisson.Domain.Topology;

/// <summary>
/// An immutable, append-only record of a single discovery run for one rack. A snapshot is the root of
/// the denormalized observed graph: every observed entity references it via <c>snapshot_id</c>. Once
/// persisted a snapshot's content is never mutated in place — the <c>DbContext</c> enforces this.
/// Snapshots carry full provenance (who/what created them, source driver, correlation id, outcome).
/// </summary>
public sealed class TopologySnapshot
{
    private readonly List<Switch> _switches = new();
    private readonly List<Server> _servers = new();
    private readonly List<Vlan> _vlans = new();
    private readonly List<TopologyCandidateMapping> _candidateMappings = new();

    private TopologySnapshot()
    {
        // EF Core materialization constructor.
        CreatedBy = null!;
        Source = null!;
    }

    /// <summary>Creates a new snapshot with its provenance/audit metadata.</summary>
    public TopologySnapshot(
        Guid id,
        Guid rackId,
        DateTime createdAtUtc,
        string createdBy,
        string source,
        Guid correlationId,
        SnapshotStatus status,
        string? sourceVersion = null,
        string? errorCode = null,
        string? errorMessage = null)
    {
        Id = id;
        RackId = rackId;
        CreatedAtUtc = createdAtUtc;
        CreatedBy = createdBy;
        Source = source;
        SourceVersion = sourceVersion;
        CorrelationId = correlationId;
        Status = status;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
    }

    /// <summary>Snapshot primary key; also breaks exact-timestamp ties for latest selection.</summary>
    public Guid Id { get; private set; }

    /// <summary>The stable rack this snapshot belongs to.</summary>
    public Guid RackId { get; private set; }

    /// <summary>When the snapshot was created (primary sort key for latest selection).</summary>
    public DateTime CreatedAtUtc { get; private set; }

    /// <summary>The service account or user that initiated the discovery run.</summary>
    public string CreatedBy { get; private set; }

    /// <summary>The source driver that produced the observations (audit/provenance).</summary>
    public string Source { get; private set; }

    /// <summary>Optional version of the source driver.</summary>
    public string? SourceVersion { get; private set; }

    /// <summary>Correlation id linking this snapshot to the originating discovery run.</summary>
    public Guid CorrelationId { get; private set; }

    /// <summary>The terminal outcome of the discovery run.</summary>
    public SnapshotStatus Status { get; private set; }

    /// <summary>Optional machine-readable error code when the run did not fully succeed.</summary>
    public string? ErrorCode { get; private set; }

    /// <summary>Optional human-readable error message when the run did not fully succeed.</summary>
    public string? ErrorMessage { get; private set; }

    /// <summary>Switches observed in this snapshot.</summary>
    public IReadOnlyCollection<Switch> Switches => _switches;

    /// <summary>Servers observed in this snapshot.</summary>
    public IReadOnlyCollection<Server> Servers => _servers;

    /// <summary>VLANs observed in this snapshot.</summary>
    public IReadOnlyCollection<Vlan> Vlans => _vlans;

    /// <summary>Candidate NIC-to-port mappings inferred for this snapshot.</summary>
    public IReadOnlyCollection<TopologyCandidateMapping> CandidateMappings => _candidateMappings;

    /// <summary>Optional derived change summary against the previous snapshot.</summary>
    public TopologyChangeSummary? ChangeSummary { get; private set; }

    /// <summary>Adds an observed switch to this snapshot's graph.</summary>
    public void AddSwitch(Switch observedSwitch) => _switches.Add(observedSwitch);

    /// <summary>Adds an observed server to this snapshot's graph.</summary>
    public void AddServer(Server server) => _servers.Add(server);

    /// <summary>Adds an observed VLAN to this snapshot's graph.</summary>
    public void AddVlan(Vlan vlan) => _vlans.Add(vlan);

    /// <summary>Adds an inferred candidate mapping to this snapshot's graph.</summary>
    public void AddCandidateMapping(TopologyCandidateMapping mapping) => _candidateMappings.Add(mapping);

    /// <summary>Attaches the derived change summary for this snapshot.</summary>
    public void SetChangeSummary(TopologyChangeSummary summary) => ChangeSummary = summary;
}
