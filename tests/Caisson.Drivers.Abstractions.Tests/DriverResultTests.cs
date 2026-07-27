using Caisson.Domain.Enums;
using Caisson.Drivers.Abstractions.Results;
using FluentAssertions;
using Xunit;

namespace Caisson.Drivers.Abstractions.Tests;

/// <summary>AC2/AC3: <see cref="DriverResult{T}"/> semantics for success, partial success, and failure.</summary>
public sealed class DriverResultTests
{
    [Fact]
    public void Ok_with_diagnostics_is_still_successful()
    {
        // AC2's LLDP-partial scenario: the device is reachable but LLDP is disabled on one port —
        // the call succeeds with a warning attached, not a failure.
        var diagnostic = new DriverDiagnostic(
            DriverDiagnosticSeverity.Warning, ReasonCode.MissingLldp, "eth0", "No LLDP frame received.");

        var result = DriverResult<string>.Ok("some-value", TimeSpan.FromMilliseconds(5), new[] { diagnostic });

        result.Success.Should().BeTrue();
        result.Value.Should().Be("some-value");
        result.Error.Should().BeNull();
        result.Diagnostics.Should().ContainSingle().Which.Should().Be(diagnostic);
    }

    [Fact]
    public void Ok_without_diagnostics_defaults_to_empty_list()
    {
        var result = DriverResult<string>.Ok("some-value", TimeSpan.Zero);

        result.Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void Ok_rejects_null_value()
    {
        var act = () => DriverResult<string>.Ok(null!, TimeSpan.Zero);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Fail_always_has_null_value_and_non_null_error()
    {
        var error = new DriverError(DriverErrorCode.DeviceUnreachable, "device did not respond", true);

        var result = DriverResult<string>.Fail(error, TimeSpan.FromSeconds(30));

        result.Success.Should().BeFalse();
        result.Value.Should().BeNull();
        result.Error.Should().Be(error);
        result.Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void Fail_rejects_null_error()
    {
        var act = () => DriverResult<string>.Fail(null!, TimeSpan.Zero);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Result_can_only_be_constructed_via_the_factory_methods()
    {
        typeof(DriverResult<string>)
            .GetConstructors(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Should().BeEmpty("DriverResult<T> must only be constructed via Ok/Fail");
    }
}
