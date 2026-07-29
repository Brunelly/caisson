using System.Diagnostics;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace Caisson.Drivers.MikroTik.Transport;

/// <summary>
/// A minimal, read-only RouterOS binary API client built on BCL sockets only — no third-party MikroTik
/// dependency (ADR 0008). The connect/TLS/cert-pin/login machinery is shared with
/// <see cref="RouterOsWriteApiClient"/> via <see cref="RouterOsApiConnection"/> (ADR 0031); this type
/// owns only the read-only command chokepoint, <see cref="SendCommandAsync"/>, which enforces the
/// <see cref="RouterOsReadCommands.Allowlist"/> and emits one structured, secret-free log line per
/// command.
/// </summary>
public sealed class RouterOsApiClient : IRouterOsApiClient
{
    /// <summary>
    /// Upper bound on the number of <c>!re</c> rows accepted for a single reply, well above any realistic
    /// bridge-host/port/LLDP table. Bounds the ~20x wire-to-heap amplification a compromised device could
    /// otherwise inflict by returning an unbounded row count.
    /// </summary>
    internal const int MaxRowsPerReply = RouterOsApiConnection.MaxRowsPerReply;

    private readonly RouterOsConnectionSettings _settings;
    private readonly ILogger _logger;
    private readonly RouterOsApiConnection _connection;

    /// <summary>Creates an unconnected client bound to <paramref name="settings"/>. Call <see cref="ConnectAsync"/> first.</summary>
    public RouterOsApiClient(RouterOsConnectionSettings settings, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        _settings = settings;
        _logger = logger;
        _connection = new RouterOsApiConnection(settings, logger);
    }

    /// <summary>
    /// Test seam: binds a pre-connected, already-authenticated <paramref name="stream"/> so the
    /// command path can be exercised without a socket. Not used in production.
    /// </summary>
    internal RouterOsApiClient(RouterOsConnectionSettings settings, ILogger logger, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        _settings = settings;
        _logger = logger;
        _connection = new RouterOsApiConnection(settings, logger, stream);
    }

    /// <inheritdoc />
    public Task ConnectAsync(CancellationToken cancellationToken) => _connection.ConnectAsync(cancellationToken);

    /// <summary>
    /// TLS peer-certificate policy for the 8729 transport — delegates to the shared
    /// <see cref="RouterOsApiConnection.ValidateServerCertificate"/> so the cert-pin sole-authority rule
    /// (ADR 0019) has a single implementation shared with the write client. Exposed here (rather than
    /// only on the connection) so existing unit tests can exercise it directly against this client.
    /// </summary>
    internal bool ValidateServerCertificate(
        object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors sslPolicyErrors)
        => _connection.ValidateServerCertificate(sender, certificate, chain, sslPolicyErrors);

    /// <inheritdoc />
    public async Task<IReadOnlyList<IReadOnlyDictionary<string, string>>> SendCommandAsync(
        string command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Read-only safety boundary (NFR1/AC1): reject anything not on the allowlist BEFORE any socket
        // I/O. This is a programmer error, not an expected device failure, so it throws rather than
        // returning a result the driver would translate to DriverResult.Fail.
        if (!RouterOsReadCommands.Allowlist.Contains(command))
        {
            throw new InvalidOperationException(
                $"Command '{command}' is not on the RouterOS read-only allowlist and will not be sent.");
        }

        if (!_connection.IsConnected)
        {
            throw new InvalidOperationException("The RouterOS client is not connected. Call ConnectAsync first.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(_settings.Timeout);

        var stopwatch = Stopwatch.StartNew();
        var outcome = "Success";
        try
        {
            await _connection.WriteWordsAsync(new[] { command }, linked.Token).ConfigureAwait(false);
            return await _connection.ReadUntilDoneAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller-initiated cancellation is control flow, not a device failure — propagate.
            outcome = "Cancelled";
            throw;
        }
        catch (OperationCanceledException)
        {
            outcome = "Timeout";
            throw new TimeoutException($"RouterOS command '{command}' timed out after {_settings.Timeout}.");
        }
        catch (Exception)
        {
            outcome = "Fail";
            throw;
        }
        finally
        {
            stopwatch.Stop();
            // One structured line per command — never the username, password or credentials reference.
            _logger.LogInformation(
                "RouterOS command {Command} host {Host} elapsed {ElapsedMs}ms outcome {Outcome}",
                command, _settings.Host, stopwatch.Elapsed.TotalMilliseconds, outcome);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
