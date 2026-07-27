using Caisson.Domain.Topology;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Caisson.Infrastructure.Persistence.Configurations;

/// <summary>Maps the observed <see cref="LldpNeighbour"/> entity.</summary>
public sealed class LldpNeighbourConfiguration : IEntityTypeConfiguration<LldpNeighbour>
{
    public void Configure(EntityTypeBuilder<LldpNeighbour> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("lldp_neighbour");
        builder.HasKey(l => l.Id);

        builder.Property(l => l.ChassisId).IsRequired().HasMaxLength(256);
        builder.Property(l => l.PortId).IsRequired().HasMaxLength(256);
        builder.Property(l => l.SystemName).HasMaxLength(256);
        builder.Property(l => l.MgmtAddress).HasMaxLength(128);

        // Ownership: the switch port owns its LLDP neighbours (cascade) — the single cascade path.
        builder.HasOne<SwitchPort>()
            .WithMany(p => p.LldpNeighbours)
            .HasForeignKey(l => l.SwitchPortId)
            .OnDelete(DeleteBehavior.Cascade);

        // Redundant denormalized snapshot/rack references (no cascade).
        builder.HasOne<TopologySnapshot>()
            .WithMany()
            .HasForeignKey(l => l.SnapshotId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<Rack>()
            .WithMany()
            .HasForeignKey(l => l.RackId)
            .OnDelete(DeleteBehavior.NoAction);

        // AC4 index.
        builder.HasIndex(l => new { l.SnapshotId, l.SwitchPortId });
    }
}
