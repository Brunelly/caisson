using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Caisson.Drivers.Simulators;

/// <summary>
/// An in-process RouterOS API server for CI (AC5): a loopback <see cref="TcpListener"/> that speaks the
/// binary RouterOS API wire protocol and login handshake and replays a committed fixture
/// <see cref="RouterOsProfile"/> keyed by command path. The wire framing here is a deliberately
/// independent implementation of the same protocol the driver's client uses, so the two interoperating
/// proves the protocol, not a single shared codec. A licensed CHR KVM image is unfit for deterministic,
/// hardware-free runners, so this simulator is the CI path; <see cref="RouterOsChrFixture"/> prefers a
/// real CHR when one is provided.
/// </summary>
public sealed class RouterOsApiSimulator : IAsyncDisposable
{
    // A fixed 16-byte challenge used for the pre-6.43 legacy login handshake.
    private const string LegacyChallengeHex = "0123456789abcdef0123456789abcdef";
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static readonly Regex RollbackScriptPattern = new(
        "interface=\"(?<port>[^\"]+)\"\\]\\s*pvid=(?<pvid>\\d+)", RegexOptions.Compiled);

    private readonly RouterOsProfile _profile;
    private readonly string _expectedUsername;
    private readonly string _expectedPassword;
    private readonly X509Certificate2? _serverCertificate;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, PendingRollback> _pendingRollbacks = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _receivedCommands = new();
    private Task? _acceptLoop;
    private DateTimeOffset _now;

    /// <summary>
    /// Creates a simulator. When <paramref name="serverCertificate"/> is supplied the transport speaks
    /// TLS (a real server-side <see cref="SslStream"/> handshake, as CHR does on 8729) before the
    /// RouterOS login; otherwise it is plaintext, as on 8728. When <paramref name="profile"/> sets
    /// <see cref="RouterOsProfile.SwitchState"/>, the simulator serves the write-driver's commands from
    /// that mutable state (AC5); <paramref name="timeProvider"/> (defaulting to <see cref="TimeProvider.System"/>)
    /// seeds the simulator's virtual clock, which tests advance deterministically via
    /// <see cref="AdvanceTime"/>/<see cref="FireDueRollbacks"/> rather than sleeping for a real confirm window.
    /// </summary>
    public RouterOsApiSimulator(
        RouterOsProfile profile, string username, string password, X509Certificate2? serverCertificate = null,
        TimeProvider? timeProvider = null)
    {
        _profile = profile;
        _expectedUsername = username;
        _expectedPassword = password;
        _serverCertificate = serverCertificate;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _now = _timeProvider.GetUtcNow();
    }

    /// <summary>The loopback host the simulator listens on.</summary>
    public string Host => IPAddress.Loopback.ToString();

    /// <summary>The ephemeral port assigned once <see cref="Start"/> has run.</summary>
    public int Port { get; private set; }

    /// <summary>Binds the listener and starts accepting connections in the background.</summary>
    public void Start()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    /// <summary>Simulator-only observability hook (AC5): every command path received, in receipt order. Not part of the wire protocol.</summary>
    public IReadOnlyList<string> ReceivedCommands => _receivedCommands.ToArray();

    /// <summary>Simulator-only observability hook (AC5): the current PVID of a stateful-mode port, or <c>null</c> if not seeded/unknown.</summary>
    public int? GetPortAccessVlan(string port) => _profile.SwitchState?.GetPvid(port);

    /// <summary>
    /// Simulator-only test seam (Task #115): directly mutates a stateful-mode port's PVID, bypassing the
    /// wire protocol — lets a scripted <c>ISwitchMutatingDriver</c> test double (standing in for the real
    /// one-shot <c>RouterOsSwitchMutatingDriver.SetAccessVlanAsync</c> on the ONE rack proving the
    /// orchestration-level withheld-confirmation/auto-rollback outcome) apply-then-revert against real
    /// simulator state instead of a hardcoded fake "before" value. No-op if the port was never seeded.
    /// </summary>
    public void SetPortAccessVlanForTest(string port, int pvid) => _profile.SwitchState?.SetPvid(port, pvid);

