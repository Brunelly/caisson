namespace Caisson.Drivers.Abstractions.Results;

/// <summary>A structured, machine-classifiable description of a failed driver call.</summary>
/// <param name="Code">The failure taxonomy code.</param>
/// <param name="Message">
/// A human-readable description of the failure. Must never contain credential/secret material
/// (e.g. passwords, API keys, tokens) even when the underlying failure was an authentication error.
/// </param>
/// <param name="Retryable">Whether a caller may reasonably retry the same call.</param>
public sealed record DriverError(DriverErrorCode Code, string Message, bool Retryable);
