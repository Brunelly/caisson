namespace Caisson.Orchestration.DriftApply;

/// <summary>
/// A step failure carrying a stable, operator-safe error code and whether the orchestrator should retry
/// the step before failing the job. Mirrors <c>Discovery.DiscoveryStepException</c>.
/// </summary>
public sealed class DriftApplyStepException : Exception
{
    /// <summary>Creates a step exception with a stable error code.</summary>
    public DriftApplyStepException(string errorCode, string message, bool retryable, Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
        Retryable = retryable;
    }

    /// <summary>Stable, operator-safe error code (see <see cref="DriftApplyErrorCodes"/>).</summary>
    public string ErrorCode { get; }

    /// <summary>Whether the orchestrator should retry the step before failing it.</summary>
    public bool Retryable { get; }
}
