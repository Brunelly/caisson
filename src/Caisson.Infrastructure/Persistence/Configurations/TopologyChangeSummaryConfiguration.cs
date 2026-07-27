using Caisson.Domain.Topology;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Caisson.Infrastructure.Persistence.Configurations;

/// <summary>Maps the optional one-to-one <see cref="TopologyChangeSummary"/> for a snapshot.</summary>
public sealed class TopologyChangeSummaryConfiguration
    : IEntityTypeConfiguration<TopologyChangeSummary>
{
    public void Configure(EntityTypeBuilder<TopologyChangeSummary> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("topology_change_summary");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.ChangeCountsJson)
            .HasColumnType("jsonb")
            .HasMaxLength(TopologyChangeSummary.MaxChangeCountsJsonLength)
            .IsRequired();

        // Ownership: one-to-one with the snapshot (cascade); snapshot_id is unique.
        builder.HasOne<TopologySnapshot>()
            .WithOne(t => t.ChangeSummary)
            .HasForeignKey<TopologyChangeSummary>(c => c.SnapshotId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(c => c.SnapshotId).IsUnique();

        // The previous snapshot is referenced for diffing (no cascade).
        builder.HasOne<TopologySnapshot>()
            .WithMany()
            .HasForeignKey(c => c.PreviousSnapshotId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne<Rack>()
            .WithMany()
            .HasForeignKey(c => c.RackId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
