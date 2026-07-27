using System.Globalization;

namespace Caisson.Domain.ValueObjects;

/// <summary>
/// A correlation-confidence score bounded to the inclusive range <c>[0.0, 1.0]</c>. Values outside
/// the range or <see cref="double.NaN"/> are rejected at construction. The same bound is enforced
/// again at the database level by a CHECK constraint (see ADR 0004) for defence in depth.
/// </summary>
public readonly record struct ConfidenceScore
{
    /// <summary>The lowest permitted confidence value.</summary>
    public const double Minimum = 0.0;

    /// <summary>The highest permitted confidence value.</summary>
    public const double Maximum = 1.0;

    private ConfidenceScore(double value) => Value = value;

    /// <summary>The bounded confidence value in <c>[0.0, 1.0]</c>.</summary>
    public double Value { get; }

    /// <summary>Creates a score, validating the <c>[0.0, 1.0]</c> bound.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="value"/> is <see cref="double.NaN"/> or outside <c>[0.0, 1.0]</c>.
    /// </exception>
    public static ConfidenceScore From(double value)
    {
        if (double.IsNaN(value) || value < Minimum || value > Maximum)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value), value, $"Confidence must be within [{Minimum}, {Maximum}].");
        }

        return new ConfidenceScore(value);
    }

    /// <summary>Attempts to create a score, returning <c>false</c> for out-of-range or NaN input.</summary>
    public static bool TryFrom(double value, out ConfidenceScore result)
    {
        if (double.IsNaN(value) || value < Minimum || value > Maximum)
        {
            result = default;
            return false;
        }

        result = new ConfidenceScore(value);
        return true;
    }

    /// <summary>Returns the invariant-culture string form of the value.</summary>
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}
