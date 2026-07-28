namespace Caisson.Domain.Enums;

/// <summary>
/// The fixed, ordered set of steps a discovery job executes (story #8, AC1). The runner walks these in
/// declaration order; each is persisted as a bounded string on its <c>DiscoveryJobStep</c> row.
/// </summary>
public enum DiscoveryStepName
{
    /// <summary>Read-only switch discovery across the rack's switches.</summary>
    SwitchDiscovery = 0,

    /// <summary>Read-only BMC/server discovery across the rack's servers.</summary>
    BmcDiscovery,

    /// <summary>Pure correlation of the observed switch/BMC output into a topology mapping.</summary>
    Correlation,

    /// <summary>Persistence of the correlated snapshot via the story-7 ingestion service.</summary>
    Persistence,
}
