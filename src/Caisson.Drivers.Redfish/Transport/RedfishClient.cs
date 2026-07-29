using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Caisson.Drivers.Redfish.Transport;

/// <summary>
/// A minimal, read-only Redfish client built on the BCL <see cref="HttpClient"/>/<see cref="SocketsHttpHandler"/>
/// only — no third-party Redfish/HTTP dependency (ADR 0009). It exposes a single command chokepoint,
/// <see cref="GetAsync"/>, that enforces the <see cref="RedfishReadPaths"/> read-only allowlist <b>before
/// any I/O</b>, authenticates each request with HTTP Basic (never a session-token POST, which would blur
/// the read-only boundary), validates the TLS peer certificate through <see cref="ValidateServerCertificate"/>
/// (never a silent accept-all, CWE-295), applies a per-request timeout, and emits exactly one structured,
/// secret-free log line per GET.
/// </summary>
public sealed class RedfishClient : IRedfishClient
{
    /// <summary>
    /// Upper bound on a single response body, mirroring <see cref="Caisson.Drivers.MikroTik.Transport.RouterOsSentence.MaxWordLength"/>.
    /// A compromised or man-in-the-middle'd BMC could otherwise stream an unbounded body — chunked
    /// responses omit <c>Content-Length</c>, so this is enforced by counting bytes as they arrive, not by
    /// trusting the header alone.
    /// </summary>
    internal const int MaxResponseBytes = 8 * 1024 * 1024;

    private readonly RedfishConnectionSettings _settings;
    private readonly ILogger _logger;
    private readonly HttpClient _http;
    private readonly HttpMessageHandler? _ownedHandler;

