using Caisson.Domain.DesiredState;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Caisson.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="DesiredPortIntent"/> (story #62, AC2/AC3): append-only, owned by a
/// <see cref="DesiredSwitchIntent"/>. The <c>accessVlan</c> range and <c>description</c> length are
/// double-enforced: the entity constructor already guards them, and this configuration adds the same
/// PostgreSQL <c>CHECK</c> constraint ADR 0004 established for <c>ConfidenceScore</c>, so the invariant
/// holds even against direct SQL writes.
/// </summary>
public sealed class DesiredPortIntentConfiguration : IEntityTypeConfiguration<DesiredPortIntent>
{
    public void Configure(EntityTypeBuilder<DesiredPortIntent> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("desired_port_intent", t => t.HasCheckConstraint(
            "ck_desired_port_intent_access_vlan",
            $"access_vlan >= {DesiredStateSchema.MinVlan} AND access_vlan <= {DesiredStateSchema.MaxVlan}"));

        builder.HasKey(p => p.Id);

        builder.Property(p => p.PortName).IsRequired().HasMaxLength(DesiredStateSchema.MaxPortNameLength);
        builder.Property(p => p.StableKey).IsRequired().HasMaxLength(512);
        builder.Property(p => p.AccessVlan).HasColumnName("access_vlan").IsRequired();
        builder.Property(p => p.Description).HasMaxLength(DesiredStateSchema.MaxDescriptionLength);
        builder.Property(p => p.NeighborSystemName).HasMaxLength(DesiredStateSchema.MaxNeighborFieldLength);
        builder.Property(p => p.NeighborPortId).HasMaxLength(DesiredStateSchema.MaxNeighborFieldLength);

        builder.HasOne<DesiredSwitchIntent>()
            .WithMany()
            .HasForeignKey(p => p.DesiredSwitchIntentId)
            .OnDelete(DeleteBehavior.Cascade);

        // Duplicate port names on a switch are already rejected at validation time; defence in depth.
        builder.HasIndex(p => new { p.DesiredSwitchIntentId, p.PortName }).IsUnique();

        // Later drift/reconciliation stories join desired ports to observed SwitchPort rows by stable key.
        builder.HasIndex(p => p.StableKey).HasDatabaseName("ix_desired_port_intent_stable_key");
    }
}
