namespace Caisson.Drivers.Abstractions.Identity;

/// <summary>
/// Connection configuration for a switch driver instance, bound in by an
/// <see cref="Registry.ISwitchDriverFactory"/> rather than passed per call (see ADR 0006). This is
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
/// <param name="UseTls">
/// Whether the driver should use the TLS RouterOS API transport. Defaults to <c>true</c> (fail-closed):
/// TLS is expressed explicitly rather than inferred from <see cref="Port"/>, so a TLS API reachable on a
/// non-standard port is expressible and a plaintext connection can never happen by omission.
/// </param>
/// <param name="AllowPlaintext">
/// Explicit, per-connection opt-in required to use the plaintext (non-TLS) transport when
/// <see cref="UseTls"/> is <c>false</c>. Defaults to <c>false</c> so sending credentials in cleartext can
/// never be the silent default.
/// </param>
public sealed record SwitchConnectionOptions(
    string Host, int? Port, TimeSpan Timeout, string CredentialsRef,
    bool UseTls = true, bool AllowPlaintext = false);
