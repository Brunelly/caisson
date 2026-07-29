using Caisson.Domain.DesiredState;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Caisson.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="DesiredRackIntent"/> (story #62): append-only, owned by a <see cref="DesiredStateVersion"/>.</summary>
public sealed class DesiredRackIntentConfiguration : IEntityTypeConfiguration<DesiredRackIntent>
{
    public void Configure(EntityTypeBuilder<DesiredRackIntent> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("desired_rack_intent");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.RackSlug).IsRequired().HasMaxLength(DesiredStateSchema.MaxRackSlugLength);
        builder.Property(r => r.StableKey).IsRequired().HasMaxLength(512);

        // Ownership: the version owns its rack intent (cascade) — both are append-only, but a version
        // may still be deleted wholesale for retention, which must take its tree with it.
        builder.HasOne<DesiredStateVersion>()
            .WithMany()
            .HasForeignKey(r => r.DesiredStateVersionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(r => r.DesiredStateVersionId).IsUnique();
    }
}
