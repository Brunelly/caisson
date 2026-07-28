namespace Caisson.Orchestration.Discovery;

/// <summary>
/// Signals a step-level failure with a stable, operator-safe error code and whether it is worth
/// retrying. The orchestrator's retry helper retries a <see cref="Retryable"/> failure (or any
/// unexpected exception) up to the configured cap, and fails the step/job immediately on a
/// non-retryable one (AC1/NFR1).
/// </summary>
public sealed class DiscoveryStepException : Exception
{
    /// <summary>Creates a step exception with a stable error code.</summary>
    public DiscoveryStepException(string errorCode, string message, bool retryable, Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
        Retryable = retryable;
    }

    /// <summary>Stable, operator-safe error code (see <see cref="DiscoveryErrorCodes"/>).</summary>
    public string ErrorCode { get; }

    /// <summary>Whether the orchestrator should retry the step before failing it.</summary>
    public bool Retryable { get; }
}
