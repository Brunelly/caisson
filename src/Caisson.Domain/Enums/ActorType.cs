namespace Caisson.Domain.Enums;

/// <summary>The kind of principal that caused an auditable action (see <c>TopologyAuditEvent</c>).</summary>
public enum ActorType
{
    /// <summary>An interactive user identity.</summary>
    User = 0,

    /// <summary>A non-interactive service principal / service account.</summary>
    ServiceAccount,

    /// <summary>The platform itself (e.g. the scheduler or a background process).</summary>
    System,
}
