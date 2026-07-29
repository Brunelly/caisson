using Caisson.Domain.Drift.Apply;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Caisson.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="DriftApplyJobStep"/> (story #65): one mutable per-step row, mirroring
/// <see cref="DiscoveryJobStepConfiguration"/> exactly. Enums are bounded strings, timestamps
/// <c>timestamptz</c>, and the bounded <c>jsonb</c> result summary carries counts/diagnostics only (never
/// secrets). The (job_id, step_name) unique index keeps the step set canonical.
/// </summary>
public sealed class DriftApplyJobStepConfiguration : IEntityTypeConfiguration<DriftApplyJobStep>
{
    public void Configure(EntityTypeBuilder<DriftApplyJobStep> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("drift_apply_job_step");
        builder.HasKey(s => s.Id);

#pragma warning disable CS0618
        builder.UseXminAsConcurrencyToken();
#pragma warning restore CS0618

        builder.Property(s => s.StepName).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(s => s.AttemptCount).IsRequired();

        builder.Property(s => s.StartedAtUtc).HasColumnType("timestamp with time zone");
        builder.Property(s => s.FinishedAtUtc).HasColumnType("timestamp with time zone");
        builder.Property(s => s.DurationMs);

        builder.Property(s => s.ErrorCode).HasMaxLength(128);
        builder.Property(s => s.ErrorMessage).HasMaxLength(2048);
        builder.Property(s => s.ResultSummaryJson)
            .HasColumnType("jsonb")
            .HasMaxLength(DriftApplyJobStep.MaxResultSummaryJsonLength);

        builder.HasIndex(s => new { s.JobId, s.StepName })
            .IsUnique()
            .HasDatabaseName("ux_drift_apply_job_step_job_id_step_name");
    }
}
