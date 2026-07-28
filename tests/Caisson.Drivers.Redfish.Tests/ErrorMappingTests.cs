using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using Caisson.Drivers.Abstractions.Results;
using Caisson.Drivers.Redfish.Tests.Fakes;
using Caisson.Drivers.Redfish.Tests.Fixtures;
using Caisson.Drivers.Redfish.Transport;
using FluentAssertions;
using Xunit;

namespace Caisson.Drivers.Redfish.Tests;

/// <summary>
/// AC2/AC3/NFR2: expected failures are mapped to <see cref="DriverError"/> codes (never thrown) with the
/// correct retryability. When both Redfish and its IPMI fallback fail, the driver still returns a structured
/// failure rather than raising.
/// </summary>
public sealed class ErrorMappingTests : IDisposable
{
    private readonly RedfishDriverHarness _harness = new();

    // No IPMI data configured — the stub reports every subcommand unavailable, so the Redfish failure is
    // what surfaces after the (empty) fallback attempt.
    private readonly StubIpmiCommandRunner _noIpmi = new();

    [Fact]
    public async Task Auth_failure_maps_to_authentication_failed_and_is_not_retryable()
    {
        var client = new FakeRedfishClient();
        client.SetThrows(RedfishFixtures.ServiceRootPath,
            () => new RedfishAuthenticationException("The Redfish endpoint rejected the credentials (HTTP 401)."));

        var result = await _harness.Build(client, _noIpmi).GetSystemInventoryAsync(CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error!.Code.Should().Be(DriverErrorCode.AuthenticationFailed);
        result.Error.Retryable.Should().BeFalse();
    }

    [Fact]
    public async Task Timeout_maps_to_connection_timeout_and_is_retryable()
    {
        var client = new FakeRedfishClient();
        client.SetThrows(RedfishFixtures.ServiceRootPath, () => new TimeoutException("timed out"));

        var result = await _harness.Build(client, _noIpmi).GetSystemInventoryAsync(CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error!.Code.Should().Be(DriverErrorCode.ConnectionTimeout);
        result.Error.Retryable.Should().BeTrue();
    }

    [Fact]
    public async Task Connection_refused_maps_to_connection_refused_and_is_retryable()
    {
        var client = new FakeRedfishClient();
        client.SetThrows(RedfishFixtures.ServiceRootPath,
            () => new SocketException((int)SocketError.ConnectionRefused));

        var result = await _harness.Build(client, _noIpmi).GetSystemInventoryAsync(CancellationToken.None);

        result.Error!.Code.Should().Be(DriverErrorCode.ConnectionRefused);
        result.Error.Retryable.Should().BeTrue();
    }

    [Fact]
    public async Task Unreachable_host_maps_to_device_unreachable()
    {
        var client = new FakeRedfishClient();
        client.SetThrows(RedfishFixtures.ServiceRootPath,
            () => new HttpRequestException("No such host is known."));

        var result = await _harness.Build(client, _noIpmi).GetSystemInventoryAsync(CancellationToken.None);

        result.Error!.Code.Should().Be(DriverErrorCode.DeviceUnreachable);
        result.Error.Retryable.Should().BeTrue();
    }

    [Fact]
    public async Task Malformed_json_maps_to_parse_error_and_is_not_retryable()
    {
        var client = new FakeRedfishClient();
        client.SetJson(RedfishFixtures.ServiceRootPath, "this is not json {");

        var result = await _harness.Build(client, _noIpmi).GetSystemInventoryAsync(CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error!.Code.Should().Be(DriverErrorCode.ParseError);
        result.Error.Retryable.Should().BeFalse();
    }

    [Fact]
    public void The_json_fixture_actually_fails_to_deserialize()
    {
        // Guards the assumption behind the parse-error test above.
        var act = () => JsonSerializer.Deserialize("this is not json {", RedfishJsonContextForTests());
        act.Should().Throw<JsonException>();
    }

    private static System.Text.Json.Serialization.Metadata.JsonTypeInfo<Model.ServiceRoot> RedfishJsonContextForTests()
        => Serialization.RedfishJsonContext.Default.ServiceRoot;

    public void Dispose() => _harness.Dispose();
}
