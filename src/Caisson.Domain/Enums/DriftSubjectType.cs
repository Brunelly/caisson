namespace Caisson.Domain.Enums;

/// <summary>
/// The kind of entity a <c>DriftItem</c>'s <c>SubjectKey</c> identifies (story #64, AC1 examples).
/// Persisted as a bounded string.
/// </summary>
public enum DriftSubjectType
{
    /// <summary>A switch port, identified by rack/switch/port name.</summary>
    SwitchPort = 0,

    /// <summary>A server NIC, identified by rack and MAC.</summary>
    ServerNic,

    /// <summary>A server-NIC-to-switch-port logical link.</summary>
    LogicalLink,
}
