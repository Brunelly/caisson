using Caisson.Domain.DesiredState;
using Caisson.Domain.Drift;
using Caisson.Domain.Topology;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Caisson.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="DriftReport"/> (story #64): a mutable, upsertable registry row (not append-only, so
/// <c>GuardAppendOnly</c> ignores it — mirrors <c>DiscoveryJobConfiguration</c>). A UNIQUE
/// <c>(rack_id, desired_revision_id, observed_snapshot_id)</c> index is both the idempotency key AC3
/// requires and the constraint <c>DriftComputationService</c>'s insert-then-retry-as-update race
/// handling detects by name.
/// </summary>
public sealed class DriftReportConfiguration : IEntityTypeConfiguration<DriftReport>
{
    /// <summary>The unique-violation constraint name <c>DriftComputationService</c> matches on for its insert race.</summary>
    public const string RackDesiredObservedUniqueConstraint = "ux_drift_report_rack_desired_observed";

    public void Configure(EntityTypeBuilder<DriftReport> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("drift_report");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(r => r.ComputedAtUtc).HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(r => r.ComputationVersion).IsRequired();
        builder.Property(r => r.TotalItems).IsRequired();
        builder.Property(r => r.CountsBySeverityJson)
            .HasColumnType("jsonb")
            .HasMaxLength(DriftSchema.MaxCountsBySeverityJsonLength)
            .IsRequired();
        builder.Property(r => r.HasAmbiguities).IsRequired();
        builder.Property(r => r.IsTruncated).IsRequired();
        builder.Property(r => r.ErrorSummary).HasMaxLength(DriftSchema.MaxErrorSummaryLength);

        // The stable rack; never deletable while drift reports reference it (mirrors DiscoveryJobConfiguration).
        builder.HasOne<Rack>()
            .WithMany()
            .HasForeignKey(r => r.RackId)
            .OnDelete(DeleteBehavior.Restrict);

        // Append-only upstream rows (never deleted in practice); Restrict states that invariant
        // explicitly rather than leaving it emergent (mirrors TopologyEntityDiffConfiguration's SnapshotId FK).
        builder.HasOne<DesiredStateVersion>()
            .WithMany()
            .HasForeignKey(r => r.DesiredRevisionId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<TopologySnapshot>()
            .WithMany()
            .HasForeignKey(r => r.ObservedSnapshotId)
            .OnDelete(DeleteBehavior.Restrict);

        // AC3: the idempotency/upsert key.
        builder.HasIndex(r => new { r.RackId, r.DesiredRevisionId, r.ObservedSnapshotId })
            .IsUnique()
            .HasDatabaseName(RackDesiredObservedUniqueConstraint);

        // History/latest-report queries: newest-first per rack.
        builder.HasIndex(r => new { r.RackId, r.ComputedAtUtc })
            .IsDescending(false, true)
            .HasDatabaseName("ix_drift_report_rack_id_computed_at");
    }
}
