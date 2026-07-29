using Caisson.Domain.DesiredState;
using Caisson.Domain.Discovery;
using Caisson.Domain.Topology;
using Microsoft.EntityFrameworkCore;

namespace Caisson.Infrastructure.Persistence;

/// <summary>
/// The observed-state persistence context. Maps the append-only, denormalized-per-snapshot topology
/// graph to PostgreSQL (snake_case naming). Append-only immutability is enforced here: any attempt to
/// persist a modification to a snapshot or any snapshot-scoped entity is rejected.
/// </summary>
public sealed class CaissonDbContext : DbContext
{
    public CaissonDbContext(DbContextOptions<CaissonDbContext> options)
        : base(options)
    {
    }

    /// <summary>Stable rack registry.</summary>
    public DbSet<Rack> Racks => Set<Rack>();

    /// <summary>Append-only discovery snapshots.</summary>
    public DbSet<TopologySnapshot> Snapshots => Set<TopologySnapshot>();

    /// <summary>Observed switches.</summary>
    public DbSet<Switch> Switches => Set<Switch>();

    /// <summary>Observed switch ports.</summary>
    public DbSet<SwitchPort> SwitchPorts => Set<SwitchPort>();

    /// <summary>Observed servers.</summary>
    public DbSet<Server> Servers => Set<Server>();

    /// <summary>Observed NICs.</summary>
    public DbSet<Nic> Nics => Set<Nic>();

    /// <summary>Observed MAC addresses.</summary>
    public DbSet<MacAddress> MacAddresses => Set<MacAddress>();

    /// <summary>Observed VLANs.</summary>
    public DbSet<Vlan> Vlans => Set<Vlan>();

    /// <summary>Observed LLDP neighbours.</summary>
    public DbSet<LldpNeighbour> LldpNeighbours => Set<LldpNeighbour>();

    /// <summary>Inferred candidate NIC-to-port mappings.</summary>
    public DbSet<TopologyCandidateMapping> CandidateMappings => Set<TopologyCandidateMapping>();

    /// <summary>Derived per-snapshot change summaries.</summary>
    public DbSet<TopologyChangeSummary> ChangeSummaries => Set<TopologyChangeSummary>();

    /// <summary>Durable, append-only per-entity diffs between consecutive snapshots.</summary>
    public DbSet<TopologyEntityDiff> EntityDiffs => Set<TopologyEntityDiff>();

    /// <summary>Tamper-evident audit trail for discovery runs and API access.</summary>
    public DbSet<TopologyAuditEvent> AuditEvents => Set<TopologyAuditEvent>();

    /// <summary>Durable, resumable discovery orchestration jobs (story #8).</summary>
    public DbSet<DiscoveryJob> DiscoveryJobs => Set<DiscoveryJob>();

    /// <summary>Per-step status rows for discovery jobs.</summary>
    public DbSet<DiscoveryJobStep> DiscoveryJobSteps => Set<DiscoveryJobStep>();

    /// <summary>Per-rack recurring discovery schedules.</summary>
    public DbSet<RackDiscoverySchedule> RackDiscoverySchedules => Set<RackDiscoverySchedule>();

    /// <summary>Git-backed desired-state ingestion runs (story #62).</summary>
    public DbSet<DesiredStateIngestionRun> DesiredStateIngestionRuns => Set<DesiredStateIngestionRun>();

    /// <summary>Append-only per-rack-per-commit desired-state versions.</summary>
    public DbSet<DesiredStateVersion> DesiredStateVersions => Set<DesiredStateVersion>();

    /// <summary>Append-only rack-level desired-state intent nodes.</summary>
    public DbSet<DesiredRackIntent> DesiredRackIntents => Set<DesiredRackIntent>();

    /// <summary>Append-only switch-level desired-state intent nodes.</summary>
    public DbSet<DesiredSwitchIntent> DesiredSwitchIntents => Set<DesiredSwitchIntent>();

    /// <summary>Append-only port-level desired-state intent nodes.</summary>
    public DbSet<DesiredPortIntent> DesiredPortIntents => Set<DesiredPortIntent>();

    /// <summary>Append-only desired-state schema validation errors.</summary>
    public DbSet<DesiredStateValidationError> DesiredStateValidationErrors => Set<DesiredStateValidationError>();

    /// <inheritdoc />
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        base.OnConfiguring(optionsBuilder);

        // Apply snake_case for all tables/columns/keys/indexes regardless of how the provider was
        // configured (design-time factory, tests, or future DI host). See ADR 0005.
        optionsBuilder.UseSnakeCaseNamingConvention();
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CaissonDbContext).Assembly);
    }

    /// <inheritdoc />
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        GuardAppendOnly();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    /// <inheritdoc />
    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        GuardAppendOnly();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// Enforces the append-only invariants:
    /// <list type="bullet">
    /// <item><description>Snapshots and snapshot-scoped observed entities may be inserted (and deleted,
    /// e.g. for retention/rollback) but never <b>modified</b> in place.</description></item>
    /// <item><description><see cref="IAppendOnly"/> records (audit events, per-entity diffs) are
    /// tamper-evident: they may never be modified <b>or deleted</b> (NFR4). The database enforces this as
    /// well via a trigger, so it holds even against raw SQL.</description></item>
    /// </list>
    /// </summary>
    private void GuardAppendOnly()
    {
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State is EntityState.Modified && entry.Entity is ISnapshotScoped or TopologySnapshot)
            {
                throw new InvalidOperationException(
                    $"Observed state is append-only: '{entry.Entity.GetType().Name}' cannot be " +
                    "modified after it is persisted. Create a new snapshot instead.");
            }

            if (entry.State is EntityState.Modified or EntityState.Deleted && entry.Entity is IAppendOnly)
            {
                throw new InvalidOperationException(
                    $"'{entry.Entity.GetType().Name}' is tamper-evident and append-only: it cannot be " +
                    $"{entry.State.ToString().ToLowerInvariant()} after it is persisted (NFR4).");
            }
        }
    }
}
