using Caisson.Domain.DesiredState;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Caisson.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="DesiredStateVersion"/> (story #62, ADR 0025): append-only — <c>GuardAppendOnly</c>
/// rejects update/delete, backed by a database trigger (see the
/// <c>DesiredStateIngestion</c> migration) for tamper-evidence against raw SQL too (NFR7). "The active
/// version for a rack" is always derived by the covering
/// <c>(rack_slug, created_at DESC, id DESC)</c> index below via
/// <c>LatestDesiredStateVersionQueries</c> — never a raw <c>WHERE is_active</c> read.
/// </summary>
public sealed class DesiredStateVersionConfiguration : IEntityTypeConfiguration<DesiredStateVersion>
{
    public void Configure(EntityTypeBuilder<DesiredStateVersion> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("desired_state_version");
        builder.HasKey(v => v.Id);

        builder.Property(v => v.RackSlug).IsRequired().HasMaxLength(DesiredStateSchema.MaxRackSlugLength);
        builder.Property(v => v.CommitSha).IsRequired().HasMaxLength(64);
        builder.Property(v => v.CreatedAtUtc).HasColumnType("timestamp with time zone");
        builder.Property(v => v.IsActive).IsRequired();
        builder.Property(v => v.ContentHash).IsRequired().HasMaxLength(64);

        // Story #63: the full materialised payload (never selected by the metadata-only history query,
        // NFR3) plus revision provenance. Author fields stay nullable end-to-end (AC1: git may omit them).
        builder.Property(v => v.DesiredStateJson)
            .IsRequired()
            .HasColumnType("jsonb")
            .HasMaxLength(DesiredStateSchema.MaxDesiredStateJsonLength);
        builder.Property(v => v.SchemaVersion).IsRequired();
        builder.Property(v => v.IngestedBy).IsRequired().HasMaxLength(DesiredStateSchema.MaxIngestedByLength);
        builder.Property(v => v.AuthorName).HasMaxLength(DesiredStateSchema.MaxAuthorNameLength);
        builder.Property(v => v.AuthorEmail).HasMaxLength(DesiredStateSchema.MaxAuthorEmailLength);
        builder.Property(v => v.AuthorWhenUtc).HasColumnType("timestamp with time zone");

        // Restrict, not cascade: a version references the run that produced it, but the run's own
        // lifecycle (mutable, non-append-only) must never ripple into deleting historical versions.
        builder.HasOne<DesiredStateIngestionRun>()
            .WithMany()
            .HasForeignKey(v => v.IngestionRunId)
            .OnDelete(DeleteBehavior.Restrict);

        // Covering index for "newest version per rack": ORDER BY created_at DESC, id DESC (ADR 0002's
        // tie-break), scoped to one rack slug.
        builder.HasIndex(v => new { v.RackSlug, v.CreatedAtUtc, v.Id })
            .IsDescending(false, true, true)
            .HasDatabaseName("ix_desired_state_version_rack_slug_created_at_id");

        // Story #63: serves the by-commit lookup and states the per-rack SHA-idempotency invariant
        // (one ingested version per rack per commit) at the DB level. Not unique: a rack file that is
        // unchanged since its last ingested commit is intentionally skipped (no new row), so this index
        // is a lookup aid, not a uniqueness guard.
        builder.HasIndex(v => new { v.RackSlug, v.CommitSha })
            .HasDatabaseName("ix_desired_state_version_rack_slug_commit_sha");
    }
}
