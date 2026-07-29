namespace Caisson.Drivers.Abstractions.Identity;

/// <summary>
/// Connection configuration for a write-capable switch driver instance, bound in by an
/// <see cref="Registry.ISwitchMutatingDriverFactory"/>. Deliberately a distinct type from
/// <see cref="SwitchConnectionOptions"/> — even though the shape mirrors it — so constructing a
/// mutating driver is structurally different from constructing a read-only one (AC1): a consumer that
/// only ever builds <see cref="SwitchConnectionOptions"/> values cannot accidentally end up with a
/// write-capable driver by construction.
/// </summary>
/// <param name="Host">The device hostname or IP address to connect to.</param>
/// <param name="Port">The port to connect on, if not the driver's default.</param>
/// <param name="Timeout">The per-call timeout the driver should apply.</param>
/// <param name="CredentialsRef">
/// An opaque reference/name to a secret-store entry (e.g. a vault path or credential-store key) — never
/// the raw secret value itself. Write operations may reasonably require a more privileged RouterOS user
/// than discovery; that is an operational (credential provisioning) concern, not a type-shape one.
/// </param>
/// <param name="UseTls">Whether the driver should use the TLS RouterOS API transport. Defaults to <c>true</c> (fail-closed).</param>
/// <param name="AllowPlaintext">
/// Explicit, per-connection opt-in required to use the plaintext (non-TLS) transport when
/// <see cref="UseTls"/> is <c>false</c>. Defaults to <c>false</c>.
/// </param>
/// <param name="ConfirmWindow">
/// The confirmed-commit window to arm before an apply, if not overridden per-request via
/// <c>SetAccessVlanRequest.ConfirmWindow</c>. When <c>null</c>, <see cref="DefaultConfirmWindow"/> applies.
/// </param>
public sealed record SwitchMutatingConnectionOptions(
    string Host, int? Port, TimeSpan Timeout, string CredentialsRef,
    bool UseTls = true, bool AllowPlaintext = false, TimeSpan? ConfirmWindow = null)
{
    /// <summary>
    /// The conservative default confirmed-commit window (30 seconds — the story's answered question)
    /// applied when neither the connection options nor an individual request specify one.
    /// </summary>
    public static readonly TimeSpan DefaultConfirmWindow = TimeSpan.FromSeconds(30);
}
