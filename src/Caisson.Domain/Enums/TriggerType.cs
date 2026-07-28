namespace Caisson.Domain.Enums;

/// <summary>How a discovery run that produced a <c>TopologySnapshot</c> was initiated.</summary>
public enum TriggerType
{
    /// <summary>The run was started by the scheduler.</summary>
    Scheduled = 0,

    /// <summary>The run was requested on demand by an operator or automation.</summary>
    OnDemand,
}
