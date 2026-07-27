using Caisson.Domain.Topology;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Caisson.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="TopologySnapshot"/>, the append-only root of the observed graph.</summary>
public sealed class TopologySnapshotConfiguration : IEntityTypeConfiguration<TopologySnapshot>
{
    public void Configure(EntityTypeBuilder<TopologySnapshot> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("topology_snapshot");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.CreatedAtUtc).HasColumnType("timestamp with time zone");
        builder.Property(s => s.CreatedBy).IsRequired().HasMaxLength(256);
        builder.Property(s => s.Source).IsRequired().HasMaxLength(128);
        builder.Property(s => s.SourceVersion).HasMaxLength(64);
        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(s => s.ErrorCode).HasMaxLength(128);
        builder.Property(s => s.ErrorMessage).HasMaxLength(2048);

        // A snapshot belongs to a stable rack; the rack must not be deletable while snapshots exist.
        builder.HasOne<Rack>()
            .WithMany(r => r.Snapshots)
            .HasForeignKey(s => s.RackId)
            .OnDelete(DeleteBehavior.Restrict);

        // AC4: deterministic "latest snapshot per rack" is served by (rack_id, created_at desc).
        builder.HasIndex(s => new { s.RackId, s.CreatedAtUtc })
            .IsDescending(false, true)
            .HasDatabaseName("ix_topology_snapshot_rack_id_created_at");
    }
}
