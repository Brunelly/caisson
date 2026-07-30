using Caisson.Domain.NetworkConfig;
using Caisson.Domain.Topology;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Caisson.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF configuration for the rack-scoped network-intent draft (story #176). Mirrors
/// <c>DriftApplyJobConfiguration</c>'s xmin-concurrency + jsonb + rack-FK-restrict shape exactly.
/// </summary>
public sealed class RackNetworkIntentConfiguration : IEntityTypeConfiguration<RackNetworkIntent>
{
    public void Configure(EntityTypeBuilder<RackNetworkIntent> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("rack_network_intent");
        builder.HasKey(x => x.Id);

        // The story's optimistic-concurrency "version/etag" (surfaced to the API as an ETag) — never a
        // hand-rolled version int, per the same precedent DriftApplyJob established.
#pragma warning disable CS0618
        builder.UseXminAsConcurrencyToken();
#pragma warning restore CS0618

        builder.Property(x => x.IntentJson)
            .IsRequired()
            .HasColumnType("jsonb")
            .HasMaxLength(RackNetworkIntent.MaxIntentJsonLength);

        builder.Property(x => x.CreatedAtUtc).HasColumnType("timestamp with time zone");
        builder.Property(x => x.CreatedBy).IsRequired().HasMaxLength(RackNetworkIntent.MaxActorLength);
        builder.Property(x => x.UpdatedAtUtc).HasColumnType("timestamp with time zone");
        builder.Property(x => x.UpdatedBy).IsRequired().HasMaxLength(RackNetworkIntent.MaxActorLength);

        // A rack must not be deletable while its authored network intent exists.
        builder.HasOne<Rack>()
            .WithMany()
            .HasForeignKey(x => x.RackId)
            .OnDelete(DeleteBehavior.Restrict);

        // Story Q3: single saved state only — at most one row per rack.
        builder.HasIndex(x => x.RackId)
            .IsUnique()
            .HasDatabaseName("ux_rack_network_intent_rack_id");
    }
}
