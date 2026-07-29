using Caisson.Domain.Discovery;
using Caisson.Domain.Topology;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Caisson.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="DiscoveryJob"/> (story #8): a mutable, registry-style job row (not append-only, so
/// <c>GuardAppendOnly</c> ignores it). Enums are bounded strings; timestamps are <c>timestamptz</c>. Two
/// partial-unique indexes back the invariants — one active job per rack (NFR5) and idempotent replay
/// (AC2) — mirroring the story-7 <c>ux_topology_snapshot_rack_id_version</c> race backstop.
/// </summary>
public sealed class DiscoveryJobConfiguration : IEntityTypeConfiguration<DiscoveryJob>
{
    public void Configure(EntityTypeBuilder<DiscoveryJob> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("discovery_job");
        builder.HasKey(j => j.Id);

        // Optimistic concurrency (finding #12): the Npgsql xmin system column as a concurrency token — no
        // new schema column, no new dependency — so a superseded execution's SaveChangesAsync throws
        // DbUpdateConcurrencyException instead of silently clobbering a write from a different claim (e.g.
        // two runner instances that both believe they hold the same job after a reclaim race).
        // UseXminAsConcurrencyToken() is marked obsolete in favour of a bare `Property<uint>("xmin")
        // .IsRowVersion()` shadow property, but that alone makes the migration generator treat xmin as a
        // brand-new column and emit an "ADD COLUMN xmin" — which Postgres rejects outright, xmin being a
        // reserved system column name. UseXminAsConcurrencyToken() correctly excludes it from migrations.
#pragma warning disable CS0618
        builder.UseXminAsConcurrencyToken();
#pragma warning restore CS0618

        builder.Property(j => j.Mode).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(j => j.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(j => j.ActorType).HasConversion<string>().HasMaxLength(32).IsRequired();

        builder.Property(j => j.CreatedAtUtc).HasColumnType("timestamp with time zone");
        builder.Property(j => j.StartedAtUtc).HasColumnType("timestamp with time zone");
        builder.Property(j => j.FinishedAtUtc).HasColumnType("timestamp with time zone");
        builder.Property(j => j.LastHeartbeatAtUtc).HasColumnType("timestamp with time zone");

        builder.Property(j => j.TriggeredBy).IsRequired().HasMaxLength(256);
        builder.Property(j => j.IdempotencyKey).HasMaxLength(200);
        builder.Property(j => j.DryRun).IsRequired();
        builder.Property(j => j.AttemptCount).IsRequired();
        builder.Property(j => j.CancellationRequested).IsRequired();
        builder.Property(j => j.ErrorCode).HasMaxLength(128);
        builder.Property(j => j.ErrorMessage).HasMaxLength(DiscoveryJob.MaxErrorMessageLength);

        // A job belongs to a stable rack; the rack must not be deletable while jobs exist.
        builder.HasOne<Rack>()
            .WithMany()
            .HasForeignKey(j => j.RackId)
            .OnDelete(DeleteBehavior.Restrict);

        // The snapshot the run produced (nullable until persistence succeeds); non-cascading.
        builder.HasOne<TopologySnapshot>()
            .WithMany()
            .HasForeignKey(j => j.ResultSnapshotId)
            .OnDelete(DeleteBehavior.NoAction);

        // Job → steps: cascade delete, mapped through the read-only collection's backing field.
        builder.HasMany(j => j.Steps)
            .WithOne()
            .HasForeignKey(s => s.JobId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Metadata.FindNavigation(nameof(DiscoveryJob.Steps))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);

        // NFR5: at most one Queued/InProgress job per rack — the DB-enforced single-active invariant.
        builder.HasIndex(j => j.RackId)
            .IsUnique()
            .HasFilter("status IN ('Queued','InProgress')")
            .HasDatabaseName("ux_discovery_job_rack_active");

        // AC2: idempotent replay — one job per (rack, idempotency_key) when a key is supplied.
        builder.HasIndex(j => new { j.RackId, j.IdempotencyKey })
            .IsUnique()
            .HasFilter("idempotency_key IS NOT NULL")
            .HasDatabaseName("ux_discovery_job_rack_idempotency_key");

        // AC4: the rack job-history endpoint lists newest-first.
        builder.HasIndex(j => new { j.RackId, j.CreatedAtUtc })
            .IsDescending(false, true)
            .HasDatabaseName("ix_discovery_job_rack_id_created_at");
    }
}
