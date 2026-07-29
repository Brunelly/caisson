using Caisson.Domain.DesiredState;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Caisson.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="DesiredStateValidationError"/> (story #62, AC2): append-only, owned by a
/// <see cref="DesiredStateIngestionRun"/>. Restrict, not cascade — the run itself is a mutable registry
/// row that is never deleted by application logic, but its errors must not silently disappear if it
/// ever were.
/// </summary>
public sealed class DesiredStateValidationErrorConfiguration : IEntityTypeConfiguration<DesiredStateValidationError>
{
    public void Configure(EntityTypeBuilder<DesiredStateValidationError> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("desired_state_validation_error");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.RackSlug).IsRequired().HasMaxLength(DesiredStateSchema.MaxRackSlugLength);
        builder.Property(e => e.FilePath).IsRequired().HasMaxLength(DesiredStateValidationError.MaxFilePathLength);
        builder.Property(e => e.Location).IsRequired().HasMaxLength(DesiredStateValidationError.MaxLocationLength);
        builder.Property(e => e.Message).IsRequired().HasMaxLength(DesiredStateValidationError.MaxMessageLength);
        builder.Property(e => e.Severity).HasConversion<string>().HasMaxLength(16).IsRequired();

        builder.HasOne<DesiredStateIngestionRun>()
            .WithMany()
            .HasForeignKey(e => e.IngestionRunId)
            .OnDelete(DeleteBehavior.Restrict);

        // AC2/step-5: validation errors are listed/paged by run.
        builder.HasIndex(e => e.IngestionRunId).HasDatabaseName("ix_desired_state_validation_error_ingestion_run_id");
    }
}
