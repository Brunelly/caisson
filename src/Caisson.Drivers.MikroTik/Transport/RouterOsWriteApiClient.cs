using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Caisson.Drivers.MikroTik.Transport;

/// <summary>
/// The write-capable RouterOS binary API client (ADR 0031). Shares its connect/TLS/cert-pin/login
/// machinery with the read-only <see cref="RouterOsApiClient"/> via <see cref="RouterOsApiConnection"/>
/// — the same cert-pin-sole-authority policy (ADR 0019) and wire caps apply here unchanged — and owns a
/// SEPARATE chokepoint, <see cref="ExecuteAsync"/>, that enforces the
/// <see cref="RouterOsWriteCommands.Allowlist"/> before any socket I/O. <see cref="RouterOsReadCommands.Allowlist"/>
/// is never consulted here and is not widened by this type.
/// </summary>
public sealed class RouterOsWriteApiClient : IRouterOsWriteApiClient
{
    private readonly RouterOsConnectionSettings _settings;
    private readonly ILogger _logger;
    private readonly RouterOsApiConnection _connection;

    /// <summary>Creates an unconnected client bound to <paramref name="settings"/>. Call <see cref="ConnectAsync"/> first.</summary>
    public RouterOsWriteApiClient(RouterOsConnectionSettings settings, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        _settings = settings;
        _logger = logger;
        _connection = new RouterOsApiConnection(settings, logger);
    }

    /// <summary>
    /// Test seam: binds a pre-connected, already-authenticated <paramref name="stream"/> so the command
    /// path can be exercised without a socket. Not used in production.
    /// </summary>
    internal RouterOsWriteApiClient(RouterOsConnectionSettings settings, ILogger logger, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        _settings = settings;
        _logger = logger;
        _connection = new RouterOsApiConnection(settings, logger, stream);
    }

    /// <inheritdoc />
    public Task ConnectAsync(CancellationToken cancellationToken) => _connection.ConnectAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<IReadOnlyDictionary<string, string>>> ExecuteAsync(
        string command, IReadOnlyList<string> words, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(words);

        // Write safety boundary (NFR1): reject anything not on the write allowlist BEFORE any socket
        // I/O. A programmer error, not an expected device failure — throws rather than returning a
        // result the driver would translate to a DriverResult.Fail.
        if (!RouterOsWriteCommands.Allowlist.Contains(command))
        {
            throw new InvalidOperationException(
                $"Command '{command}' is not on the RouterOS write allowlist and will not be sent.");
        }

        if (!_connection.IsConnected)
        {
            throw new InvalidOperationException("The RouterOS write client is not connected. Call ConnectAsync first.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(_settings.Timeout);

        var sentenceWords = new List<string>(words.Count + 1) { command };
        sentenceWords.AddRange(words);

        var stopwatch = Stopwatch.StartNew();
        var outcome = "Success";
        try
        {
            await _connection.WriteWordsAsync(sentenceWords, linked.Token).ConfigureAwait(false);
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
            throw new TimeoutException($"RouterOS write command '{command}' timed out after {_settings.Timeout}.");
        }
        catch (Exception)
        {
            outcome = "Fail";
            throw;
        }
        finally
        {
            stopwatch.Stop();
            // One structured line per command — never the username, password, credentials reference or
            // the attribute VALUES (which could echo the port/VLAN, harmless, but we keep parity with the
            // read client's minimal-surface log line rather than widening it).
            _logger.LogInformation(
                "RouterOS write command {Command} host {Host} elapsed {ElapsedMs}ms outcome {Outcome}",
                command, _settings.Host, stopwatch.Elapsed.TotalMilliseconds, outcome);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
