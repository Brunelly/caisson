using Caisson.Domain.Git;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Caisson.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF configuration for the PR status projection (story #173, Task #210). Mirrors
/// <see cref="GitPullRequestLinkConfiguration"/>: a mutable (non-append-only) row with xmin optimistic
/// concurrency, enum-as-string, bounded lengths, and a FK with <see cref="DeleteBehavior.Restrict"/>. The
/// 1:1 invariant is DB-enforced by a unique index on <c>pull_request_link_id</c>; a lease index on
/// <c>(next_poll_after_at, last_checked_at)</c> backs the poller's due-candidate selection. The per-check
/// rollup is stored as <c>jsonb</c>. See ADR 0061.
/// </summary>
public sealed class GitPullRequestStatusRecordConfiguration : IEntityTypeConfiguration<GitPullRequestStatusRecord>
{
    /// <summary>The named unique constraint enforcing the 1:1 link-to-status relationship.</summary>
    public const string LinkUniqueConstraint = "ux_git_pull_request_status_link";

    public void Configure(EntityTypeBuilder<GitPullRequestStatusRecord> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("git_pull_request_status");
        builder.HasKey(x => x.Id);

        // Optimistic concurrency via Postgres xmin, matching the sibling mutable entities.
#pragma warning disable CS0618
        builder.UseXminAsConcurrencyToken();
#pragma warning restore CS0618

        builder.Property(x => x.RepoOwner).IsRequired().HasMaxLength(GitPullRequestStatusRecord.MaxRepoSegmentLength);
        builder.Property(x => x.RepoName).IsRequired().HasMaxLength(GitPullRequestStatusRecord.MaxRepoSegmentLength);
        builder.Property(x => x.PullRequestNumber).IsRequired();
        builder.Property(x => x.PullRequestUrl).IsRequired().HasMaxLength(GitPullRequestStatusRecord.MaxUrlLength);

        builder.Property(x => x.State).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(x => x.HeadSha).HasMaxLength(GitPullRequestStatusRecord.MaxHeadShaLength);
        builder.Property(x => x.ChecksConclusion).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(x => x.FailingChecksCount);

        builder.Property(x => x.ChecksSummary)
            .HasColumnType("jsonb")
            .HasMaxLength(GitPullRequestStatusRecord.MaxChecksSummaryLength);

        builder.Property(x => x.LastCheckedAtUtc).HasColumnType("timestamp with time zone");
        builder.Property(x => x.NextPollAfterUtc).HasColumnType("timestamp with time zone");
        builder.Property(x => x.ConsecutivePollFailures).IsRequired();
        builder.Property(x => x.LastPollFailureReason).HasMaxLength(GitPullRequestStatusRecord.MaxFailureReasonLength);
        builder.Property(x => x.UpdatedAtUtc).HasColumnType("timestamp with time zone");

        // 1:1 with the link; a link must not be deletable while its status projection references it.
        builder.HasOne<GitPullRequestLink>()
            .WithMany()
            .HasForeignKey(x => x.PullRequestLinkId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.PullRequestLinkId)
            .IsUnique()
            .HasDatabaseName(LinkUniqueConstraint);

        // Rack-scoped read: at most one status record per rack candidate, newest data surfaced.
        builder.HasIndex(x => x.RackId)
            .HasDatabaseName("ix_git_pull_request_status_rack");

        // Poller lease: select due candidates ordered by when they became due.
        builder.HasIndex(x => new { x.NextPollAfterUtc, x.LastCheckedAtUtc })
            .HasDatabaseName("ix_git_pull_request_status_lease");
    }
}
