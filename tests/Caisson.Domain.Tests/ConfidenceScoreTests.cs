using Caisson.Domain.ValueObjects;
using FluentAssertions;
using Xunit;

namespace Caisson.Domain.Tests;

public sealed class ConfidenceScoreTests
{
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(0.999)]
    [InlineData(1.0)]
    public void From_accepts_values_within_the_inclusive_bound(double value)
    {
        var score = ConfidenceScore.From(value);

        score.Value.Should().Be(value);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void From_rejects_out_of_range_and_non_finite_values(double value)
    {
        var act = () => ConfidenceScore.From(value);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0.0, true)]
    [InlineData(1.0, true)]
    [InlineData(-0.01, false)]
    [InlineData(1.01, false)]
    [InlineData(double.NaN, false)]
    public void TryFrom_reports_validity_without_throwing(double value, bool expected)
    {
        ConfidenceScore.TryFrom(value, out var score).Should().Be(expected);
        if (expected)
        {
            score.Value.Should().Be(value);
        }
    }

    [Fact]
    public void Scores_with_the_same_value_are_equal()
    {
        ConfidenceScore.From(0.75).Should().Be(ConfidenceScore.From(0.75));
    }
}
