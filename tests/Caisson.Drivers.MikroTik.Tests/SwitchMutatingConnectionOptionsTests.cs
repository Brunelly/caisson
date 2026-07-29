using Caisson.Drivers.Abstractions.Identity;
using FluentAssertions;
using Xunit;

namespace Caisson.Drivers.MikroTik.Tests;

/// <summary>
/// NFR2: the confirmed-commit window defaults conservatively (30s, the story's answered question) and
/// is configurable. This is a plain options/TimeSpan-math unit test, kept separate from the
/// simulator-backed integration suite so the default itself is asserted without depending on any
/// simulator timing behaviour.
/// </summary>
public sealed class SwitchMutatingConnectionOptionsTests
{
    [Fact]
    public void Default_confirm_window_is_30_seconds()
    {
        SwitchMutatingConnectionOptions.DefaultConfirmWindow.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void ConfirmWindow_is_nullable_so_the_conservative_default_applies_when_omitted()
    {
        var options = new SwitchMutatingConnectionOptions("10.0.0.1", null, TimeSpan.FromSeconds(2), "core_switch");

        options.ConfirmWindow.Should().BeNull();
    }

    [Fact]
    public void ConfirmWindow_can_be_configured_per_environment()
    {
        var options = new SwitchMutatingConnectionOptions(
            "10.0.0.1", null, TimeSpan.FromSeconds(2), "core_switch", ConfirmWindow: TimeSpan.FromSeconds(60));

        options.ConfirmWindow.Should().Be(TimeSpan.FromSeconds(60));
    }
}
