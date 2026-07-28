using System.Diagnostics.CodeAnalysis;
using Caisson.Drivers.Abstractions.Identity;

namespace Caisson.Drivers.Abstractions.Registry;

/// <summary>
/// A plain dictionary lookup over the <see cref="IBmcDriverFactory"/> instances supplied at
/// construction — no reflection or assembly scanning (NFR4). Duplicate descriptors are rejected
/// fail-fast, matching the codebase's validate-at-construction convention. Resolution matches by
/// (Vendor, Model, ConnectionKind) and is version-agnostic, selecting the highest registered
/// <see cref="DriverDescriptor.DriverVersion"/> when several match (see ADR 0007).
/// </summary>
public sealed class BmcDriverRegistry : IBmcDriverRegistry
{
    private readonly Dictionary<DriverDescriptor, IBmcDriverFactory> _factories;

    /// <summary>Indexes <paramref name="factories"/> by their <see cref="DriverDescriptor"/>.</summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when two factories declare an identical <see cref="DriverDescriptor"/>.
    /// </exception>
    public BmcDriverRegistry(IEnumerable<IBmcDriverFactory> factories)
    {
        _factories = new Dictionary<DriverDescriptor, IBmcDriverFactory>();
        foreach (var factory in factories)
        {
            if (!_factories.TryAdd(factory.Descriptor, factory))
            {
                throw new InvalidOperationException(
                    $"A BMC driver factory is already registered for descriptor '{factory.Descriptor}'.");
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<DriverDescriptor> RegisteredDrivers => _factories.Keys.ToArray();

    /// <inheritdoc />
    public bool TryResolve(DriverDescriptor query, [NotNullWhen(true)] out IBmcDriverFactory? factory)
    {
        IBmcDriverFactory? best = null;
        string? bestVersion = null;
        foreach (var (descriptor, candidate) in _factories)
        {
            // Match by vendor/model/connection-kind only; the query's DriverVersion is ignored.
            if (!string.Equals(descriptor.Vendor, query.Vendor, StringComparison.Ordinal)
                || descriptor.Model != query.Model
                || descriptor.ConnectionKind != query.ConnectionKind)
            {
                continue;
            }

            if (best is null
                || DriverVersionComparer.Instance.Compare(descriptor.DriverVersion, bestVersion) > 0)
            {
                best = candidate;
                bestVersion = descriptor.DriverVersion;
            }
        }

        factory = best;
        return best is not null;
    }
}
