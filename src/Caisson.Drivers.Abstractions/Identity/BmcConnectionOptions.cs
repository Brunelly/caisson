namespace Caisson.Drivers.Abstractions.Identity;

/// <summary>
/// Connection configuration for a BMC driver instance, bound in by an
/// <see cref="Registry.IBmcDriverFactory"/> rather than passed per call (see ADR 0006). This is
/// deliberately minimal: real credential/secret-store wiring is scoped to the concrete driver
/// implementation stories (#4/#5), not this abstraction-only project.
/// </summary>
/// <param name="Host">The device hostname or IP address to connect to.</param>
/// <param name="Port">The port to connect on, if not the driver's default.</param>
/// <param name="Timeout">The per-call timeout the driver should apply.</param>
/// <param name="CredentialsRef">
/// An opaque reference/name to a secret-store entry (e.g. a vault path or credential-store key) —
/// never the raw secret value itself.
/// </param>
public sealed record BmcConnectionOptions(string Host, int? Port, TimeSpan Timeout, string CredentialsRef);
