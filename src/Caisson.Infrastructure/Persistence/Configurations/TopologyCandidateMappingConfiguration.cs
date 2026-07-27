using Caisson.Domain.Topology;
using Caisson.Infrastructure.Persistence.Conversions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Caisson.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="TopologyCandidateMapping"/>: inferred NIC-to-port correlations with a reason code
/// and a bounded confidence score. The <c>[0.0, 1.0]</c> bound is enforced by a DB CHECK constraint in
/// addition to the value object (ADR 0004). Evidence is stored as bounded <c>jsonb</c>.
/// </summary>
public sealed class TopologyCandidateMappingConfiguration
    : IEntityTypeConfiguration<TopologyCandidateMapping>
{
    public void Configure(EntityTypeBuilder<TopologyCandidateMapping> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("topology_candidate_mapping", t =>
            t.HasCheckConstraint(
                "ck_topology_candidate_mapping_confidence",
                "confidence >= 0.0 AND confidence <= 1.0"));

        builder.HasKey(m => m.Id);

        builder.Property(m => m.Confidence)
            .HasColumnName("confidence")
            .HasConversion<ConfidenceScoreConverter>()
            .HasColumnType("double precision")
            .IsRequired();
        builder.Property(m => m.ReasonCode).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(m => m.EvidenceJson)
            .HasColumnType("jsonb")
            .HasMaxLength(TopologyCandidateMapping.MaxEvidenceJsonLength);

        // Ownership: the snapshot owns its candidate mappings (cascade).
        builder.HasOne<TopologySnapshot>()
            .WithMany(t => t.CandidateMappings)
            .HasForeignKey(m => m.SnapshotId)
            .OnDelete(DeleteBehavior.Cascade);

        // References within the same snapshot (no cascade; the snapshot is the owner).
        builder.HasOne<Nic>()
            .WithMany()
            .HasForeignKey(m => m.NicId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<SwitchPort>()
            .WithMany()
            .HasForeignKey(m => m.SwitchPortId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<Rack>()
            .WithMany()
            .HasForeignKey(m => m.RackId)
            .OnDelete(DeleteBehavior.NoAction);

        // AC4 indexes: join lookup, plus confidence-descending for "best candidate first".
        builder.HasIndex(m => new { m.SnapshotId, m.NicId, m.SwitchPortId });
        builder.HasIndex(m => new { m.SnapshotId, m.Confidence })
            .IsDescending(false, true)
            .HasDatabaseName("ix_topology_candidate_mapping_snapshot_id_confidence");
    }
}
