using Caisson.Domain.Topology;
using Caisson.Infrastructure.Persistence.Conversions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Caisson.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps the observed <see cref="MacAddress"/> entity. Its owner for cleanup is the snapshot (cascade),
/// because <see cref="MacAddress.NicId"/> is optional — a MAC can be observed without being correlated
/// to a NIC. Duplicate MACs within a snapshot are intentionally allowed (non-unique index).
/// </summary>
public sealed class MacAddressConfiguration : IEntityTypeConfiguration<MacAddress>
{
    public void Configure(EntityTypeBuilder<MacAddress> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("mac_address");
        builder.HasKey(m => m.Id);

        builder.Property(m => m.Mac)
            .HasConversion<MacAddressValueConverter>()
            .HasMaxLength(12)
            .IsRequired();
        builder.Property(m => m.Source).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(m => m.LastSeenAtUtc).HasColumnType("timestamp with time zone");

        // Ownership: the snapshot owns MAC rows (cascade) — the single cascade path.
        builder.HasOne<TopologySnapshot>()
            .WithMany()
            .HasForeignKey(m => m.SnapshotId)
            .OnDelete(DeleteBehavior.Cascade);

        // Optional correlation to a NIC (no cascade; the NIC is not the owner for cleanup).
        builder.HasOne<Nic>()
            .WithMany(n => n.MacAddresses)
            .HasForeignKey(m => m.NicId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne<Rack>()
            .WithMany()
            .HasForeignKey(m => m.RackId)
            .OnDelete(DeleteBehavior.NoAction);

        // AC4: NON-UNIQUE — duplicate MACs within a snapshot represent a real observed conflict.
        builder.HasIndex(m => new { m.SnapshotId, m.Mac });
    }
}
