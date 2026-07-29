namespace Caisson.Drivers.MikroTik.Transport;

/// <summary>
/// The bounded write RouterOS API surface the mutating driver depends on. Abstracted so
/// <see cref="Caisson.Drivers.MikroTik.RouterOsSwitchMutatingDriver"/> can be unit-tested against a fake
/// client without a socket, while the real <see cref="RouterOsWriteApiClient"/> owns the wire protocol
/// (shared with the read client via <see cref="RouterOsApiConnection"/>, ADR 0031).
/// </summary>
public interface IRouterOsWriteApiClient : IAsyncDisposable
{
    /// <summary>Opens the socket and performs the RouterOS login handshake.</summary>
    Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Sends a single <paramref name="command"/> from <see cref="RouterOsWriteCommands.Allowlist"/> with
    /// the given pre-built attribute/query words (e.g. <c>=pvid=20</c>, <c>?interface=ether1</c> —
    /// assembled by the driver from already-validated typed values, never raw caller input) and returns
    /// one raw key/value map per <c>!re</c> reply row (empty for a bare <c>!done</c> acknowledgement).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown before any I/O when <paramref name="command"/> is not on the write allowlist.
    /// </exception>
    Task<IReadOnlyList<IReadOnlyDictionary<string, string>>> ExecuteAsync(
        string command, IReadOnlyList<string> words, CancellationToken cancellationToken);
}
