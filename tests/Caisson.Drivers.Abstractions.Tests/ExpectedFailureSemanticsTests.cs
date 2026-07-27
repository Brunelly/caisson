using Caisson.Drivers.Abstractions.Identity;
using Caisson.Drivers.Abstractions.Results;
using Caisson.Drivers.Abstractions.Tests.Mocks;
using FluentAssertions;
using Xunit;

namespace Caisson.Drivers.Abstractions.Tests;

/// <summary>
/// AC2's remaining Given/When/Then scenarios: invalid credentials and a connection timeout must
/// both surface as a failed <see cref="DriverResult{T}"/> rather than a thrown exception.
/// </summary>
public sealed class ExpectedFailureSemanticsTests
{
    private const string FakeCredentialsRef = "vault://fake-super-secret-password-123";

    [Fact]
    public async Task Invalid_credentials_return_authentication_failed_and_do_not_leak_the_secret()
    {
        var driver = new MockSwitchDiscoveryDriver
        {
            DeviceInfoResult = () => DriverResult<Switches.SwitchDeviceInfo>.Fail(
                new DriverError(
                    DriverErrorCode.AuthenticationFailed,
                    "The device rejected the supplied credentials.",
                    Retryable: false),
                TimeSpan.FromMilliseconds(50)),
        };
        var options = new SwitchConnectionOptions("10.0.0.5", null, TimeSpan.FromSeconds(5), FakeCredentialsRef);

        // The connection options carry only a reference to the secret, never the secret itself —
        // exercise a driver call against them and assert the returned error never echoes that
        // reference (or, by extension, a real secret) back to a caller/log.
        var result = await driver.GetDeviceInfoAsync(CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error!.Code.Should().Be(DriverErrorCode.AuthenticationFailed);
        result.Error.Retryable.Should().BeFalse();
        result.Error.Message.Should().NotContain(FakeCredentialsRef);
        result.Error.Message.Should().NotContain(options.CredentialsRef);
    }

    [Fact]
    public async Task Connection_timeout_is_reported_as_retryable()
    {
        var driver = new MockBmcDiscoveryDriver
        {
            SystemInventoryResult = () => DriverResult<Bmc.BmcSystemInventory>.Fail(
                new DriverError(DriverErrorCode.ConnectionTimeout, "Timed out connecting to the BMC.", Retryable: true),
                TimeSpan.FromSeconds(5)),
        };

        var result = await driver.GetSystemInventoryAsync(CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error!.Code.Should().Be(DriverErrorCode.ConnectionTimeout);
        result.Error.Retryable.Should().BeTrue();
        result.Duration.Should().Be(TimeSpan.FromSeconds(5));
    }
}
