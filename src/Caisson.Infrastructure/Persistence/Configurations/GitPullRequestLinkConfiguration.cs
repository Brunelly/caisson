using Caisson.Domain.Git;
using Caisson.Domain.Topology;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Caisson.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF configuration for the desired-state PR idempotency + audit link (story #172, Task #206). Mirrors
/// <see cref="DesiredStateCandidateDiffCacheConfiguration"/>/<see cref="RackNetworkIntentConfiguration"/>:
/// a mutable (non-append-only) row with xmin optimistic concurrency, enum-as-string, bounded lengths, and a
/// rack FK with <see cref="DeleteBehavior.Restrict"/>. The critical invariant is the <b>filtered partial-
/// unique index</b> on <c>(rack_id, candidate_fingerprint) WHERE status = 'Open'</c> (mirroring the drift-
/// apply active-job index): it DB-enforces one Open PR link per (rack, candidate) so concurrent identical
/// requests collapse onto a single PR (NFR3), while a Closed/Merged link for the same fingerprint does NOT
/// block a fresh Open one (story Q2: always a new branch+PR after the prior closes). See ADR 0057.
/// </summary>
public sealed class GitPullRequestLinkConfiguration : IEntityTypeConfiguration<GitPullRequestLink>
{
    /// <summary>The named partial-unique constraint the insert-or-get path keys its conflict handling off.</summary>
    public const string OpenLinkUniqueConstraint = "ux_git_pull_request_link_rack_fingerprint_open";

    public void Configure(EntityTypeBuilder<GitPullRequestLink> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("git_pull_request_link");
        builder.HasKey(x => x.Id);

        // Optimistic concurrency via Postgres xmin, matching the sibling mutable entities.
#pragma warning disable CS0618
        builder.UseXminAsConcurrencyToken();
#pragma warning restore CS0618

        builder.Property(x => x.RepoOwner).IsRequired().HasMaxLength(GitPullRequestLink.MaxRepoSegmentLength);
        builder.Property(x => x.RepoName).IsRequired().HasMaxLength(GitPullRequestLink.MaxRepoSegmentLength);
        builder.Property(x => x.BranchName).IsRequired().HasMaxLength(GitPullRequestLink.MaxBranchNameLength);

        builder.Property(x => x.PullRequestNumber);
        builder.Property(x => x.PullRequestUrl).HasMaxLength(GitPullRequestLink.MaxUrlLength);
        builder.Property(x => x.CommitSha).HasMaxLength(GitPullRequestLink.MaxCommitShaLength);

        builder.Property(x => x.CandidateFingerprint)
            .IsRequired()
            .HasMaxLength(GitPullRequestLink.FingerprintHexLength)
            .IsFixedLength();

        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(16).IsRequired();

        builder.Property(x => x.CreatedBy).IsRequired().HasMaxLength(GitPullRequestLink.MaxActorLength);
        builder.Property(x => x.CreatedAtUtc).HasColumnType("timestamp with time zone");
        builder.Property(x => x.LastCheckedAtUtc).HasColumnType("timestamp with time zone");
        builder.Property(x => x.CorrelationId).IsRequired().HasMaxLength(GitPullRequestLink.MaxCorrelationIdLength);

        // A rack must not be deletable while a PR link references it.
        builder.HasOne<Rack>()
            .WithMany()
            .HasForeignKey(x => x.RackId)
            .OnDelete(DeleteBehavior.Restrict);

        // One Open link per (rack, candidate fingerprint): the idempotency/concurrency invariant (NFR3).
        // Filtered to Open so a Closed/Merged link never blocks a fresh PR for the same candidate (Q2).
        builder.HasIndex(x => new { x.RackId, x.CandidateFingerprint })
            .IsUnique()
            .HasFilter("status = 'Open'")
            .HasDatabaseName(OpenLinkUniqueConstraint);

        // Rack-scoped listing / reconciliation: newest-first.
        builder.HasIndex(x => new { x.RackId, x.CreatedAtUtc })
            .HasDatabaseName("ix_git_pull_request_link_rack_created");
    }
}
