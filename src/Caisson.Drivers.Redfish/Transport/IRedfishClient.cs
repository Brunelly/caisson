namespace Caisson.Drivers.Redfish.Transport;

/// <summary>
/// The read-only Redfish surface the driver depends on. Abstracted so
/// <see cref="Caisson.Drivers.Redfish.RedfishBmcDriver"/> can be unit-tested against a fake client
/// without a socket, while the real <see cref="RedfishClient"/> owns the HTTPS/JSON transport. The
/// interface exposes <b>no mutating verb</b> — there is only a GET — so the read-only boundary is visible
/// in the type itself, and the single <see cref="GetAsync"/> chokepoint additionally enforces the
/// <see cref="RedfishReadPaths"/> allowlist before any I/O.
/// </summary>
public interface IRedfishClient : IDisposable
{
    /// <summary>
    /// Issues a read-only <c>GET</c> against <paramref name="path"/> (an absolute Redfish resource path
    /// such as <c>/redfish/v1/Systems</c>) and returns the raw response body as a string for the caller to
    /// deserialize through the source-generated JSON context.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown before any I/O when <paramref name="path"/> is not an allowlisted read-only GET
    /// (see <see cref="RedfishReadPaths.IsReadOnlyGet"/>).
    /// </exception>
    /// <exception cref="RedfishAuthenticationException">Thrown on an HTTP 401/403 response.</exception>
    /// <exception cref="RedfishException">Thrown on any other non-success HTTP status.</exception>
    Task<string> GetAsync(string path, CancellationToken cancellationToken);
}

/// <summary>
/// Everything <see cref="RedfishClient"/> needs to open and authenticate a connection. Built by the
/// factory from <c>BmcConnectionOptions</c> plus credentials resolved from a <c>CredentialsRef</c>; the
/// raw <see cref="Password"/> lives only here and is never logged. Authentication is HTTP Basic per
/// request (not a Redfish session-token POST), which keeps the read-only boundary clean — a login POST
/// would itself be a mutating call (ADR 0009).
/// </summary>
/// <param name="Host">BMC/iLO hostname or IP.</param>
/// <param name="Port">HTTPS port (443 by default).</param>
/// <param name="Username">Redfish account username (least-privilege read-only iLO user).</param>
/// <param name="Password">Redfish account password.</param>
/// <param name="Timeout">Per-request timeout applied via a linked cancellation source.</param>
/// <param name="CertificateThumbprint">
/// Optional SHA-256 certificate fingerprint to pin the TLS peer against (hex, separators/whitespace and
/// case ignored). When set, the self-signed iLO certificate is trusted only if its fingerprint matches;
/// this is the recommended way to secure the transport against an active man-in-the-middle.
/// </param>
/// <param name="AllowUntrustedCertificate">
/// Explicit, per-connection opt-in that accepts an otherwise-untrusted TLS certificate (self-signed,
/// expired or name-mismatched) when no <see cref="CertificateThumbprint"/> pin is configured. Defaults to
/// <c>false</c> so blanket acceptance can never be the silent default (CWE-295).
/// </param>
public sealed record RedfishConnectionSettings(
    string Host, int Port, string Username, string Password, TimeSpan Timeout,
    string? CertificateThumbprint = null, bool AllowUntrustedCertificate = false)
{
    /// <summary>
    /// Overrides the compiler-generated record <c>ToString()</c> — which would otherwise print every
    /// positional member, including <see cref="Password"/> — so an accidental <c>settings.ToString()</c>
    /// (e.g. in a debugger watch or a future log call) can never leak the credential.
    /// </summary>
    public override string ToString() => $"{Host}:{Port} as {Username}";
}

/// <summary>A Redfish protocol-level failure (a non-success HTTP status or an unexpected response).</summary>
public class RedfishException : Exception
{
    /// <summary>Creates the exception with a caller-supplied, secret-free message.</summary>
    public RedfishException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception wrapping an underlying cause.</summary>
    public RedfishException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A Redfish authentication/authorization failure — an HTTP 401 or 403 response.</summary>
public sealed class RedfishAuthenticationException : RedfishException
{
    /// <summary>Creates the exception with a message that must never contain credential material.</summary>
    public RedfishAuthenticationException(string message)
        : base(message)
    {
    }
}
