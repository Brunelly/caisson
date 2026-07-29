using Caisson.Drivers.MikroTik;
using Caisson.Drivers.MikroTik.Credentials;
using Caisson.Drivers.MikroTik.Observability;
using Caisson.Drivers.MikroTik.Transport;
using FluentAssertions;
using Xunit;

namespace Caisson.Drivers.MikroTik.Tests;

/// <summary>
/// NFR4: no secret material appears in a <see cref="Caisson.Drivers.Abstractions.Results.DriverError"/>
/// message or in captured log output, even when an underlying exception message contains it.
/// </summary>
public sealed class RedactionTests
{
    private const string Password = "hunter2-SUPER-secret";
    private const string CredentialsRef = "vault://switches/core-secret";

    [Fact]
    public async Task Driver_error_and_logs_never_echo_a_secret_from_an_underlying_exception()
    {
        using var metrics = new RouterOsMetrics();
        var logger = new CapturingLogger<RouterOsSwitchDriver>();

        // The transport (carelessly) put the password into the exception message; the driver must not
        // propagate it into the DriverError.Message or its logs.
        var client = new FakeRouterOsApiClient
        {
            OnConnect = () => throw new RouterOsAuthenticationException(
                $"login rejected for password={Password} ref={CredentialsRef}"),
        };
        var driver = new RouterOsSwitchDriver("10.0.0.1", () => client, TimeSpan.FromSeconds(2), metrics, logger);

        var result = await driver.GetDeviceInfoAsync(CancellationToken.None);

        result.Error!.Message.Should().NotContain(Password);
        result.Error.Message.Should().NotContain(CredentialsRef);
        logger.AllText.Should().NotContain(Password);
        logger.AllText.Should().NotContain(CredentialsRef);
    }

    [Fact]
    public async Task Transport_command_log_never_contains_the_password()
    {
        var logger = new CapturingLogger<RouterOsApiClient>();
        var settings = new RouterOsConnectionSettings(
            "10.0.0.1", 8728, UseTls: false, "reader", Password, TimeSpan.FromSeconds(2));

        // A framed "!done" reply lets the real command path run over an in-memory stream (no socket).
        using var replyBuffer = new MemoryStream();
        await RouterOsSentence.WriteAsync(replyBuffer, new[] { "!done" }, CancellationToken.None);
        var reply = new OneWayReplyStream(replyBuffer.ToArray());

        await using var client = new RouterOsApiClient(settings, logger, reply);
        var rows = await client.SendCommandAsync(RouterOsReadCommands.Interfaces, CancellationToken.None);

        rows.Should().BeEmpty();
        logger.AllText.Should().NotBeEmpty();
        logger.AllText.Should().NotContain(Password);
    }

    [Fact]
    public void RouterOsConnectionSettings_ToString_never_contains_the_password()
    {
        var settings = new RouterOsConnectionSettings(
            "10.0.0.1", 8729, UseTls: true, "reader", Password, TimeSpan.FromSeconds(2));

        settings.ToString().Should().NotContain(Password);
        settings.ToString().Should().Contain("reader");
    }

    [Fact]
    public void SwitchCredentials_ToString_never_contains_the_password()
    {
        var credentials = new SwitchCredentials("reader", Password);

        credentials.ToString().Should().NotContain(Password);
    }
}
