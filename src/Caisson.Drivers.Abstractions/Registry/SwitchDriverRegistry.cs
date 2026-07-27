using System.Diagnostics.CodeAnalysis;
using Caisson.Drivers.Abstractions.Identity;

namespace Caisson.Drivers.Abstractions.Registry;

/// <summary>
/// A plain dictionary lookup over the <see cref="ISwitchDriverFactory"/> instances supplied at
/// construction — no reflection or assembly scanning (NFR4). Duplicate descriptors are rejected
/// fail-fast, matching the codebase's validate-at-construction convention.
/// </summary>
public sealed class SwitchDriverRegistry : ISwitchDriverRegistry
{
    private readonly Dictionary<DriverDescriptor, ISwitchDriverFactory> _factories;

    /// <summary>Indexes <paramref name="factories"/> by their <see cref="DriverDescriptor"/>.</summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when two factories declare an identical <see cref="DriverDescriptor"/>.
    /// </exception>
    public SwitchDriverRegistry(IEnumerable<ISwitchDriverFactory> factories)
    {
        _factories = new Dictionary<DriverDescriptor, ISwitchDriverFactory>();
        foreach (var factory in factories)
        {
            if (!_factories.TryAdd(factory.Descriptor, factory))
            {
                throw new InvalidOperationException(
                    $"A switch driver factory is already registered for descriptor '{factory.Descriptor}'.");
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<DriverDescriptor> RegisteredDrivers => _factories.Keys.ToArray();

    /// <inheritdoc />
    public bool TryResolve(DriverDescriptor query, [NotNullWhen(true)] out ISwitchDriverFactory? factory)
        => _factories.TryGetValue(query, out factory);
}
