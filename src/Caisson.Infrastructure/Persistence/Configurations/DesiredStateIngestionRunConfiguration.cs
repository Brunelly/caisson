using Caisson.Domain.DesiredState;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Caisson.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="DesiredStateIngestionRun"/> (story #62): a mutable, registry-style row (not
/// append-only, so <c>GuardAppendOnly</c> ignores it, mirroring <c>DiscoveryJobConfiguration</c>). Two
/// partial-unique indexes back NFR2/NFR3: at most one live/processed run per commit SHA, and replay
/// protection on the webhook delivery id.
/// </summary>
public sealed class DesiredStateIngestionRunConfiguration : IEntityTypeConfiguration<DesiredStateIngestionRun>
{
    /// <summary>Statuses that mean the commit is either being processed or was already fully processed —
    /// a currently-running or already-processed commit is never reprocessed. <see cref="IngestionRunStatus.Failed"/>
    /// (an infrastructure fault) is deliberately excluded so the next poll tick can retry it.</summary>
    internal const string LiveOrProcessedStatusFilter =
        "commit_sha IS NOT NULL AND status IN ('Running','Succeeded','PartiallySucceeded','ValidationFailed')";

    public void Configure(EntityTypeBuilder<DesiredStateIngestionRun> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("desired_state_ingestion_run");
        builder.HasKey(r => r.Id);

        // Optimistic concurrency, same rationale/obsolete-API caveat as DiscoveryJobConfiguration.
#pragma warning disable CS0618
        builder.UseXminAsConcurrencyToken();
#pragma warning restore CS0618

        builder.Property(r => r.TriggerType).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(r => r.ErrorCategory).HasConversion<string>().HasMaxLength(32);

        builder.Property(r => r.StartedAtUtc).HasColumnType("timestamp with time zone");
        builder.Property(r => r.CompletedAtUtc).HasColumnType("timestamp with time zone");
        builder.Property(r => r.CommitTimeUtc).HasColumnType("timestamp with time zone");

        builder.Property(r => r.RepoUrl).IsRequired().HasMaxLength(2048);
        builder.Property(r => r.Branch).IsRequired().HasMaxLength(256);
        builder.Property(r => r.CommitSha).HasMaxLength(64);
        builder.Property(r => r.CommitAuthor).HasMaxLength(256);
        builder.Property(r => r.CommitMessage).HasMaxLength(DesiredStateIngestionRun.MaxCommitMessageLength);
        builder.Property(r => r.WebhookDeliveryId).HasMaxLength(256);
        builder.Property(r => r.ErrorSummary).HasMaxLength(DesiredStateIngestionRun.MaxErrorSummaryLength);

        // NFR3: at most one live-or-processed run per observed commit SHA.
        builder.HasIndex(r => r.CommitSha)
            .IsUnique()
            .HasFilter(LiveOrProcessedStatusFilter)
            .HasDatabaseName("ux_desired_state_ingestion_run_commit_sha");

        // NFR2: replay protection — a webhook delivery id is processed at most once.
        builder.HasIndex(r => r.WebhookDeliveryId)
            .IsUnique()
            .HasFilter("webhook_delivery_id IS NOT NULL")
            .HasDatabaseName("ux_desired_state_ingestion_run_webhook_delivery_id");

        // AC4: the ingestion-runs listing endpoint reads newest-first.
        builder.HasIndex(r => r.StartedAtUtc)
            .IsDescending(true)
            .HasDatabaseName("ix_desired_state_ingestion_run_started_at");
    }
}
