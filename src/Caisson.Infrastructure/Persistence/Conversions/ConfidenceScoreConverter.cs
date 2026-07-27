using Caisson.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Caisson.Infrastructure.Persistence.Conversions;

/// <summary>
/// Converts <see cref="ConfidenceScore"/> to/from <see cref="double"/> for storage. The
/// <c>[0.0, 1.0]</c> bound is validated again on read by <see cref="ConfidenceScore.From"/> and, in
/// the database, by a CHECK constraint (see ADR 0004).
/// </summary>
public sealed class ConfidenceScoreConverter : ValueConverter<ConfidenceScore, double>
{
    public ConfidenceScoreConverter()
        : base(
            score => score.Value,
            stored => ConfidenceScore.From(stored))
    {
    }
}
