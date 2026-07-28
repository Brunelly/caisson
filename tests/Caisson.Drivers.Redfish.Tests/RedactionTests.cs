using System.Net;
using Caisson.Drivers.Redfish.Tests.Fakes;
using Caisson.Drivers.Redfish.Tests.Fixtures;
using Caisson.Drivers.Redfish.Transport;
using FluentAssertions;
using Xunit;

namespace Caisson.Drivers.Redfish.Tests;

/// <summary>
/// NFR3: no secret material (password, Basic Authorization header, session token, ipmitool stderr secret)
/// appears in a <c>DriverError.Message</c> or in captured log output, even when an underlying exception
/// embeds it.
/// </summary>
public sealed class RedactionTests : IDisposable
{
    private const string Password = "hunter2-SUPER-secret";
    private const string Token = "X-Auth-Token-9f8e7d6c";

    private readonly RedfishDriverHarness _harness = new();

    [Fact]
    public async Task Driver_error_and_logs_never_echo_a_secret_from_an_underlying_exception()
    {
        var logger = new CapturingLogger<RedfishBmcDriver>();

        // The transport (carelessly) put the password and a token into the exception message; the driver
        // must not propagate them into the DriverError.Message or its logs.
        var client = new FakeRedfishClient();
        client.SetThrows(RedfishFixtures.ServiceRootPath,
            () => new RedfishAuthenticationException($"rejected password={Password} token={Token}"));

        var result = await _harness.Build(client, new StubIpmiCommandRunner(), logger)
            .GetSystemInventoryAsync(CancellationToken.None);

        result.Error!.Message.Should().NotContain(Password);
        result.Error.Message.Should().NotContain(Token);
        logger.AllText.Should().NotContain(Password);
        logger.AllText.Should().NotContain(Token);
    }

    [Fact]
    public async Task Transport_get_log_never_contains_the_authorization_header_or_password()
    {
        var logger = new CapturingLogger<RedfishClient>();
        var settings = new RedfishConnectionSettings(
            "10.4.7.5", 443, "reader", Password, TimeSpan.FromSeconds(2));

        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, RedfishFixtures.ServiceRoot);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://10.4.7.5:443") };
        using var client = new RedfishClient(settings, logger, http);

        var body = await client.GetAsync(RedfishFixtures.ServiceRootPath, CancellationToken.None);

        body.Should().NotBeEmpty();
        // Basic auth was actually sent (the base64 of reader:password)...
        handler.SeenAuthorization.Should().StartWith("Basic ");
        // ...but neither the password nor the encoded credential ever reaches the log.
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"reader:{Password}"));
        logger.AllText.Should().NotBeEmpty();
        logger.AllText.Should().NotContain(Password);
        logger.AllText.Should().NotContain(encoded);
        logger.AllText.Should().NotContain("Authorization");
    }

    public void Dispose() => _harness.Dispose();
}