    /// <summary>Simulator-only observability hook (AC5): whether an armed confirmed-commit rollback is still pending for <paramref name="port"/>.</summary>
    public bool HasPendingRollback(string port)
    {
        lock (_pendingRollbacks)
        {
            return _pendingRollbacks.Values.Any(p => string.Equals(p.PortName, port, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Moves the simulator's virtual clock forward by <paramref name="delta"/> WITHOUT firing any due
    /// rollbacks — call <see cref="FireDueRollbacks"/> afterwards to apply them. Lets CI prove the
    /// confirmed-commit window elapsing deterministically, without a real 30-second sleep.
    /// </summary>
    public void AdvanceTime(TimeSpan delta)
    {
        lock (_pendingRollbacks)
        {
            _now = _now.Add(delta);
        }
    }

    /// <summary>
    /// Reverts every armed rollback whose window has elapsed (per the virtual clock advanced via
    /// <see cref="AdvanceTime"/>) — the simulator-side equivalent of a RouterOS
    /// <c>/system/scheduler</c> job firing its <c>on-event</c> script.
    /// </summary>
    public void FireDueRollbacks()
    {
        var state = _profile.SwitchState;
        if (state is null)
        {
            return;
        }

        lock (_pendingRollbacks)
        {
            foreach (var name in _pendingRollbacks.Where(kvp => kvp.Value.DueAt <= _now).Select(kvp => kvp.Key).ToArray())
            {
                var pending = _pendingRollbacks[name];
                state.SetPvid(pending.PortName, pending.RevertPvid);
                _pendingRollbacks.Remove(name);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop;
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }

        _cts.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            _ = Task.Run(() => HandleClientAsync(client, cancellationToken), cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        await using (var networkStream = client.GetStream())
        {
            Stream stream = networkStream;
            SslStream? ssl = null;
            try
            {
                if (_serverCertificate is not null)
                {
                    // Real server-side TLS handshake — the driver's SslStream/ValidateServerCertificate
                    // path runs against this, proving the 8729 transport end-to-end (not just the callback).
                    ssl = new SslStream(networkStream, leaveInnerStreamOpen: false);
                    await ssl.AuthenticateAsServerAsync(
                        new SslServerAuthenticationOptions { ServerCertificate = _serverCertificate },
                        cancellationToken).ConfigureAwait(false);
                    stream = ssl;
                }

                if (!await HandleLoginAsync(stream, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                while (!cancellationToken.IsCancellationRequested)
                {
                    var words = await ReadSentenceAsync(stream, cancellationToken).ConfigureAwait(false);
                    if (words.Count == 0)
                    {
                        return;
                    }

                    await HandleCommandAsync(stream, words, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                // Shutting down.
            }
            catch (EndOfStreamException)
            {
                // Client disconnected — normal.
            }
            catch (IOException)
            {
                // Client disconnected mid-write — normal.
            }
            catch (System.Security.Authentication.AuthenticationException)
            {
                // The client aborted the TLS handshake (e.g. it rejected our certificate) — expected.
            }
            finally
            {
                if (ssl is not null)
                {
                    await ssl.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private async Task<bool> HandleLoginAsync(Stream stream, CancellationToken cancellationToken)
    {
        var first = ParseAttributes(await ReadSentenceAsync(stream, cancellationToken).ConfigureAwait(false));

        if (_profile.LegacyLogin)
        {
            // Pre-6.43: challenge the client, then verify its MD5 response.
            await WriteSentenceAsync(stream, new[] { "!done", "=ret=" + LegacyChallengeHex }, cancellationToken).ConfigureAwait(false);

            var second = ParseAttributes(await ReadSentenceAsync(stream, cancellationToken).ConfigureAwait(false));
            var expected = ComputeChallengeResponse(_expectedPassword, LegacyChallengeHex);
            var ok = second.GetValueOrDefault("name") == _expectedUsername
                && second.GetValueOrDefault("response") == expected;
            return await CompleteLoginAsync(stream, ok, cancellationToken).ConfigureAwait(false);
        }

        var accepted = first.GetValueOrDefault("name") == _expectedUsername
            && first.GetValueOrDefault("password") == _expectedPassword;
        return await CompleteLoginAsync(stream, accepted, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> CompleteLoginAsync(Stream stream, bool accepted, CancellationToken cancellationToken)
    {
        await WriteSentenceAsync(
            stream,
            accepted ? new[] { "!done" } : new[] { "!trap", "=message=invalid user name or password" },
            cancellationToken).ConfigureAwait(false);
        return accepted;
    }

    private async Task HandleCommandAsync(Stream stream, IReadOnlyList<string> words, CancellationToken cancellationToken)
    {
        var command = words[0];
        _receivedCommands.Enqueue(command);

        if (_profile.SwitchState is { } state
            && TryHandleStatefulCommand(state, command, words, out var statefulRows, out var statefulTrap))
        {
            await WriteReplyAsync(stream, statefulRows, statefulTrap, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!_profile.Commands.TryGetValue(command, out var reply))
        {
            // Unknown command → empty result set.
            await WriteSentenceAsync(stream, new[] { "!done" }, cancellationToken).ConfigureAwait(false);
            return;
        }

        await WriteReplyAsync(stream, reply.Rows, reply.Trap, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteReplyAsync(
        Stream stream, List<Dictionary<string, string>>? rows, string? trap, CancellationToken cancellationToken)
    {
        if (trap is not null)
        {
            await WriteSentenceAsync(stream, new[] { "!trap", "=message=" + trap }, cancellationToken).ConfigureAwait(false);
            await WriteSentenceAsync(stream, new[] { "!done" }, cancellationToken).ConfigureAwait(false);
            return;
        }

        foreach (var row in rows ?? new List<Dictionary<string, string>>())
        {
            var words = new List<string> { "!re" };
            words.AddRange(row.Select(pair => $"={pair.Key}={pair.Value}"));
            await WriteSentenceAsync(stream, words, cancellationToken).ConfigureAwait(false);
        }

        await WriteSentenceAsync(stream, new[] { "!done" }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Serves the write driver's bounded command set from mutable <paramref name="state"/> instead of
    /// the stateless fixture replay (AC5). This deliberately recognises only the driver's fixed on-event
    /// rollback template structurally (via <see cref="RollbackScriptPattern"/>) — it is NOT a general
    /// RouterOS script interpreter, an intentional bounded simplification (see ADR 0031).
    /// </summary>
    private bool TryHandleStatefulCommand(
        SimulatorSwitchState state, string command, IReadOnlyList<string> words,
        out List<Dictionary<string, string>>? rows, out string? trap)
    {
        rows = null;
        trap = null;

        switch (command)
        {
            case "/interface/print":
            case "/interface/ethernet/print":
                {
                    // Not on the write allowlist, but the simulator serves them too when stateful so the
                    // READ driver can observe the same seeded ports for the read/write parity check (AC5)
                    // — the allowlist boundary is enforced client-side, not by the simulator.
                    rows = new List<Dictionary<string, string>>();
                    foreach (var port in state.Ports.Keys)
                    {
                        rows.Add(new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["name"] = port,
                            ["running"] = "true",
                            ["disabled"] = "false",
                        });
                    }

                    return true;
                }

            case "/interface/bridge/port/print":
                {
                    var filters = ParseQueryFilters(words);
                    rows = new List<Dictionary<string, string>>();
                    foreach (var (port, pvid) in state.Ports)
                    {
                        if (filters.TryGetValue("interface", out var wanted)
                            && !string.Equals(wanted, port, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        rows.Add(new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            [".id"] = "*" + port,
                            ["interface"] = port,
                            ["pvid"] = pvid.ToString(CultureInfo.InvariantCulture),
                        });
                    }

                    return true;
                }

            case "/interface/bridge/vlan/print":
                {
                    rows = new List<Dictionary<string, string>>();
                    foreach (var (vlanId, membership) in state.Vlans)
                    {
                        rows.Add(new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["vlan-ids"] = vlanId.ToString(CultureInfo.InvariantCulture),
                            ["tagged"] = string.Join(",", membership.Tagged),
                            ["untagged"] = string.Join(",", membership.Untagged),
                        });
                    }

                    return true;
                }

            case "/interface/bridge/port/set":
                {
                    var attributes = ParseAttributes(words);
                    rows = new List<Dictionary<string, string>>();
                    var portId = attributes.GetValueOrDefault(".id");
                    var port = portId is { Length: > 1 } && portId[0] == '*' ? portId[1..] : portId;
                    if (port is not null && attributes.TryGetValue("pvid", out var pvidText)
                        && int.TryParse(pvidText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pvid))
                    {
                        state.SetPvid(port, pvid);
                    }

                    return true;
                }

            case "/system/scheduler/add":
                {
                    var attributes = ParseAttributes(words);
                    rows = new List<Dictionary<string, string>>();
                    var name = attributes.GetValueOrDefault("name");
                    var onEvent = attributes.GetValueOrDefault("on-event") ?? string.Empty;
                    var startTime = attributes.GetValueOrDefault("start-time") ?? string.Empty;

                    if (name is not null
                        && TryParseRelativeSeconds(startTime, out var seconds)
                        && TryParseRollbackScript(onEvent, out var revertPort, out var revertPvid))
                    {
                        lock (_pendingRollbacks)
                        {
                            _pendingRollbacks[name] = new PendingRollback(revertPort, revertPvid, _now.AddSeconds(seconds));
                        }
                    }

                    return true;
                }

            case "/system/scheduler/remove":
                {
                    var attributes = ParseAttributes(words);
                    rows = new List<Dictionary<string, string>>();
                    var numbers = attributes.GetValueOrDefault("numbers");
                    if (numbers is not null)
                    {
                        lock (_pendingRollbacks)
                        {
                            _pendingRollbacks.Remove(numbers);
                        }
                    }

                    return true;
                }

            case "/system/scheduler/print":
                {
                    rows = new List<Dictionary<string, string>>();
                    lock (_pendingRollbacks)
                    {
                        foreach (var name in _pendingRollbacks.Keys)
                        {
                            rows.Add(new Dictionary<string, string>(StringComparer.Ordinal) { ["name"] = name });
                        }
                    }

                    return true;
                }

            default:
                return false;
        }
    }

    private static Dictionary<string, string> ParseQueryFilters(IReadOnlyList<string> words)
    {
        var filters = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < words.Count; i++)
        {
            var word = words[i];
            if (word.Length == 0 || word[0] != '?')
            {
                continue;
            }

            var separator = word.IndexOf('=', 1);
            if (separator >= 0)
            {
                filters[word[1..separator]] = word[(separator + 1)..];
            }
        }

        return filters;
    }

    private static bool TryParseRelativeSeconds(string startTime, out int seconds)
    {
        seconds = 0;
        if (startTime.Length < 3 || startTime[0] != '+' || startTime[^1] != 's')
        {
            return false;
        }

        return int.TryParse(
            startTime.AsSpan(1, startTime.Length - 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds);
    }

    private static bool TryParseRollbackScript(string script, out string port, out int pvid)
    {
        port = string.Empty;
        pvid = 0;

        var match = RollbackScriptPattern.Match(script);
        if (!match.Success)
        {
            return false;
        }

        port = match.Groups["port"].Value;
        return int.TryParse(match.Groups["pvid"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out pvid);
    }

    private sealed record PendingRollback(string PortName, int RevertPvid, DateTimeOffset DueAt);

    private static Dictionary<string, string> ParseAttributes(IReadOnlyList<string> words)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < words.Count; i++)
        {
            var word = words[i];
            if (word.Length == 0 || word[0] != '=')
            {
                continue;
            }

            var separator = word.IndexOf('=', 1);
            if (separator < 0)
            {
                attributes[word[1..]] = string.Empty;
            }
            else
            {
                attributes[word[1..separator]] = word[(separator + 1)..];
            }
        }

        return attributes;
    }

    private static string ComputeChallengeResponse(string password, string challengeHex)
    {
        var challenge = Convert.FromHexString(challengeHex);
        var passwordBytes = Encoding.ASCII.GetBytes(password);
        var input = new byte[1 + passwordBytes.Length + challenge.Length];
        input[0] = 0x00;
        passwordBytes.CopyTo(input, 1);
        challenge.CopyTo(input, 1 + passwordBytes.Length);
        return "00" + Convert.ToHexString(MD5.HashData(input)).ToLowerInvariant();
    }

    // --- Independent RouterOS API framing (encoder/decoder) ---

    private static async Task WriteSentenceAsync(Stream stream, IReadOnlyList<string> words, CancellationToken cancellationToken)
    {
        var buffer = new List<byte>(64);
        foreach (var word in words)
        {
            var bytes = Utf8.GetBytes(word);
            WriteLength(buffer, bytes.Length);
            buffer.AddRange(bytes);
        }

        WriteLength(buffer, 0);
        await stream.WriteAsync(buffer.ToArray(), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void WriteLength(List<byte> buffer, int length)
    {
        if (length < 0x80)
        {
            buffer.Add((byte)length);
        }
        else if (length < 0x4000)
        {
            buffer.Add((byte)((length >> 8) | 0x80));
            buffer.Add((byte)length);
        }
        else if (length < 0x200000)
        {
            buffer.Add((byte)((length >> 16) | 0xC0));
            buffer.Add((byte)(length >> 8));
            buffer.Add((byte)length);
        }
        else if (length < 0x10000000)
        {
            buffer.Add((byte)((length >> 24) | 0xE0));
            buffer.Add((byte)(length >> 16));
            buffer.Add((byte)(length >> 8));
            buffer.Add((byte)length);
        }
        else
        {
            buffer.Add(0xF0);
            buffer.Add((byte)(length >> 24));
            buffer.Add((byte)(length >> 16));
            buffer.Add((byte)(length >> 8));
            buffer.Add((byte)length);
        }
    }

    private static async Task<IReadOnlyList<string>> ReadSentenceAsync(Stream stream, CancellationToken cancellationToken)
    {
        var words = new List<string>();
        while (true)
        {
            var length = await ReadLengthAsync(stream, cancellationToken).ConfigureAwait(false);
            if (length == 0)
            {
                return words;
            }

            var bytes = await ReadExactAsync(stream, length, cancellationToken).ConfigureAwait(false);
            words.Add(Utf8.GetString(bytes));
        }
    }

    private static async Task<int> ReadLengthAsync(Stream stream, CancellationToken cancellationToken)
    {
        int first = (await ReadExactAsync(stream, 1, cancellationToken).ConfigureAwait(false))[0];
        if ((first & 0x80) == 0x00)
        {
            return first;
        }

        if ((first & 0xC0) == 0x80)
        {
            var rest = await ReadExactAsync(stream, 1, cancellationToken).ConfigureAwait(false);
            return ((first & 0x3F) << 8) | rest[0];
        }

        if ((first & 0xE0) == 0xC0)
        {
            var rest = await ReadExactAsync(stream, 2, cancellationToken).ConfigureAwait(false);
            return ((first & 0x1F) << 16) | (rest[0] << 8) | rest[1];
        }

        if ((first & 0xF0) == 0xE0)
        {
            var rest = await ReadExactAsync(stream, 3, cancellationToken).ConfigureAwait(false);
            return ((first & 0x0F) << 24) | (rest[0] << 16) | (rest[1] << 8) | rest[2];
        }

        var wide = await ReadExactAsync(stream, 4, cancellationToken).ConfigureAwait(false);
        return (wide[0] << 24) | (wide[1] << 16) | (wide[2] << 8) | wide[3];
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken cancellationToken)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, count - read), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                throw new EndOfStreamException();
            }

            read += n;
        }

        return buffer;
    }

    /// <summary>Loads a committed fixture profile from the test output's <c>Fixtures</c> directory.</summary>
    public static RouterOsProfile LoadProfile(string profileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", profileName + ".json");
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<RouterOsProfile>(json, ProfileJsonOptions)
            ?? throw new InvalidOperationException($"Fixture '{profileName}' deserialized to null.");
    }

    private static readonly JsonSerializerOptions ProfileJsonOptions = new(JsonSerializerDefaults.Web);
}
