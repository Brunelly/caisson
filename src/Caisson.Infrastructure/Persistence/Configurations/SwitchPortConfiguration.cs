using Caisson.Domain.Topology;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Caisson.Infrastructure.Persistence.Configurations;

/// <summary>Maps the observed <see cref="SwitchPort"/> entity.</summary>
public sealed class SwitchPortConfiguration : IEntityTypeConfiguration<SwitchPort>
{
    public void Configure(EntityTypeBuilder<SwitchPort> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("switch_port");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.PortName).IsRequired().HasMaxLength(128);
        builder.Property(p => p.TaggedVlans).HasColumnType("integer[]");

        // Ownership: the switch owns its ports (cascade) — the single cascade path.
        builder.HasOne<Switch>()
            .WithMany(s => s.Ports)
            .HasForeignKey(p => p.SwitchId)
            .OnDelete(DeleteBehavior.Cascade);

        // Redundant denormalized snapshot/rack references (no cascade) to keep a single cascade path.
        builder.HasOne<TopologySnapshot>()
            .WithMany()
            .HasForeignKey(p => p.SnapshotId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<Rack>()
            .WithMany()
            .HasForeignKey(p => p.RackId)
            .OnDelete(DeleteBehavior.NoAction);

        // AC4: port name is unique within a switch within a snapshot.
        builder.HasIndex(p => new { p.SnapshotId, p.SwitchId, p.PortName })
            .IsUnique()
            .HasDatabaseName("ux_switch_port_snapshot_id_switch_id_port_name");
    }
}
