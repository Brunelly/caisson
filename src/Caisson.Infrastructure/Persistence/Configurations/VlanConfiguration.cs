using Caisson.Domain.Topology;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Caisson.Infrastructure.Persistence.Configurations;

/// <summary>Maps the observed <see cref="Vlan"/> entity.</summary>
public sealed class VlanConfiguration : IEntityTypeConfiguration<Vlan>
{
    public void Configure(EntityTypeBuilder<Vlan> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("vlan");
        builder.HasKey(v => v.Id);

        builder.Property(v => v.Name).HasMaxLength(128);

        // Ownership: the snapshot owns its VLANs (cascade).
        builder.HasOne<TopologySnapshot>()
            .WithMany(t => t.Vlans)
            .HasForeignKey(v => v.SnapshotId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Rack>()
            .WithMany()
            .HasForeignKey(v => v.RackId)
            .OnDelete(DeleteBehavior.NoAction);

        // AC4 index.
        builder.HasIndex(v => new { v.SnapshotId, v.VlanId });
    }
}
