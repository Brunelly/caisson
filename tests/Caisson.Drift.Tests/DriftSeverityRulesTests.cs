using Caisson.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace Caisson.Drift.Tests;

/// <summary>Exhaustive coverage of the static severity rule table (story #64, Q2).</summary>
public sealed class DriftSeverityRulesTests
{
    public static IEnumerable<object[]> AllDriftTypes()
        => Enum.GetValues<DriftType>().Select(t => new object[] { t });

    [Theory]
    [MemberData(nameof(AllDriftTypes))]
    public void Every_drift_type_has_a_defined_severity(DriftType driftType)
    {
        var act = () => DriftSeverityRules.For(driftType);
        act.Should().NotThrow();
    }

    [Fact]
    public void The_drift_type_enumeration_is_not_silently_empty()
    {
        // Guards against the theory above vacuously passing if DriftType is ever emptied.
        Enum.GetValues<DriftType>().Should().NotBeEmpty();
    }

    [Theory]
    [InlineData(DriftType.MissingDesiredEntity, DriftSeverity.High)]
    [InlineData(DriftType.ExtraObservedEntity, DriftSeverity.Low)]
    [InlineData(DriftType.AccessVlanMismatch, DriftSeverity.High)]
    [InlineData(DriftType.UnexpectedTrunkConfig, DriftSeverity.Medium)]
    [InlineData(DriftType.UnexpectedNeighbour, DriftSeverity.Medium)]
    [InlineData(DriftType.UnknownTopologyMapping, DriftSeverity.Medium)]
    public void The_static_mapping_matches_the_documented_rule_table(DriftType driftType, DriftSeverity expected)
    {
        DriftSeverityRules.For(driftType).Should().Be(expected);
    }

    [Fact]
    public void An_undefined_drift_type_throws_rather_than_silently_defaulting()
    {
        var act = () => DriftSeverityRules.For((DriftType)999);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