    /// <summary>Creates a client bound to <paramref name="settings"/> over a TLS-validating handler.</summary>
    public RedfishClient(RedfishConnectionSettings settings, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        _settings = settings;
        _logger = logger;

        var handler = new SocketsHttpHandler
        {
            // Redfish navigation is done explicitly by following @odata.id links, each re-validated by the
            // read-only allowlist before I/O, so redirects are never needed. Disabling them stops a malicious
            // or compromised BMC from using a 3xx to steer a read-only GET to an off-allowlist path or an
            // internal host (SSRF) after the pre-I/O boundary check has already passed.
            AllowAutoRedirect = false,

            // iLO ships a self-signed certificate. Validation is enforced by ValidateServerCertificate: a
            // fully trusted chain, a matching pinned fingerprint, or an explicit per-connection opt-in —
            // never a silent accept-all (CWE-295).
            SslOptions =
            {
                RemoteCertificateValidationCallback = ValidateServerCertificate,

                // Revocation checking only makes sense against a chain we are actually trusting via the
                // platform store — a pinned self-signed certificate has no CA-issued revocation info to
                // check, so this is scoped to the non-pinned path (see ValidateServerCertificate).
                CertificateRevocationCheckMode = string.IsNullOrWhiteSpace(settings.CertificateThumbprint)
                    ? X509RevocationMode.Online
                    : X509RevocationMode.NoCheck,
            },
        };

        _ownedHandler = handler;
        _http = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri($"https://{_settings.Host}:{_settings.Port}"),
        };
    }

    /// <summary>
    /// Test seam: binds a caller-supplied <paramref name="httpClient"/> (e.g. one over a fake handler) so
    /// the request path can be exercised without a real socket. Not used in production.
    /// </summary>
    internal RedfishClient(RedfishConnectionSettings settings, ILogger logger, HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(httpClient);
        _settings = settings;
        _logger = logger;
        _http = httpClient;
        _ownedHandler = null;
    }

    /// <inheritdoc />
    public async Task<string> GetAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);

        // Read-only safety boundary (NFR1/AC1): reject anything that is not an allowlisted read-only GET
        // BEFORE any network I/O. This is a programmer error, not an expected device failure, so it throws
        // rather than returning a body the driver would translate to DriverResult.Fail.
        if (!RedfishReadPaths.IsReadOnlyGet("GET", path))
        {
            throw new InvalidOperationException(
                $"Path '{RedfishReadPaths.SanitizeForLog(path)}' is not on the Redfish read-only allowlist and will not be requested.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(_settings.Timeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", BasicCredential());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var stopwatch = Stopwatch.StartNew();
        HttpStatusCode? status = null;
        try
        {
            using var response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            status = response.StatusCode;

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                // Secret-free: name only the status, never the credential that was rejected.
                throw new RedfishAuthenticationException(
                    $"The Redfish endpoint rejected the credentials (HTTP {(int)response.StatusCode}).");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new RedfishException(
                    $"The Redfish endpoint returned HTTP {(int)response.StatusCode} for '{RedfishReadPaths.SanitizeForLog(path)}'.");
            }

            // Reject early on a Content-Length that already exceeds the cap, but never trust that header
            // alone — a chunked response omits it — so the bounded read below is the real enforcement.
            var declaredLength = response.Content.Headers.ContentLength;
            if (declaredLength is > MaxResponseBytes)
            {
                throw new RedfishException(
                    $"The Redfish response for '{RedfishReadPaths.SanitizeForLog(path)}' declared a Content-Length of " +
                    $"{declaredLength} bytes, exceeding the {MaxResponseBytes}-byte cap.");
            }

            return await ReadBoundedAsync(response, path, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller-initiated cancellation is control flow, not a device failure — propagate.
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"The Redfish GET '{path}' timed out after {_settings.Timeout}.");
        }
        finally
        {
            stopwatch.Stop();
            // One structured line per GET — never the Authorization header, username, password or token.
            // The path is a device-supplied @odata.id and is logged only in its sanitised (CR/LF-stripped,
            // truncated) form so a crafted resource id can never inject a fake log line (log injection).
            _logger.LogInformation(
                "Redfish {Method} {Path} host {Host} status {StatusCode} elapsed {ElapsedMs}ms",
                "GET", RedfishReadPaths.SanitizeForLog(path), _settings.Host,
                status is null ? "-" : ((int)status).ToString(), stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _http.Dispose();
        _ownedHandler?.Dispose();
    }

    private string BasicCredential()
        => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_settings.Username}:{_settings.Password}"));

    /// <summary>
    /// Reads the response body up to <see cref="MaxResponseBytes"/>, throwing <see cref="RedfishException"/>
    /// the moment more bytes arrive than the cap allows. Reads the stream directly rather than
    /// <c>ReadAsStringAsync</c> (which buffers unboundedly, up to 2 GB, regardless of any cap) — a chunked
    /// response never sends <c>Content-Length</c>, so this is the only reliable enforcement point.
    /// </summary>
    private static async Task<string> ReadBoundedAsync(
        HttpResponseMessage response, string path, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
            {
                throw new RedfishException(
                    $"The Redfish response for '{RedfishReadPaths.SanitizeForLog(path)}' exceeded the " +
                    $"{MaxResponseBytes}-byte cap and was rejected.");
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>
    /// TLS peer-certificate policy for the HTTPS transport. When a SHA-256 fingerprint pin is configured it
    /// is the SOLE authority — the pin comparison result is returned regardless of <paramref name="sslPolicyErrors"/>,
    /// so a certificate that happens to chain to a trusted root can never bypass a configured pin (the pin
    /// exists precisely to defend the active-MITM case, where the platform validator would otherwise say
    /// <see cref="SslPolicyErrors.None"/>). Only when no pin is configured does a fully trusted chain short-circuit
    /// to accepted, or does an untrusted certificate fall through to the explicit
    /// <see cref="RedfishConnectionSettings.AllowUntrustedCertificate"/> opt-in. All other cases are rejected, so
    /// validation is never silently disabled (CWE-295). Log lines are secret-free. Ported verbatim from
    /// <c>RouterOsApiClient.ValidateServerCertificate</c>.
    /// </summary>
    internal bool ValidateServerCertificate(
        object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors sslPolicyErrors)
    {
        if (certificate is not null && !string.IsNullOrWhiteSpace(_settings.CertificateThumbprint))
        {
            var presented = Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
            if (string.Equals(presented, NormalizeFingerprint(_settings.CertificateThumbprint), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            _logger.LogWarning(
                "Redfish TLS certificate for {Host} did not match the configured SHA-256 fingerprint pin; rejecting the connection.",
                _settings.Host);
            return false;
        }

        if (sslPolicyErrors == SslPolicyErrors.None)
        {
            return true;
        }

        if (_settings.AllowUntrustedCertificate)
        {
            _logger.LogWarning(
                "Redfish TLS certificate for {Host} is untrusted ({Errors}) but was accepted because certificate " +
                "validation is explicitly disabled for this connection. Configure a certificate fingerprint pin to remove this risk.",
                _settings.Host, sslPolicyErrors);
            return true;
        }

        _logger.LogWarning(
            "Rejecting Redfish TLS certificate for {Host}: {Errors}. Configure a SHA-256 certificate fingerprint pin " +
            "or explicitly allow an untrusted certificate for this connection.",
            _settings.Host, sslPolicyErrors);
        return false;
    }

    /// <summary>Strips separators/whitespace from a hex fingerprint so <c>AA:BB</c>, <c>aa bb</c> and <c>AABB</c> compare equal.</summary>
    private static string NormalizeFingerprint(string fingerprint)
    {
        var builder = new StringBuilder(fingerprint.Length);
        foreach (var c in fingerprint)
        {
            if (Uri.IsHexDigit(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
