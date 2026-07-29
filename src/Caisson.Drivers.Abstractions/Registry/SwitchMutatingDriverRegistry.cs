using System.Diagnostics.CodeAnalysis;
using Caisson.Drivers.Abstractions.Identity;

namespace Caisson.Drivers.Abstractions.Registry;

/// <summary>
/// A plain dictionary lookup over the <see cref="ISwitchMutatingDriverFactory"/> instances supplied at
/// construction — no reflection or assembly scanning, mirroring <see cref="SwitchDriverRegistry"/>.
/// Duplicate descriptors are rejected fail-fast. Resolution matches by (Vendor, Model, ConnectionKind)
/// and is version-agnostic, selecting the highest registered <see cref="DriverDescriptor.DriverVersion"/>
/// when several match (see ADR 0007).
/// </summary>
public sealed class SwitchMutatingDriverRegistry : ISwitchMutatingDriverRegistry
{
    private readonly Dictionary<DriverDescriptor, ISwitchMutatingDriverFactory> _factories;

    /// <summary>Indexes <paramref name="factories"/> by their <see cref="DriverDescriptor"/>.</summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when two factories declare an identical <see cref="DriverDescriptor"/>.
    /// </exception>
    public SwitchMutatingDriverRegistry(IEnumerable<ISwitchMutatingDriverFactory> factories)
    {
        _factories = new Dictionary<DriverDescriptor, ISwitchMutatingDriverFactory>();
        foreach (var factory in factories)
        {
            if (!_factories.TryAdd(factory.Descriptor, factory))
            {
                throw new InvalidOperationException(
                    $"A switch mutating driver factory is already registered for descriptor '{factory.Descriptor}'.");
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<DriverDescriptor> RegisteredDrivers => _factories.Keys.ToArray();

    /// <inheritdoc />
    public bool TryResolve(DriverDescriptor query, [NotNullWhen(true)] out ISwitchMutatingDriverFactory? factory)
    {
        ISwitchMutatingDriverFactory? best = null;
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
