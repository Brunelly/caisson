namespace Caisson.Domain.Topology;

/// <summary>An observed switch within a snapshot. Owns its <see cref="SwitchPort"/> records.</summary>
public sealed class Switch : ISnapshotScoped
{
    private readonly List<SwitchPort> _ports = new();

    private Switch()
    {
        // EF Core materialization constructor.
    }

    /// <summary>Creates an observed switch record scoped to a snapshot and rack.</summary>
    public Switch(
        Guid id,
        Guid rackId,
        Guid snapshotId,
        DateTime lastSeenAtUtc,
        string? managementIp = null,
        string? serial = null,
        string? model = null,
        string? osVersion = null)
    {
        Id = id;
        RackId = rackId;
        SnapshotId = snapshotId;
        LastSeenAtUtc = lastSeenAtUtc;
        ManagementIp = managementIp;
        Serial = serial;
        Model = model;
        OsVersion = osVersion;
    }

    /// <summary>Primary key.</summary>
    public Guid Id { get; private set; }

    /// <inheritdoc />
    public Guid RackId { get; private set; }

    /// <inheritdoc />
    public Guid SnapshotId { get; private set; }

    /// <summary>Observed management IP address, if known.</summary>
    public string? ManagementIp { get; private set; }

    /// <summary>Observed serial number (natural key, unique per snapshot when present).</summary>
    public string? Serial { get; private set; }

    /// <summary>Observed hardware model, if known.</summary>
    public string? Model { get; private set; }

    /// <summary>Observed OS/firmware version, if known.</summary>
    public string? OsVersion { get; private set; }

    /// <summary>When the switch was last seen during discovery.</summary>
    public DateTime LastSeenAtUtc { get; private set; }

    /// <summary>Ports observed on this switch.</summary>
    public IReadOnlyCollection<SwitchPort> Ports => _ports;

    /// <summary>Adds an observed port to this switch.</summary>
    public void AddPort(SwitchPort port) => _ports.Add(port);
}
