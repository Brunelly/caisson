using Caisson.Drivers.Redfish.Transport;

namespace Caisson.Drivers.Redfish.Tests.Fakes;

/// <summary>
/// An in-memory <see cref="IRedfishClient"/> for driver tests: per-path canned JSON bodies (or throwing
/// delegates to simulate unreachable/timeout/auth/parse failures). Unlike the real client it does not run
/// the allowlist guard — that boundary is exercised directly against <c>RedfishClient</c>/<c>RedfishReadPaths</c>.
/// </summary>
public sealed class FakeRedfishClient : IRedfishClient
{
    /// <summary>Per-path handlers. A handler may return a body or throw to simulate a failure.</summary>
    public Dictionary<string, Func<string>> Responses { get; } = new(StringComparer.Ordinal);

    /// <summary>Every path requested, in order — lets tests assert on the navigation performed.</summary>
    public List<string> RequestedPaths { get; } = new();

    public int DisposeCount { get; private set; }

    public void SetJson(string path, string json) => Responses[path] = () => json;

    public void SetThrows(string path, Func<Exception> exceptionFactory)
        => Responses[path] = () => throw exceptionFactory();

    public Task<string> GetAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequestedPaths.Add(path);
        if (Responses.TryGetValue(path, out var handler))
        {
            return Task.FromResult(handler());
        }

        // An unmapped path models a 404 — a Redfish protocol failure the driver maps to a ParseError.
        throw new RedfishException($"The Redfish endpoint returned HTTP 404 for '{path}'.");
    }

    public void Dispose() => DisposeCount++;
}
