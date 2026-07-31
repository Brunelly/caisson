using Caisson.Domain.DesiredState;
using Caisson.Domain.Topology;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Caisson.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF configuration for the impact-preview diff cache (story #171, Task #197). Mirrors
/// <see cref="RackNetworkIntentConfiguration"/>'s xmin-concurrency + jsonb + rack-FK-restrict shape. The
/// unique index on <c>(rack_id, baseline_revision_id, candidate_sha256)</c> is the leak-safe, rack-scoped
/// cache key that both dedupes per-content and prevents cross-rack retrieval (NFR2) and invalidates when a
/// new baseline revision arrives; the non-unique <c>(rack_id, expires_at_utc)</c> index backs the TTL pruner.
/// </summary>
public sealed class DesiredStateCandidateDiffCacheConfiguration : IEntityTypeConfiguration<DesiredStateCandidateDiffCache>
{
    public void Configure(EntityTypeBuilder<DesiredStateCandidateDiffCache> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("desired_state_candidate_diff_cache");
        builder.HasKey(x => x.Id);

        // Optimistic concurrency via Postgres xmin (surfaced as an ETag), matching RackNetworkIntent.
#pragma warning disable CS0618
        builder.UseXminAsConcurrencyToken();
#pragma warning restore CS0618

        builder.Property(x => x.CandidateSha256)
            .IsRequired()
            .HasMaxLength(DesiredStateCandidateDiffCache.Sha256HexLength)
            .IsFixedLength();
        builder.Property(x => x.BaselineSha256)
            .IsRequired()
            .HasMaxLength(DesiredStateCandidateDiffCache.Sha256HexLength)
            .IsFixedLength();

        builder.Property(x => x.RawUnifiedDiff)
            .IsRequired()
            .HasColumnType("text")
            .HasMaxLength(DesiredStateCandidateDiffCache.MaxRawUnifiedDiffLength);

        builder.Property(x => x.StructuredSummaryJson)
            .IsRequired()
            .HasColumnType("jsonb")
            .HasMaxLength(DesiredStateCandidateDiffCache.MaxStructuredSummaryJsonLength);

        builder.Property(x => x.CreatedAtUtc).HasColumnType("timestamp with time zone");
        builder.Property(x => x.ExpiresAtUtc).HasColumnType("timestamp with time zone");
        builder.Property(x => x.CreatedBy).IsRequired().HasMaxLength(DesiredStateCandidateDiffCache.MaxActorLength);

        // A rack must not be deletable while a cached preview references it.
        builder.HasOne<Rack>()
            .WithMany()
            .HasForeignKey(x => x.RackId)
            .OnDelete(DeleteBehavior.Restrict);

        // Leak-safe, rack-scoped, content-addressed cache key (NFR2): dedupes per (rack, baseline, candidate).
        builder.HasIndex(x => new { x.RackId, x.BaselineRevisionId, x.CandidateSha256 })
            .IsUnique()
            .HasDatabaseName("ux_desired_state_candidate_diff_cache_rack_baseline_candidate");

        // Backs the TTL pruner's rack-scoped expiry sweep.
        builder.HasIndex(x => new { x.RackId, x.ExpiresAtUtc })
            .HasDatabaseName("ix_desired_state_candidate_diff_cache_rack_expires");
    }
}
