using Caisson.Domain.Drift.Apply;
using Caisson.Domain.Topology;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Caisson.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="DriftApplyJob"/> (story #65): a mutable, registry-style job row (not append-only, so
/// <c>GuardAppendOnly</c> ignores it) — mirrors <see cref="DiscoveryJobConfiguration"/> exactly. Optimistic
/// concurrency is the Npgsql <c>xmin</c> system column (no new schema column). <see cref="DriftApplyJob.DriftItemId"/>
/// is deliberately a plain indexed value, NOT a foreign key to <c>drift_item</c> (that row is
/// upserted/pruned by recompute and may legitimately be gone by the time the job runs — the stale-drift
/// condition itself, not a referential-integrity error). A partial-unique index on
/// <c>(rack_id, drift_item_id)</c> filtered to non-terminal statuses DB-enforces "at most one active job
/// per drift item" (AC5), backing the idempotent create (201/202) behaviour.
/// </summary>
public sealed class DriftApplyJobConfiguration : IEntityTypeConfiguration<DriftApplyJob>
{
    public void Configure(EntityTypeBuilder<DriftApplyJob> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("drift_apply_job");
        builder.HasKey(j => j.Id);

#pragma warning disable CS0618
        builder.UseXminAsConcurrencyToken();
#pragma warning restore CS0618

        builder.Property(j => j.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(j => j.ActorType).HasConversion<string>().HasMaxLength(32).IsRequired();

        builder.Property(j => j.RequestedAtUtc).HasColumnType("timestamp with time zone");
        builder.Property(j => j.ClaimedAtUtc).HasColumnType("timestamp with time zone");
        builder.Property(j => j.LastHeartbeatAtUtc).HasColumnType("timestamp with time zone");
        builder.Property(j => j.FinishedAtUtc).HasColumnType("timestamp with time zone");

        builder.Property(j => j.SubjectKey).IsRequired().HasMaxLength(DriftApplyJob.MaxSubjectKeyLength);
        builder.Property(j => j.RequestedBy).IsRequired().HasMaxLength(DriftApplyJob.MaxActorLength);
        builder.Property(j => j.ClaimedByInstanceId).HasMaxLength(DriftApplyJob.MaxActorLength);
        builder.Property(j => j.AttemptCount).IsRequired();

        builder.Property(j => j.ExpectedBeforeVlan);
        builder.Property(j => j.ExpectedAfterVlan).IsRequired();

        builder.Property(j => j.SwitchDeviceKey).HasMaxLength(DriftApplyJob.MaxSwitchDeviceKeyLength);
        builder.Property(j => j.PortName).HasMaxLength(DriftApplyJob.MaxPortNameLength);
        builder.Property(j => j.DesiredVlanId);

        builder.Property(j => j.DeviceReasonCode).HasMaxLength(DriftApplyJob.MaxErrorCodeLength);
        builder.Property(j => j.DeviceConfirmed);
        builder.Property(j => j.BeforeStateJson).HasColumnType("jsonb").HasMaxLength(DriftApplyJob.MaxStateJsonLength);
        builder.Property(j => j.AfterStateJson).HasColumnType("jsonb").HasMaxLength(DriftApplyJob.MaxStateJsonLength);

        builder.Property(j => j.ErrorCategory).HasMaxLength(DriftApplyJob.MaxErrorCategoryLength);
        builder.Property(j => j.ErrorCode).HasMaxLength(DriftApplyJob.MaxErrorCodeLength);
        builder.Property(j => j.ErrorMessage).HasMaxLength(DriftApplyJob.MaxErrorMessageLength);
        builder.Property(j => j.ErrorDetailsJson).HasColumnType("jsonb").HasMaxLength(DriftApplyJob.MaxErrorDetailsJsonLength);

        // A job belongs to a stable rack; the rack must not be deletable while jobs exist.
        builder.HasOne<Rack>()
            .WithMany()
            .HasForeignKey(j => j.RackId)
            .OnDelete(DeleteBehavior.Restrict);

        // Job → steps: cascade delete, mapped through the read-only collection's backing field.
        builder.HasMany(j => j.Steps)
            .WithOne()
            .HasForeignKey(s => s.JobId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Metadata.FindNavigation(nameof(DriftApplyJob.Steps))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);

        // AC5: at most one non-terminal job per (rack, drift item) — the DB-enforced single-active
        // invariant backing the idempotent-create (201/202) behaviour.
        builder.HasIndex(j => new { j.RackId, j.DriftItemId })
            .IsUnique()
            .HasFilter("status IN ('Pending','Claimed','Revalidating','Executing')")
            .HasDatabaseName("ux_drift_apply_job_drift_item_active");

        // Rack job-listing endpoint: newest-first.
        builder.HasIndex(j => new { j.RackId, j.RequestedAtUtc })
            .IsDescending(false, true)
            .HasDatabaseName("ix_drift_apply_job_rack_id_requested_at");
    }
}
