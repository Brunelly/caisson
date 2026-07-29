using Caisson.Domain.Drift;
using Caisson.Domain.Topology;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Caisson.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="DriftItem"/> (story #64). Uniqueness is scoped to
/// <c>(drift_report_id, drift_item_id)</c> — NOT global on <c>drift_item_id</c> — because
/// <c>Diffing.DeterministicGuid</c> deliberately excludes the desired-revision/observed-snapshot
/// identity, so identical drift can legitimately recur across reports (see the entity's type-level
/// remarks and <c>TopologyEntityDiffConfiguration</c>'s analogous scoped-key precedent).
/// </summary>
public sealed class DriftItemConfiguration : IEntityTypeConfiguration<DriftItem>
{
    public void Configure(EntityTypeBuilder<DriftItem> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("drift_item");
        builder.HasKey(i => i.Id);

        builder.Property(i => i.DriftType).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(i => i.Severity).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(i => i.Actionable).IsRequired();
        builder.Property(i => i.SubjectType).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(i => i.SubjectKey).IsRequired().HasMaxLength(DriftSchema.MaxSubjectKeyLength);
        builder.Property(i => i.ExpectedValue).HasMaxLength(DriftSchema.MaxExpectedValueLength);
        builder.Property(i => i.ActualValue).HasMaxLength(DriftSchema.MaxActualValueLength);
        builder.Property(i => i.Why).IsRequired().HasMaxLength(DriftSchema.MaxWhyLength);
        builder.Property(i => i.DetailsJson).HasColumnType("jsonb").HasMaxLength(DriftSchema.MaxDetailsJsonLength);
        builder.Property(i => i.CreatedAtUtc).HasColumnType("timestamp with time zone").IsRequired();

        // Report owns its items outright: deleting a report (retention pruning) cascades its items.
        builder.HasOne<DriftReport>()
            .WithMany()
            .HasForeignKey(i => i.DriftReportId)
            .OnDelete(DeleteBehavior.Cascade);

        // The stable rack; never deletable while drift items reference it.
        builder.HasOne<Rack>()
            .WithMany()
            .HasForeignKey(i => i.RackId)
            .OnDelete(DeleteBehavior.Restrict);

        // Upsert-by-id key (see type remarks): scoped, not global.
        builder.HasIndex(i => new { i.DriftReportId, i.DriftItemId })
            .IsUnique()
            .HasDatabaseName("ux_drift_item_report_id_drift_item_id");

        // GET items/{driftItemId}: rack-scoped lookup across a rack's reports, resolving the latest one.
        builder.HasIndex(i => new { i.RackId, i.DriftItemId })
            .HasDatabaseName("ix_drift_item_rack_id_drift_item_id");

        // Item-page filters (severity/driftType/actionable) plus the keyset ordering, scoped to one report.
        builder.HasIndex(i => new { i.DriftReportId, i.CreatedAtUtc })
            .HasDatabaseName("ix_drift_item_report_id_created_at");
    }
}
