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
    /// Enforces the append-only invariant: snapshots and snapshot-scoped observed entities may be
    /// inserted (and deleted, e.g. for retention/rollback), but never modified in place.
    /// </summary>
    private void GuardAppendOnly()
    {
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State != EntityState.Modified)
            {
                continue;
            }

            if (entry.Entity is ISnapshotScoped or TopologySnapshot)
            {
                throw new InvalidOperationException(
                    $"Observed state is append-only: '{entry.Entity.GetType().Name}' cannot be " +
                    "modified after it is persisted. Create a new snapshot instead.");
            }
        }
    }
}
