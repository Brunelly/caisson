namespace Caisson.Drivers.Abstractions.Results;

/// <summary>
/// The outcome of a single read-only driver call. Every method on
/// <see cref="Caisson.Drivers.Abstractions.ReadOnly.ISwitchDiscoveryDriver"/> and
/// <see cref="Caisson.Drivers.Abstractions.ReadOnly.IBmcDiscoveryDriver"/> returns this instead of
/// throwing for expected failures (unreachable device, bad credentials, timeout, partial data). A
/// result is either successful — with an optional list of per-item <see cref="Diagnostics"/> for
/// partial reads — or failed with a single <see cref="DriverError"/>. Construction is only possible
/// through <see cref="Ok"/> and <see cref="Fail"/>, which keep <see cref="Success"/>,
/// <see cref="Value"/> and <see cref="Error"/> consistent with each other.
/// </summary>
/// <typeparam name="T">The DTO type returned by a successful call.</typeparam>
public sealed record DriverResult<T>
{
    private static readonly IReadOnlyList<DriverDiagnostic> NoDiagnostics = Array.Empty<DriverDiagnostic>();

    private DriverResult(
        bool success, T? value, DriverError? error, IReadOnlyList<DriverDiagnostic> diagnostics, TimeSpan duration)
    {
        Success = success;
        Value = value;
        Error = error;
        Diagnostics = diagnostics;
        Duration = duration;
    }

    /// <summary>Whether the call succeeded. When <c>true</c>, <see cref="Value"/> is non-null.</summary>
    public bool Success { get; }

    /// <summary>The returned DTO. Non-null if and only if <see cref="Success"/> is <c>true</c>.</summary>
    public T? Value { get; }

    /// <summary>The failure detail. Non-null if and only if <see cref="Success"/> is <c>false</c>.</summary>
    public DriverError? Error { get; }

    /// <summary>
    /// Per-item warnings or errors for a partially readable result (e.g. one port with no LLDP data).
    /// May be non-empty even when <see cref="Success"/> is <c>true</c>. Empty by default.
    /// </summary>
    public IReadOnlyList<DriverDiagnostic> Diagnostics { get; }

    /// <summary>How long the underlying call took, for logging/telemetry (NFR3).</summary>
    public TimeSpan Duration { get; }

    /// <summary>Creates a successful result, optionally carrying per-item diagnostics.</summary>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="value"/> is <c>null</c>.</exception>
    public static DriverResult<T> Ok(T value, TimeSpan duration, IReadOnlyList<DriverDiagnostic>? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new DriverResult<T>(true, value, null, diagnostics ?? NoDiagnostics, duration);
    }

    /// <summary>Creates a failed result. <see cref="Value"/> is left default/null.</summary>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="error"/> is <c>null</c>.</exception>
    public static DriverResult<T> Fail(
        DriverError error, TimeSpan duration, IReadOnlyList<DriverDiagnostic>? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new DriverResult<T>(false, default, error, diagnostics ?? NoDiagnostics, duration);
    }
}
