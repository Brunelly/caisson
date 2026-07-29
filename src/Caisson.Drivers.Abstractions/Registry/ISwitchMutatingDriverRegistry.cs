using System.Diagnostics.CodeAnalysis;
using Caisson.Drivers.Abstractions.Identity;

namespace Caisson.Drivers.Abstractions.Registry;

/// <summary>
/// Resolves an <see cref="ISwitchMutatingDriverFactory"/> by its <see cref="DriverDescriptor"/>. A
/// second, distinct registry from <see cref="ISwitchDriverRegistry"/> (AC1): a consumer holding only an
/// <see cref="ISwitchDriverRegistry"/> reference cannot structurally reach a mutating factory through it.
/// </summary>
public interface ISwitchMutatingDriverRegistry
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
    bool TryResolve(DriverDescriptor query, [NotNullWhen(true)] out ISwitchMutatingDriverFactory? factory);
}
