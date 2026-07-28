using Caisson.Domain.Topology;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Caisson.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="TopologyAuditEvent"/>: the tamper-evident audit trail for discovery runs and API
/// access (AC3, NFR4). It is not snapshot-scoped, so <c>rack_id</c>/<c>snapshot_id</c> are nullable and
/// carry no cascade. Enums are bounded strings and the details payload bounded <c>jsonb</c>. Indexed by
/// <c>(rack_id, occurred_at desc)</c> for the time-range audit query and by <c>(correlation_id)</c>.
/// </summary>
public sealed class TopologyAuditEventConfiguration : IEntityTypeConfiguration<TopologyAuditEvent>
{
    public void Configure(EntityTypeBuilder<TopologyAuditEvent> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("topology_audit_event");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.OccurredAtUtc).HasColumnType("timestamp with time zone");
        builder.Property(a => a.ActorType).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(a => a.ActorId).IsRequired().HasMaxLength(256);
        builder.Property(a => a.Action).IsRequired().HasMaxLength(128);
        builder.Property(a => a.TargetType).IsRequired().HasMaxLength(64);
        builder.Property(a => a.TargetId).HasMaxLength(256);
        builder.Property(a => a.Result).IsRequired().HasMaxLength(64);
        builder.Property(a => a.DetailsJson)
            .HasColumnType("jsonb")
            .HasMaxLength(TopologyAuditEvent.MaxDetailsJsonLength);

        // Nullable, non-cascading references (API-access events are not snapshot-bound).
        builder.HasOne<Rack>()
            .WithMany()
            .HasForeignKey(a => a.RackId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<TopologySnapshot>()
            .WithMany()
            .HasForeignKey(a => a.SnapshotId)
            .OnDelete(DeleteBehavior.NoAction);

        // AC3: time-range audit query per rack, ordered newest-first; plus correlation lookup.
        builder.HasIndex(a => new { a.RackId, a.OccurredAtUtc })
            .IsDescending(false, true)
            .HasDatabaseName("ix_topology_audit_event_rack_id_occurred_at");
        builder.HasIndex(a => a.CorrelationId)
            .HasDatabaseName("ix_topology_audit_event_correlation_id");
    }
}
