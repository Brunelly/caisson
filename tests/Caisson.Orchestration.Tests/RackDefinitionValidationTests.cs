using Caisson.Orchestration.RackDefinitions;
using FluentAssertions;
using Xunit;

namespace Caisson.Orchestration.Tests;

/// <summary>
/// Finding #33/#8: <see cref="RackDefinitionValidation"/> is the fail-closed, whole-configuration pass —
/// an individually-valid <c>CredentialsRef</c> can still collide with another device's once normalized, or
/// a switch can still pair a non-TLS transport with a meaningless certificate pin, and both must fail
/// startup rather than run silently misconfigured.
/// </summary>
public sealed class RackDefinitionValidationTests
{
    [Fact]
    public void Two_switches_whose_CredentialsRef_normalize_to_the_same_slug_but_differ_fail_validation()
    {
        var options = new RackDefinitionOptions
        {
            Racks =
            {
                new RackDefinitionEntry
                {
                    ExternalKey = "rack-1",
                    Switches =
                    {
                        Switch("sw-a", "rack1_sw"),
                        Switch("sw-b", "RACK1_SW"),
                    },
                },
            },
        };

        var act = () => RackDefinitionValidation.Validate(options);

        act.Should().Throw<InvalidOperationException>().WithMessage("*normalize to the same slug*");
    }

    [Fact]
    public void Distinct_non_colliding_CredentialsRef_values_pass_validation()
    {
        var options = new RackDefinitionOptions
        {
            Racks =
            {
                new RackDefinitionEntry
                {
                    ExternalKey = "rack-1",
                    Switches = { Switch("sw-a", "rack1_sw"), Switch("sw-b", "rack2_sw") },
                    Servers = { Server("srv-a", "bmc1_creds") },
                },
            },
        };

        var act = () => RackDefinitionValidation.Validate(options);

        act.Should().NotThrow();
    }

    [Fact]
    public void An_invalid_CredentialsRef_fails_validation()
    {
        var options = new RackDefinitionOptions
        {
            Racks =
            {
                new RackDefinitionEntry
                {
                    ExternalKey = "rack-1",
                    Switches = { Switch("sw-a", "rack1-sw") },
                },
            },
        };

        var act = () => RackDefinitionValidation.Validate(options);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_tls_fingerprint_paired_with_a_non_tls_switch_fails_validation()
    {
        var options = new RackDefinitionOptions
        {
            Racks =
            {
                new RackDefinitionEntry
                {
                    ExternalKey = "rack-1",
                    Switches = { Switch("sw-a", "rack1_sw", useTls: false, allowPlaintext: true) },
                },
            },
        };

        var act = () => RackDefinitionValidation.Validate(
            options, name => name == "CAISSON_SWITCH_RACK1_SW_TLS_FINGERPRINT" ? "aa:bb:cc" : null);

        act.Should().Throw<InvalidOperationException>().WithMessage("*TLS_FINGERPRINT*");
    }

    [Fact]
    public void A_non_tls_switch_with_no_fingerprint_configured_passes_validation()
    {
        var options = new RackDefinitionOptions
        {
            Racks =
            {
                new RackDefinitionEntry
                {
                    ExternalKey = "rack-1",
                    Switches = { Switch("sw-a", "rack1_sw", useTls: false, allowPlaintext: true) },
                },
            },
        };

        var act = () => RackDefinitionValidation.Validate(options, _ => null);

        act.Should().NotThrow();
    }

    private static DeviceDefinitionEntry Switch(
        string deviceKey, string credentialsRef, bool useTls = true, bool allowPlaintext = false)
        => new()
        {
            DeviceKey = deviceKey,
            Vendor = "MikroTik",
            Host = "10.0.0.1",
            CredentialsRef = credentialsRef,
            UseTls = useTls,
            AllowPlaintext = allowPlaintext,
        };

    private static DeviceDefinitionEntry Server(string deviceKey, string credentialsRef)
        => new() { DeviceKey = deviceKey, Vendor = "HPE", Host = "10.0.0.2", CredentialsRef = credentialsRef };
}
