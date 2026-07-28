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
            // iLO ships a self-signed certificate. Validation is enforced by ValidateServerCertificate: a
            // fully trusted chain, a matching pinned fingerprint, or an explicit per-connection opt-in —
            // never a silent accept-all (CWE-295).
            SslOptions =
            {
                RemoteCertificateValidationCallback = ValidateServerCertificate,
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
                $"Path '{path}' is not on the Redfish read-only allowlist and will not be requested.");
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
                    $"The Redfish endpoint returned HTTP {(int)response.StatusCode} for '{path}'.");
            }

            return await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
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
            _logger.LogInformation(
                "Redfish {Method} {Path} host {Host} status {StatusCode} elapsed {ElapsedMs}ms",
                "GET", path, _settings.Host, status is null ? "-" : ((int)status).ToString(),
                stopwatch.Elapsed.TotalMilliseconds);
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
    /// TLS peer-certificate policy for the HTTPS transport. Accepts a fully trusted chain; otherwise, if a
    /// SHA-256 fingerprint pin is configured, accepts only an exact match (the recommended posture for the
    /// self-signed iLO certificate); otherwise accepts an untrusted certificate only when the caller has
    /// explicitly opted in via <see cref="RedfishConnectionSettings.AllowUntrustedCertificate"/>. All other
    /// cases are rejected, so validation is never silently disabled (CWE-295). Log lines are secret-free.
    /// Ported verbatim from <c>RouterOsApiClient.ValidateServerCertificate</c>.
    /// </summary>
    internal bool ValidateServerCertificate(
        object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors sslPolicyErrors)
    {
        if (sslPolicyErrors == SslPolicyErrors.None)
        {
            return true;
        }

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
