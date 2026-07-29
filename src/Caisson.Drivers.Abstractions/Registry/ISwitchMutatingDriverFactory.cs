using Caisson.Drivers.Abstractions.Identity;
using Caisson.Drivers.Abstractions.Mutating;

namespace Caisson.Drivers.Abstractions.Registry;

/// <summary>
/// Creates <see cref="ISwitchMutatingDriver"/> instances for a specific vendor/model/connection kind,
/// binding connection configuration into the instance at creation time — mirroring
/// <see cref="ISwitchDriverFactory"/>, but kept as a distinct interface/type (AC1) so a write-capable
/// factory can never be resolved through the read-only <see cref="ISwitchDriverRegistry"/>. Registered
/// with DI via <c>AddSwitchMutatingDriver{TFactory}()</c> and resolved through
/// <see cref="ISwitchMutatingDriverRegistry"/>.
/// </summary>
public interface ISwitchMutatingDriverFactory
{
    /// <summary>The identity/capability metadata this factory's drivers report.</summary>
    DriverDescriptor Descriptor { get; }

    /// <summary>Creates a write-capable driver instance bound to the given connection configuration.</summary>
    ISwitchMutatingDriver Create(SwitchMutatingConnectionOptions options);
}
