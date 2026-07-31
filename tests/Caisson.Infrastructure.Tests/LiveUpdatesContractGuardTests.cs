using System.Reflection;
using System.Text.Json;
using Caisson.Infrastructure.LiveUpdates;
using FluentAssertions;
using Xunit;

namespace Caisson.Infrastructure.Tests;

/// <summary>
/// Contract guard for the live-updates wire format (story #9, NFR5), mirroring the domain no-secrets
/// guard. Events must expose only ids/version/status/counts/timestamps/seq/correlationId — never
/// host/port/MAC/credentialsRef/graph or raw device data. Both the reflected property names AND the
/// serialized JSON are checked, so nothing sensitive can leak onto the channel or a SignalR payload.
/// </summary>
public sealed class LiveUpdatesContractGuardTests
{
    // Substrings that would signal host/port/MAC/credential/graph/raw-device data leaking into an event.
    private static readonly string[] ForbiddenMarkers =
    {
        "host", "port", "mac", "credential", "graph", "password", "secret",
        "passphrase", "apikey", "privatekey", "device", "serialnumber", "ipaddress",
    };

    private static readonly Type[] ContractTypes =
    {
        typeof(TopologyEvent),
        typeof(SnapshotUpdatedEvent),
        typeof(SnapshotSummary),
        typeof(DiscoveryJobStatusChangedEvent),
        typeof(DriftApplyJobStatusChangedEvent),
        typeof(GitPullRequestStatusChangedEvent),
        typeof(HeartbeatEvent),
    };

    public static IEnumerable<object[]> ContractProperties()
    {
        foreach (var type in ContractTypes)
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                yield return new object[] { type.Name, property.Name };
            }
        }
    }

    [Theory]
    [MemberData(nameof(ContractProperties))]
    public void No_event_property_exposes_sensitive_data(string typeName, string propertyName)
    {
        ForbiddenMarkers.Should().NotContain(
            marker => propertyName.Contains(marker, StringComparison.OrdinalIgnoreCase),
            "{0}.{1} must not expose host/port/MAC/credential/graph/device data (NFR5)", typeName, propertyName);
    }

    [Fact]
    public void Serialized_event_json_contains_no_sensitive_field_names()
    {
        var snapshot = new SnapshotUpdatedEvent(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 7,
            new SnapshotSummary(2, 20, 3, 4, 0, 1), DateTimeOffset.UnixEpoch, 7, Guid.NewGuid());
        var status = new DiscoveryJobStatusChangedEvent(
            Guid.NewGuid(), Guid.NewGuid(), "Failed", "InProgress", null, "SWITCH_DISCOVERY_FAILED",
            DateTimeOffset.UnixEpoch, 3, Guid.NewGuid());
        var prStatus = new GitPullRequestStatusChangedEvent(
            Guid.NewGuid(), "octo", "repo", 7, "https://gh/pr/7", "Merged", "abc123", "Success", 0,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 5, Guid.NewGuid());

        foreach (var @event in new TopologyEvent[] { snapshot, status, prStatus, new HeartbeatEvent(DateTimeOffset.UnixEpoch) })
        {
            var json = TopologyEventSerialization.Serialize(@event);
            using var document = JsonDocument.Parse(json);
            foreach (var name in PropertyNames(document.RootElement))
            {
                ForbiddenMarkers.Should().NotContain(
                    marker => name.Contains(marker, StringComparison.OrdinalIgnoreCase),
                    "serialized {0} must not carry a '{1}' field (NFR5)", @event.GetType().Name, name);
            }
        }
    }

    private static IEnumerable<string> PropertyNames(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    yield return property.Name;
                    foreach (var nested in PropertyNames(property.Value))
                    {
                        yield return nested;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var nested in PropertyNames(item))
                    {
                        yield return nested;
                    }
                }

                break;
        }
    }
}
