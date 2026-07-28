using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace Caisson.Drivers.Redfish.IntegrationTests;

/// <summary>
/// An in-process HTTPS Redfish server for CI (AC4): a loopback <see cref="TcpListener"/> that performs a
/// real server-side <see cref="SslStream"/> handshake with a generated self-signed certificate (so the
/// driver's <c>ValidateServerCertificate</c> path runs end-to-end, mirroring <c>RouterOsApiSimulator</c>),
/// then answers GET requests by replaying a committed iLO Redfish JSON <see cref="RedfishProfile"/> keyed
/// by path. It is deliberately ASP.NET-free — a minimal HTTP/1.1 request parser over the TLS stream — so no
/// container or physical hardware is needed. The <c>authFail</c> profile answers 401 to everything.
/// </summary>
public sealed class RedfishSimulator : IAsyncDisposable
{
    private static readonly Encoding Ascii = new ASCIIEncoding();

    private readonly RedfishProfile _profile;
    private readonly X509Certificate2 _serverCertificate;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentQueue<string> _requestedPaths = new();
    private Task? _acceptLoop;

    public RedfishSimulator(RedfishProfile profile, X509Certificate2 serverCertificate)
    {
        _profile = profile;
        _serverCertificate = serverCertificate;
        _listener = new TcpListener(IPAddress.Loopback, 0);
    }

    /// <summary>The loopback host the simulator listens on.</summary>
    public string Host => IPAddress.Loopback.ToString();

    /// <summary>The ephemeral port assigned once <see cref="Start"/> has run.</summary>
    public int Port { get; private set; }

    /// <summary>
    /// Every request-line path the simulator actually received, in order — exactly what
    /// <see cref="System.Net.Http.HttpClient"/> put on the wire (so any <c>..</c> dot-segments have already
    /// been collapsed). Lets a security test assert that a resource the read-only allowlist rejects was never
    /// requested at all, rather than merely not returned.
    /// </summary>
    public IReadOnlyCollection<string> RequestedPaths => _requestedPaths.ToArray();

    /// <summary>Binds the listener and starts accepting connections in the background.</summary>
    public void Start()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
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
        await using (var ssl = new SslStream(networkStream, leaveInnerStreamOpen: false))
        {
            try
            {
                await ssl.AuthenticateAsServerAsync(
                    new SslServerAuthenticationOptions { ServerCertificate = _serverCertificate },
                    cancellationToken).ConfigureAwait(false);

                while (!cancellationToken.IsCancellationRequested)
                {
                    var request = await ReadRequestLineAsync(ssl, cancellationToken).ConfigureAwait(false);
                    if (request is null)
                    {
                        return;
                    }

                    await WriteResponseAsync(ssl, request.Value.Method, request.Value.Path, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                // Shutting down.
            }
            catch (IOException)
            {
                // Client disconnected — normal.
            }
            catch (System.Security.Authentication.AuthenticationException)
            {
                // The client rejected our certificate and aborted the handshake — expected in TLS tests.
            }
        }
    }

    private static async Task<(string Method, string Path)?> ReadRequestLineAsync(
        Stream stream, CancellationToken cancellationToken)
    {
        // Read the request head (up to the blank line). GET has no body, so this is the whole request.
        var buffer = new List<byte>(256);
        var single = new byte[1];
        var terminator = 0;
        while (true)
        {
            var n = await stream.ReadAsync(single, cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                return buffer.Count == 0 ? null : ParseRequestLine(buffer);
            }

            buffer.Add(single[0]);
            terminator = single[0] switch
            {
                (byte)'\n' when terminator is 1 or 3 => 4,
                (byte)'\n' => 2,
                (byte)'\r' when terminator is 2 => 3,
                (byte)'\r' => 1,
                _ => 0,
            };

            if (terminator == 4)
            {
                return ParseRequestLine(buffer);
            }
        }
    }

    private static (string Method, string Path)? ParseRequestLine(List<byte> buffer)
    {
        var text = Ascii.GetString(buffer.ToArray());
        var firstLine = text.Split("\r\n", StringSplitOptions.None)[0];
        var parts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? (parts[0], parts[1]) : null;
    }

    private async Task WriteResponseAsync(Stream stream, string method, string path, CancellationToken cancellationToken)
    {
        int status;
        string body;

        // Record what actually reached the server (post-canonicalization) so tests can prove an off-allowlist
        // resource was never requested.
        _requestedPaths.Enqueue(StripQuery(path));

        if (_profile.AuthFail)
        {
            status = 401;
            body = """{ "error": { "code": "Base.1.0.GeneralError", "message": "Invalid credentials" } }""";
        }
        else if (!string.Equals(method, "GET", StringComparison.Ordinal))
        {
            // The read-only driver never sends a non-GET; guard anyway so a bug would be caught, not served.
            status = 405;
            body = """{ "error": "method not allowed" }""";
        }
        else if (_profile.Paths.TryGetValue(StripQuery(path), out var payload))
        {
            status = 200;
            body = payload;
        }
        else
        {
            status = 404;
            body = """{ "error": { "code": "Base.1.0.ResourceMissing" } }""";
        }

        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var head =
            $"HTTP/1.1 {status} {ReasonPhrase(status)}\r\n" +
            "Content-Type: application/json\r\n" +
            $"Content-Length: {bodyBytes.Length}\r\n" +
            "\r\n";

        await stream.WriteAsync(Ascii.GetBytes(head), cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bodyBytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string StripQuery(string path)
    {
        var cut = path.IndexOf('?', StringComparison.Ordinal);
        return cut < 0 ? path : path[..cut];
    }

    private static string ReasonPhrase(int status) => status switch
    {
        200 => "OK",
        401 => "Unauthorized",
        404 => "Not Found",
        405 => "Method Not Allowed",
        _ => "Unknown",
    };

    /// <summary>Loads a committed fixture profile from the test output's <c>Fixtures</c> directory.</summary>
    public static RedfishProfile LoadProfile(string profileName)
    {
        var jsonPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", profileName + ".json");
        using var document = JsonDocument.Parse(File.ReadAllText(jsonPath));
        var root = document.RootElement;

        var authFail = root.TryGetProperty("authFail", out var af) && af.GetBoolean();
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("paths", out var pathsElement))
        {
            foreach (var property in pathsElement.EnumerateObject())
            {
                paths[property.Name] = property.Value.GetRawText();
            }
        }

        return new RedfishProfile(authFail, paths);
    }
}

/// <summary>A committed simulator profile: whether to fail auth, and the per-path JSON responses.</summary>
public sealed record RedfishProfile(bool AuthFail, IReadOnlyDictionary<string, string> Paths);
