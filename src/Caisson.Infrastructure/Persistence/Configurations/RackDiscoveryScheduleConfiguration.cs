using Caisson.Domain.Discovery;
using Caisson.Domain.Topology;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Caisson.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="RackDiscoverySchedule"/> (story #8, AC3): one mutable row per rack (1:1 with
/// <c>Rack</c>). Fixed-interval-plus-jitter only — no cron column (ADR 0013). Timestamps are
/// <c>timestamptz</c>.
/// </summary>
public sealed class RackDiscoveryScheduleConfiguration : IEntityTypeConfiguration<RackDiscoverySchedule>
{
    public void Configure(EntityTypeBuilder<RackDiscoverySchedule> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("rack_discovery_schedule");
        builder.HasKey(s => s.RackId);

        builder.Property(s => s.RackId).ValueGeneratedNever();
        builder.Property(s => s.Enabled).IsRequired();
        builder.Property(s => s.IntervalSeconds).IsRequired();
        builder.Property(s => s.JitterSeconds).IsRequired();
        builder.Property(s => s.NextRunAtUtc).HasColumnType("timestamp with time zone");
        builder.Property(s => s.LastAttemptAtUtc).HasColumnType("timestamp with time zone");
        builder.Property(s => s.LastSuccessAtUtc).HasColumnType("timestamp with time zone");

        // 1:1 with the stable rack; removing the rack removes its schedule.
        builder.HasOne<Rack>()
            .WithOne()
            .HasForeignKey<RackDiscoverySchedule>(s => s.RackId)
            .OnDelete(DeleteBehavior.Cascade);

        // AC3: the scheduler scans enabled schedules that are due.
        builder.HasIndex(s => new { s.Enabled, s.NextRunAtUtc })
            .HasDatabaseName("ix_rack_discovery_schedule_enabled_next_run");
    }
}
