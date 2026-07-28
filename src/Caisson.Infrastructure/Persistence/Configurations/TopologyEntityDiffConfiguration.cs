using Caisson.Domain.Topology;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Caisson.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="TopologyEntityDiff"/>: the durable per-entity diff scoped to its "to" snapshot
/// (AC2). Enums are stored as bounded strings and the diff payload as bounded <c>jsonb</c>. A UNIQUE
/// <c>(snapshot_id, entity_type, entity_stable_key)</c> index is the defence-in-depth backstop for diff
/// idempotency; <c>(rack_id, snapshot_id)</c> serves entity-history queries.
/// </summary>
public sealed class TopologyEntityDiffConfiguration : IEntityTypeConfiguration<TopologyEntityDiff>
{
    public void Configure(EntityTypeBuilder<TopologyEntityDiff> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("topology_entity_diff");
        builder.HasKey(d => d.Id);

        builder.Property(d => d.EntityType).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(d => d.EntityStableKey).IsRequired().HasMaxLength(512);
        builder.Property(d => d.ChangeType).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(d => d.DiffPayloadJson)
            .HasColumnType("jsonb")
            .HasMaxLength(TopologyEntityDiff.MaxDiffPayloadJsonLength)
            .IsRequired();
        builder.Property(d => d.CreatedAtUtc).HasColumnType("timestamp with time zone");

        // Ownership: the "to" snapshot owns its diff rows (cascade) — the single cascade path.
        builder.HasOne<TopologySnapshot>()
            .WithMany()
            .HasForeignKey(d => d.SnapshotId)
            .OnDelete(DeleteBehavior.Cascade);

        // Denormalized references (no cascade) to keep a single cascade path.
        builder.HasOne<TopologySnapshot>()
            .WithMany()
            .HasForeignKey(d => d.PreviousSnapshotId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<Rack>()
            .WithMany()
            .HasForeignKey(d => d.RackId)
            .OnDelete(DeleteBehavior.NoAction);

        // AC2: idempotency backstop — one diff row per (to-snapshot, entity type, stable key).
        builder.HasIndex(d => new { d.SnapshotId, d.EntityType, d.EntityStableKey })
            .IsUnique()
            .HasDatabaseName("ux_topology_entity_diff_snapshot_entity_key");

        // Entity-history / per-snapshot diff listing.
        builder.HasIndex(d => new { d.RackId, d.SnapshotId })
            .HasDatabaseName("ix_topology_entity_diff_rack_id_snapshot_id");
        builder.HasIndex(d => new { d.RackId, d.EntityType, d.EntityStableKey })
            .HasDatabaseName("ix_topology_entity_diff_rack_id_entity_type_entity_stable_key");
    }
}
