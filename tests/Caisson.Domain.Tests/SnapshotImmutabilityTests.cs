using System.Reflection;
using Caisson.Domain.Topology;
using FluentAssertions;
using Xunit;

namespace Caisson.Domain.Tests;

/// <summary>
/// Structural guarantees that snapshot content is not mutable through the public surface: audit
/// fields have no public setter and observed collections are exposed read-only. (The runtime
/// append-only guard is exercised additionally by the Infrastructure integration tests.)
/// </summary>
public sealed class SnapshotImmutabilityTests
{
    [Theory]
    [InlineData(nameof(TopologySnapshot.Id))]
    [InlineData(nameof(TopologySnapshot.RackId))]
    [InlineData(nameof(TopologySnapshot.CreatedAtUtc))]
    [InlineData(nameof(TopologySnapshot.CreatedBy))]
    [InlineData(nameof(TopologySnapshot.Source))]
    [InlineData(nameof(TopologySnapshot.SourceVersion))]
    [InlineData(nameof(TopologySnapshot.CorrelationId))]
    [InlineData(nameof(TopologySnapshot.Status))]
    public void Snapshot_audit_fields_have_no_public_setter(string propertyName)
    {
        var setter = typeof(TopologySnapshot).GetProperty(propertyName)!.SetMethod;

        (setter is null || !setter.IsPublic).Should().BeTrue(
            "'{0}' must not be publicly settable to preserve append-only immutability", propertyName);
    }

    [Theory]
    [InlineData(nameof(TopologySnapshot.Switches))]
    [InlineData(nameof(TopologySnapshot.Servers))]
    [InlineData(nameof(TopologySnapshot.Vlans))]
    [InlineData(nameof(TopologySnapshot.CandidateMappings))]
    public void Snapshot_collections_are_read_only(string propertyName)
    {
        var property = typeof(TopologySnapshot).GetProperty(propertyName)!;

        property.SetMethod.Should().BeNull("'{0}' must be a get-only collection", propertyName);
        property.PropertyType.GetGenericTypeDefinition().Should().Be(typeof(IReadOnlyCollection<>));
    }

    [Fact]
    public void Every_snapshot_scoped_entity_exposes_no_public_setters_on_key_audit_fields()
    {
        var entityTypes = typeof(ISnapshotScoped).Assembly
            .GetTypes()
            .Where(t => typeof(ISnapshotScoped).IsAssignableFrom(t) && t is { IsClass: true, IsAbstract: false });

        foreach (var type in entityTypes)
        {
            foreach (var name in new[] { nameof(ISnapshotScoped.SnapshotId), nameof(ISnapshotScoped.RackId) })
            {
                var setter = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)!.SetMethod;
                (setter is null || !setter.IsPublic).Should().BeTrue(
                    "{0}.{1} must not be publicly settable", type.Name, name);
            }
        }
    }
}
