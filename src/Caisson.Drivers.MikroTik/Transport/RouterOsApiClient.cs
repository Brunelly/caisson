using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Caisson.Drivers.MikroTik.Transport;

/// <summary>
/// A minimal, read-only RouterOS binary API client built on BCL sockets only — no third-party MikroTik
/// dependency (ADR 0008). Speaks the wire protocol via <see cref="RouterOsSentence"/> over TCP 8728
/// (plain) or 8729 (<see cref="SslStream"/>), authenticates with the post-6.43 plaintext scheme and
/// falls back to the pre-6.43 MD5 challenge-response, and exposes a single command chokepoint,
/// <see cref="SendCommandAsync"/>, that enforces the <see cref="RouterOsReadCommands.Allowlist"/> and
/// emits one structured, secret-free log line per command.
/// </summary>
public sealed class RouterOsApiClient : IRouterOsApiClient
{
    private readonly RouterOsConnectionSettings _settings;
    private readonly ILogger _logger;

    private TcpClient? _tcp;
    private Stream? _stream;

    /// <summary>Creates an unconnected client bound to <paramref name="settings"/>. Call <see cref="ConnectAsync"/> first.</summary>
    public RouterOsApiClient(RouterOsConnectionSettings settings, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        _settings = settings;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        // The connect + login handshake shares the per-command timeout budget.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(_settings.Timeout);

        try
        {
            _tcp = new TcpClient();
            await _tcp.ConnectAsync(_settings.Host, _settings.Port, linked.Token).ConfigureAwait(false);

            Stream stream = _tcp.GetStream();
            if (_settings.UseTls)
            {
                // CHR ships a self-signed certificate; TLS here protects credentials in transit.
                // Certificate pinning is a deliberate future enhancement (see docs/routeros-discovery.md).
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false, (_, _, _, _) => true);
                await ssl.AuthenticateAsClientAsync(
                    new SslClientAuthenticationOptions { TargetHost = _settings.Host }, linked.Token).ConfigureAwait(false);
                stream = ssl;
            }

            _stream = stream;
            await LoginAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Connecting to RouterOS at {_settings.Host}:{_settings.Port} timed out.");
        }
    }

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

        if (_stream is null)
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
            await RouterOsSentence.WriteAsync(_stream, new[] { command }, linked.Token).ConfigureAwait(false);
            return await ReadUntilDoneAsync(linked.Token).ConfigureAwait(false);
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
    public async ValueTask DisposeAsync()
    {
        if (_stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
            _stream = null;
        }

        _tcp?.Dispose();
        _tcp = null;
    }

    private async Task LoginAsync(CancellationToken cancellationToken)
    {
        // Post-6.43 plaintext login. A v6/pre-6.43 server ignores the password and answers with a
        // '=ret=' challenge, which we satisfy with the legacy MD5 response below (best-effort v6/v7).
        await RouterOsSentence.WriteAsync(
            _stream!,
            new[] { "/login", "=name=" + _settings.Username, "=password=" + _settings.Password },
            cancellationToken).ConfigureAwait(false);

        var reply = await ReadSentenceAsync(cancellationToken).ConfigureAwait(false);
        EnsureLoginAccepted(reply.Type);

        if (reply.Attributes.TryGetValue("ret", out var challenge))
        {
            var response = ComputeChallengeResponse(_settings.Password, challenge);
            await RouterOsSentence.WriteAsync(
                _stream!,
                new[] { "/login", "=name=" + _settings.Username, "=response=" + response },
                cancellationToken).ConfigureAwait(false);

            var fallbackReply = await ReadSentenceAsync(cancellationToken).ConfigureAwait(false);
            EnsureLoginAccepted(fallbackReply.Type);
        }
    }

    private static void EnsureLoginAccepted(string replyType)
    {
        switch (replyType)
        {
            case "!done":
                return;
            case "!trap":
            case "!fatal":
                throw new RouterOsAuthenticationException("RouterOS rejected the supplied credentials.");
            default:
                throw new RouterOsApiException($"Unexpected RouterOS reply '{replyType}' during login.");
        }
    }

    private async Task<IReadOnlyList<IReadOnlyDictionary<string, string>>> ReadUntilDoneAsync(
        CancellationToken cancellationToken)
    {
        var rows = new List<IReadOnlyDictionary<string, string>>();
        while (true)
        {
            var (type, attributes) = await ReadSentenceAsync(cancellationToken).ConfigureAwait(false);
            switch (type)
            {
                case "!re":
                    rows.Add(attributes);
                    break;
                case "!done":
                    return rows;
                case "!trap":
                    throw new RouterOsApiException(TrapMessage(attributes));
                case "!fatal":
                    throw new RouterOsApiException("RouterOS returned a fatal error: " + TrapMessage(attributes));
                default:
                    throw new RouterOsApiException($"Unexpected RouterOS reply word '{type}'.");
            }
        }
    }

    private async Task<(string Type, IReadOnlyDictionary<string, string> Attributes)> ReadSentenceAsync(
        CancellationToken cancellationToken)
    {
        var words = await RouterOsSentence.ReadAsync(_stream!, cancellationToken).ConfigureAwait(false);
        if (words.Count == 0)
        {
            throw new RouterOsApiException("Received an empty sentence from RouterOS.");
        }

        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < words.Count; i++)
        {
            AddAttribute(attributes, words[i]);
        }

        return (words[0], attributes);
    }

    private static void AddAttribute(Dictionary<string, string> attributes, string word)
    {
        // Attribute words look like "=key=value" (value may be empty). Non-attribute words such as a
        // ".tag=..." reply tag are ignored for evidence purposes.
        if (word.Length == 0 || word[0] != '=')
        {
            return;
        }

        var separator = word.IndexOf('=', 1);
        if (separator < 0)
        {
            attributes[word[1..]] = string.Empty;
            return;
        }

        attributes[word[1..separator]] = word[(separator + 1)..];
    }

    private static string TrapMessage(IReadOnlyDictionary<string, string> attributes)
        => attributes.TryGetValue("message", out var message) ? message : "unspecified RouterOS error";

    private static string ComputeChallengeResponse(string password, string challengeHex)
    {
        var challenge = Convert.FromHexString(challengeHex);
        var passwordBytes = Encoding.ASCII.GetBytes(password);

        var input = new byte[1 + passwordBytes.Length + challenge.Length];
        input[0] = 0x00;
        passwordBytes.CopyTo(input, 1);
        challenge.CopyTo(input, 1 + passwordBytes.Length);

        var hash = MD5.HashData(input);
        return "00" + Convert.ToHexString(hash).ToLowerInvariant();
    }
}
