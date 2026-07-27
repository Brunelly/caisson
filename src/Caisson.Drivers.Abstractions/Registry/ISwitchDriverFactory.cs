using Caisson.Drivers.Abstractions.Identity;
using Caisson.Drivers.Abstractions.ReadOnly;

namespace Caisson.Drivers.Abstractions.Registry;

/// <summary>
/// Creates <see cref="ISwitchDiscoveryDriver"/> instances for a specific vendor/model/connection
/// kind, binding connection configuration into the instance at creation time (per the story's
/// answered question). Registered with DI via <c>AddSwitchDriver{TFactory}()</c> and resolved
/// through <see cref="ISwitchDriverRegistry"/>.
/// </summary>
public interface ISwitchDriverFactory
{
    /// <summary>The identity/capability metadata this factory's drivers report.</summary>
    DriverDescriptor Descriptor { get; }

    /// <summary>Creates a driver instance bound to the given connection configuration.</summary>
    ISwitchDiscoveryDriver Create(SwitchConnectionOptions options);
}
