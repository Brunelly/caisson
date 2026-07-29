using Caisson.Domain.DesiredState;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Caisson.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="DesiredSwitchIntent"/> (story #62): append-only, owned by a <see cref="DesiredRackIntent"/>.</summary>
public sealed class DesiredSwitchIntentConfiguration : IEntityTypeConfiguration<DesiredSwitchIntent>
{
    public void Configure(EntityTypeBuilder<DesiredSwitchIntent> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("desired_switch_intent");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.SwitchName).IsRequired().HasMaxLength(DesiredStateSchema.MaxSwitchNameLength);
        builder.Property(s => s.StableKey).IsRequired().HasMaxLength(512);

        builder.HasOne<DesiredRackIntent>()
            .WithMany()
            .HasForeignKey(s => s.DesiredRackIntentId)
            .OnDelete(DeleteBehavior.Cascade);

        // Duplicate switch names within a rack are already rejected at validation time; this is a
        // defence-in-depth backstop against direct writes bypassing the validator.
        builder.HasIndex(s => new { s.DesiredRackIntentId, s.SwitchName }).IsUnique();
    }
}
