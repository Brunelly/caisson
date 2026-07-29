namespace Caisson.Drivers.MikroTik.Transport;

/// <summary>
/// The read-only RouterOS API surface the driver depends on. Abstracted so
/// <see cref="Caisson.Drivers.MikroTik.RouterOsSwitchDriver"/> can be unit-tested against a fake client
/// without a socket, while the real <see cref="RouterOsApiClient"/> owns the wire protocol.
/// </summary>
public interface IRouterOsApiClient : IAsyncDisposable
{
    /// <summary>Opens the socket and performs the RouterOS login handshake.</summary>
    Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Sends a single read-only <c>print</c> <paramref name="command"/> and returns one raw key/value
    /// map per <c>!re</c> reply row (the evidence used by the mappers).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown before any I/O when <paramref name="command"/> is not on the read-only allowlist.
    /// </exception>
    Task<IReadOnlyList<IReadOnlyDictionary<string, string>>> SendCommandAsync(
        string command, CancellationToken cancellationToken);
}

/// <summary>
/// Everything <see cref="RouterOsApiClient"/> needs to open and authenticate a connection. Built by the
/// factory from <c>SwitchConnectionOptions</c> plus credentials resolved from a <c>CredentialsRef</c>;
/// the raw <see cref="Password"/> lives only here and is never logged.
/// </summary>
/// <param name="Host">Device hostname or IP.</param>
/// <param name="Port">TCP port (8728 plain, 8729 TLS).</param>
/// <param name="UseTls">Whether to wrap the socket in TLS (port 8729).</param>
/// <param name="Username">RouterOS API username (least-privilege read+api user).</param>
/// <param name="Password">RouterOS API password.</param>
/// <param name="Timeout">Per-command timeout applied via a linked cancellation source.</param>
/// <param name="CertificateThumbprint">
/// Optional SHA-256 certificate fingerprint to pin the TLS peer against (hex, separators/whitespace and
/// case ignored). When set, the self-signed CHR certificate is trusted only if its fingerprint matches;
/// this is the recommended way to secure the 8729 transport against an active man-in-the-middle.
/// </param>
/// <param name="AllowUntrustedCertificate">
/// Explicit, per-connection opt-in that accepts an otherwise-untrusted TLS certificate (self-signed,
/// expired or name-mismatched) when no <see cref="CertificateThumbprint"/> pin is configured. Defaults
/// to <c>false</c> so blanket acceptance can never be the silent default (CWE-295).
/// </param>
public sealed record RouterOsConnectionSettings(
    string Host, int Port, bool UseTls, string Username, string Password, TimeSpan Timeout,
    string? CertificateThumbprint = null, bool AllowUntrustedCertificate = false)
{
    /// <summary>
    /// Overrides the compiler-generated record <c>ToString()</c> — which would otherwise print every
    /// positional member, including <see cref="Password"/> — so an accidental <c>settings.ToString()</c>
    /// (e.g. in a debugger watch or a future log call) can never leak the credential.
    /// </summary>
    public override string ToString() => $"{Host}:{Port} as {Username} (UseTls={UseTls})";
}

/// <summary>A RouterOS protocol-level failure (a <c>!trap</c>/<c>!fatal</c> reply or a malformed sentence).</summary>
public class RouterOsApiException : Exception
{
    /// <summary>Creates the exception with a caller-supplied, secret-free message.</summary>
    public RouterOsApiException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception wrapping an underlying cause.</summary>
    public RouterOsApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A RouterOS login failure — invalid credentials or insufficient permission.</summary>
public sealed class RouterOsAuthenticationException : RouterOsApiException
{
    /// <summary>Creates the exception with a message that must never contain credential material.</summary>
    public RouterOsAuthenticationException(string message)
        : base(message)
    {
    }
}
