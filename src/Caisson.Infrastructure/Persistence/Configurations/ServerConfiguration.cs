using Caisson.Domain.Topology;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Caisson.Infrastructure.Persistence.Configurations;

/// <summary>Maps the observed <see cref="Server"/> entity.</summary>
public sealed class ServerConfiguration : IEntityTypeConfiguration<Server>
{
    public void Configure(EntityTypeBuilder<Server> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("server");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.BmcType).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(s => s.ExternalDeviceKey).IsRequired().HasMaxLength(256);
        builder.Property(s => s.BmcAddress).IsRequired().HasMaxLength(128);
        builder.Property(s => s.BmcUuid).HasMaxLength(128);
        builder.Property(s => s.Hostname).HasMaxLength(256);

        // Ownership: the snapshot owns its servers (cascade).
        builder.HasOne<TopologySnapshot>()
            .WithMany(t => t.Servers)
            .HasForeignKey(s => s.SnapshotId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Rack>()
            .WithMany()
            .HasForeignKey(s => s.RackId)
            .OnDelete(DeleteBehavior.NoAction);

        // AC4 indexes.
        builder.HasIndex(s => new { s.SnapshotId, s.RackId });
        builder.HasIndex(s => s.BmcUuid);
    }
}
