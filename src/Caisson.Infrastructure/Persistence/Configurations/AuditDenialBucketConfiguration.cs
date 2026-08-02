using Caisson.Domain.Auditing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Caisson.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="AuditDenialBucket"/> (story #308, ADR 0064). The unique index on
/// <c>(actor_id, endpoint, outcome, window_start_at_utc)</c> IS the bucket lookup: the writer inserts with
/// <c>ON CONFLICT DO NOTHING</c> then locks the (new-or-existing) row, which is what makes the first-N
/// durable-denial count global across API replicas. <c>window_end_at_utc</c> is indexed for the periodic
/// expiry sweep that bounds <c>DenialMaxActiveBuckets</c>.
/// </summary>
public sealed class AuditDenialBucketConfiguration : IEntityTypeConfiguration<AuditDenialBucket>
{
    /// <summary>The named unique constraint enforcing one bucket per (actor, endpoint, outcome, window).</summary>
    public const string BucketKeyUniqueConstraint = "ux_audit_denial_bucket_key";

    public void Configure(EntityTypeBuilder<AuditDenialBucket> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("audit_denial_bucket");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.ActorId).IsRequired().HasMaxLength(AuditDenialBucket.MaxActorIdLength);
        builder.Property(x => x.ActorType).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(x => x.Endpoint).IsRequired().HasMaxLength(AuditDenialBucket.MaxEndpointLength);
        builder.Property(x => x.Outcome).IsRequired().HasMaxLength(AuditDenialBucket.MaxOutcomeLength);
        builder.Property(x => x.WindowStartAtUtc).HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(x => x.WindowEndAtUtc).HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(x => x.FirstSeenAtUtc).HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(x => x.LastSeenAtUtc).HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(x => x.DurableCount).IsRequired();

        builder.HasIndex(x => new { x.ActorId, x.Endpoint, x.Outcome, x.WindowStartAtUtc })
            .IsUnique()
            .HasDatabaseName(BucketKeyUniqueConstraint);

        builder.HasIndex(x => x.WindowEndAtUtc)
            .HasDatabaseName("ix_audit_denial_bucket_window_end_at");
    }
}
