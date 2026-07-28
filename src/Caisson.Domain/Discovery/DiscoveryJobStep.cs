using Caisson.Domain.Enums;

namespace Caisson.Domain.Discovery;

/// <summary>
/// One durable step within a <see cref="DiscoveryJob"/> (story #8, AC1). Unlike the append-only
/// observed-state graph, a step is a mutable registry-style row: its status is transitioned in place as
/// the runner executes it, so a restarted process can resume from the last completed step. It carries
/// only counts/diagnostics in <see cref="ResultSummaryJson"/> — never secrets or raw device data (NFR4).
/// </summary>
public sealed class DiscoveryJobStep
{
    /// <summary>Maximum length of the bounded <see cref="ResultSummaryJson"/> payload.</summary>
    public const int MaxResultSummaryJsonLength = 4096;

    /// <summary>Maximum length of the operator-safe <see cref="ErrorMessage"/> (matches the column bound).</summary>
    public const int MaxErrorMessageLength = 2048;

    private DiscoveryJobStep()
    {
        // EF Core materialization constructor.
    }

    /// <summary>Creates a pending step for a job.</summary>
    public DiscoveryJobStep(Guid id, Guid jobId, DiscoveryStepName stepName)
    {
        Id = id;
        JobId = jobId;
        StepName = stepName;
        Status = DiscoveryStepStatus.Pending;
    }

    /// <summary>Primary key.</summary>
    public Guid Id { get; private set; }

    /// <summary>The job this step belongs to.</summary>
    public Guid JobId { get; private set; }

    /// <summary>Which step this is.</summary>
    public DiscoveryStepName StepName { get; private set; }

    /// <summary>Current durable status of the step.</summary>
    public DiscoveryStepStatus Status { get; private set; }

    /// <summary>Number of execution attempts made so far (bounded-retry, NFR1).</summary>
    public int AttemptCount { get; private set; }

    /// <summary>When the current/last attempt started.</summary>
    public DateTime? StartedAtUtc { get; private set; }

    /// <summary>When the step reached a terminal status.</summary>
    public DateTime? FinishedAtUtc { get; private set; }

    /// <summary>Wall-clock duration of the completed step in milliseconds (NFR4 traceability).</summary>
    public long? DurationMs { get; private set; }

    /// <summary>Stable machine-readable error code when the step failed.</summary>
    public string? ErrorCode { get; private set; }

    /// <summary>Operator-safe error message when the step failed.</summary>
    public string? ErrorMessage { get; private set; }

    /// <summary>Bounded <c>jsonb</c> summary of the step outcome (counts/diagnostics only, no secrets).</summary>
    public string? ResultSummaryJson { get; private set; }

    /// <summary>Marks the step as running and increments the attempt counter.</summary>
    public void BeginAttempt(DateTime startedAtUtc)
    {
        Status = DiscoveryStepStatus.InProgress;
        StartedAtUtc = startedAtUtc;
        FinishedAtUtc = null;
        DurationMs = null;
        AttemptCount++;
    }

    /// <summary>Marks the step as succeeded, recording its duration and an optional result summary.</summary>
    /// <exception cref="ArgumentException">Thrown when the summary payload exceeds the bound.</exception>
    public void Succeed(DateTime finishedAtUtc, string? resultSummaryJson = null)
    {
        GuardSummary(resultSummaryJson);
        Status = DiscoveryStepStatus.Succeeded;
        Finish(finishedAtUtc);
        ResultSummaryJson = resultSummaryJson;
        ErrorCode = null;
        ErrorMessage = null;
    }

    /// <summary>Marks the step as failed with a stable error code/message.</summary>
    public void Fail(DateTime finishedAtUtc, string errorCode, string? errorMessage)
    {
        Status = DiscoveryStepStatus.Failed;
        Finish(finishedAtUtc);
        ErrorCode = errorCode;
        ErrorMessage = Truncate(errorMessage);
    }

    /// <summary>Marks the step as skipped (already done, or the job was canceled).</summary>
    public void Skip(DateTime finishedAtUtc)
    {
        Status = DiscoveryStepStatus.Skipped;
        Finish(finishedAtUtc);
    }

    private void Finish(DateTime finishedAtUtc)
    {
        FinishedAtUtc = finishedAtUtc;
        DurationMs = StartedAtUtc is { } started
            ? (long)Math.Max(0, (finishedAtUtc - started).TotalMilliseconds)
            : null;
    }

    private static string? Truncate(string? message)
        => message is { Length: > MaxErrorMessageLength } ? message[..MaxErrorMessageLength] : message;

    private static void GuardSummary(string? resultSummaryJson)
    {
        if (resultSummaryJson is { Length: > MaxResultSummaryJsonLength })
        {
            throw new ArgumentException(
                $"Result summary JSON exceeds the {MaxResultSummaryJsonLength}-character bound.",
                nameof(resultSummaryJson));
        }
    }
}
