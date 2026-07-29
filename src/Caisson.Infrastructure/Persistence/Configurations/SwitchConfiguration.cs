using Caisson.Domain.Topology;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Caisson.Infrastructure.Persistence.Configurations;

/// <summary>Maps the observed <see cref="Switch"/> entity.</summary>
public sealed class SwitchConfiguration : IEntityTypeConfiguration<Switch>
{
    public void Configure(EntityTypeBuilder<Switch> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("switch");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.ExternalDeviceKey).IsRequired().HasMaxLength(256);
        builder.Property(s => s.ManagementIp).HasMaxLength(64);
        builder.Property(s => s.Serial).HasMaxLength(128);
        builder.Property(s => s.Model).HasMaxLength(128);
        builder.Property(s => s.OsVersion).HasMaxLength(128);
        builder.Property(s => s.LastSeenAtUtc).HasColumnType("timestamp with time zone");

        // Ownership: the snapshot owns its switches (cascade). This is the single cascade path.
        builder.HasOne<TopologySnapshot>()
            .WithMany(s => s.Switches)
            .HasForeignKey(s => s.SnapshotId)
            .OnDelete(DeleteBehavior.Cascade);

        // Redundant denormalized rack reference for isolation/indexing (no cascade).
        builder.HasOne<Rack>()
            .WithMany()
            .HasForeignKey(s => s.RackId)
            .OnDelete(DeleteBehavior.NoAction);

        // AC4 indexes.
        builder.HasIndex(s => new { s.SnapshotId, s.RackId });
        builder.HasIndex(s => new { s.SnapshotId, s.Serial })
            .IsUnique()
            .HasFilter("serial IS NOT NULL")
            .HasDatabaseName("ux_switch_snapshot_id_serial");
    }
}
