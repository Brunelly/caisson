using Caisson.Domain.Auditing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Caisson.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="AuditOutboxMessage"/> (story #308, ADR 0064). Bounded audit columns mirror
/// <see cref="TopologyAuditEventConfiguration"/> exactly, stored as real columns (not an opaque blob) so
/// the dispatcher's INSERT into <c>topology_audit_event</c> is a plain projection and the target table's
/// own constraints still apply. A partial index on <c>(status, available_at_utc)</c> covers the
/// dispatcher's due-row claim in claim order without scanning dispatched/poisoned rows.
/// </summary>
public sealed class AuditOutboxMessageConfiguration : IEntityTypeConfiguration<AuditOutboxMessage>
{
    public void Configure(EntityTypeBuilder<AuditOutboxMessage> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("audit_outbox");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.OccurredAtUtc).HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(x => x.ActorType).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(x => x.ActorId).IsRequired().HasMaxLength(AuditOutboxMessage.MaxActorIdLength);
        builder.Property(x => x.Action).IsRequired().HasMaxLength(AuditOutboxMessage.MaxActionLength);
        builder.Property(x => x.TargetType).IsRequired().HasMaxLength(AuditOutboxMessage.MaxTargetTypeLength);
        builder.Property(x => x.TargetId).HasMaxLength(AuditOutboxMessage.MaxTargetIdLength);
        builder.Property(x => x.CorrelationId).IsRequired();
        builder.Property(x => x.Result).IsRequired().HasMaxLength(AuditOutboxMessage.MaxResultLength);
        builder.Property(x => x.DetailsJson).HasColumnType("jsonb").HasMaxLength(AuditOutboxMessage.MaxDetailsJsonLength);

        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(x => x.AvailableAtUtc).HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(x => x.AttemptCount).IsRequired();
        builder.Property(x => x.LeaseUntilUtc).HasColumnType("timestamp with time zone");
        builder.Property(x => x.ClaimedBy).HasMaxLength(AuditOutboxMessage.MaxClaimedByLength);
        builder.Property(x => x.DispatchedAtUtc).HasColumnType("timestamp with time zone");
        builder.Property(x => x.FailureCode).HasMaxLength(AuditOutboxMessage.MaxFailureCodeLength);

        // No FK to rack/snapshot: unlike topology_audit_event this is a short-lived staging row, and the
        // referenced rack/snapshot may already be gone by dispatch time in pathological retention cases —
        // dispatch must never be blocked by a foreign key.

        // Dispatcher's claim query: due Pending rows in claim order. Partial so dispatched/poisoned rows
        // (the overwhelming majority once the system has run a while) never bloat this index.
        builder.HasIndex(x => new { x.Status, x.AvailableAtUtc })
            .HasDatabaseName("ix_audit_outbox_status_available_at")
            .HasFilter("status = 'Pending'");
    }
}
