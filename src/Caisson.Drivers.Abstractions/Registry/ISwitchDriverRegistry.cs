using System.Diagnostics.CodeAnalysis;
using Caisson.Drivers.Abstractions.Identity;

namespace Caisson.Drivers.Abstractions.Registry;

/// <summary>
/// Resolves an <see cref="ISwitchDriverFactory"/> by its <see cref="DriverDescriptor"/>. Business
/// logic depends only on this interface, never on a concrete vendor factory type.
/// </summary>
public interface ISwitchDriverRegistry
{
    /// <summary>All descriptors currently registered, for diagnostics/discovery tooling.</summary>
    IReadOnlyList<DriverDescriptor> RegisteredDrivers { get; }

    /// <summary>Attempts to resolve the factory registered for an exact <paramref name="query"/> match.</summary>
    /// <returns><c>true</c> if a factory was found; <c>false</c> for an unknown descriptor.</returns>
    bool TryResolve(DriverDescriptor query, [NotNullWhen(true)] out ISwitchDriverFactory? factory);
}
