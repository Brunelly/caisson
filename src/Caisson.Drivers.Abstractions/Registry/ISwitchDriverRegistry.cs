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

    /// <summary>
    /// Attempts to resolve a factory for <paramref name="query"/> by matching on its
    /// <c>Vendor</c>, <c>Model</c> and <c>ConnectionKind</c>. The query's
    /// <see cref="DriverDescriptor.DriverVersion"/> is <b>ignored</b> — when several registered drivers
    /// share that key, the highest <c>DriverVersion</c> is selected (see ADR 0007).
    /// </summary>
    /// <returns><c>true</c> if a matching factory was found; <c>false</c> for an unknown descriptor.</returns>
    bool TryResolve(DriverDescriptor query, [NotNullWhen(true)] out ISwitchDriverFactory? factory);
}
