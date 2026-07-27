using Caisson.Domain.Topology;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Caisson.Infrastructure.Persistence.Configurations;

/// <summary>Maps the stable <see cref="Rack"/> registry entity.</summary>
public sealed class RackConfiguration : IEntityTypeConfiguration<Rack>
{
    public void Configure(EntityTypeBuilder<Rack> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("rack");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.ExternalKey).IsRequired().HasMaxLength(256);
        builder.Property(r => r.Name).IsRequired().HasMaxLength(256);
        builder.Property(r => r.CreatedAtUtc).HasColumnType("timestamp with time zone");

        // Stable natural identity is globally unique for a rack registry entry.
        builder.HasIndex(r => r.ExternalKey).IsUnique();
    }
}
