using Caisson.Domain.Topology;
using Caisson.Infrastructure.Persistence.Conversions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Caisson.Infrastructure.Persistence.Configurations;

/// <summary>Maps the observed <see cref="Nic"/> entity.</summary>
public sealed class NicConfiguration : IEntityTypeConfiguration<Nic>
{
    public void Configure(EntityTypeBuilder<Nic> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("nic");
        builder.HasKey(n => n.Id);

        builder.Property(n => n.Name).IsRequired().HasMaxLength(128);
        builder.Property(n => n.MacPrimary)
            .HasConversion<MacAddressValueConverter>()
            .HasMaxLength(12)
            .IsRequired();
        builder.Property(n => n.LinkState).HasConversion<string>().HasMaxLength(16);

        // Ownership: the server owns its NICs (cascade) — the single cascade path.
        builder.HasOne<Server>()
            .WithMany(s => s.Nics)
            .HasForeignKey(n => n.ServerId)
            .OnDelete(DeleteBehavior.Cascade);

        // Redundant denormalized snapshot/rack references (no cascade).
        builder.HasOne<TopologySnapshot>()
            .WithMany()
            .HasForeignKey(n => n.SnapshotId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<Rack>()
            .WithMany()
            .HasForeignKey(n => n.RackId)
            .OnDelete(DeleteBehavior.NoAction);

        // AC4 indexes.
        builder.HasIndex(n => new { n.SnapshotId, n.ServerId });
        builder.HasIndex(n => n.MacPrimary);
    }
}
