using Caisson.Domain.Security;

namespace Caisson.Domain.DesiredState;

/// <summary>
/// A single attempt to ingest the desired-state Git repository at one observed commit (story #62,
/// AC1). Like <c>DiscoveryJob</c> it is a mutable, registry-style entity — deliberately <b>not</b>
/// append-only — whose status is transitioned in place as the run progresses; it is the durable record
/// that both the poll scheduler and the webhook endpoint enqueue idempotently through, and it is what
/// the DB partial-unique indexes on <c>commit_sha</c>/<c>webhook_delivery_id</c> key against (NFR2/NFR3).
/// It never carries any Git credential or webhook secret (AC5).
/// </summary>
public sealed class DesiredStateIngestionRun
{
    /// <summary>Maximum length of the captured <see cref="CommitMessage"/>.</summary>
    public const int MaxCommitMessageLength = DesiredStateSchema.MaxCommitMessageLength;

    /// <summary>Maximum length of the operator-safe <see cref="ErrorSummary"/>.</summary>
    public const int MaxErrorSummaryLength = DesiredStateSchema.MaxErrorSummaryLength;

    private DesiredStateIngestionRun()
    {
        // EF Core materialization constructor.
        RepoUrl = null!;
        Branch = null!;
    }

    /// <summary>
    /// Creates a running ingestion attempt. Commit metadata is attached separately via
    /// <see cref="RecordCommit"/> once the latest commit for the branch has been fetched, so a run
    /// that fails before a commit is even reachable (Auth/Network) can still be persisted for AC6.
    /// </summary>
    public DesiredStateIngestionRun(
        Guid id,
        IngestionTriggerType triggerType,
        DateTime startedAtUtc,
        string repoUrl,
        string branch,
        Guid correlationId,
        string? webhookDeliveryId = null)
    {
        ArgumentNullException.ThrowIfNull(repoUrl);
        ArgumentNullException.ThrowIfNull(branch);

        Id = id;
        TriggerType = triggerType;
        StartedAtUtc = startedAtUtc;
        Status = IngestionRunStatus.Running;
        RepoUrl = repoUrl;
        Branch = branch;
        CorrelationId = correlationId;
        WebhookDeliveryId = webhookDeliveryId;
    }

    /// <summary>Stable run identifier.</summary>
    public Guid Id { get; private set; }

    /// <summary>How the run was initiated.</summary>
    public IngestionTriggerType TriggerType { get; private set; }

    /// <summary>When the run started.</summary>
    public DateTime StartedAtUtc { get; private set; }

    /// <summary>When the run reached a terminal state.</summary>
    public DateTime? CompletedAtUtc { get; private set; }

    /// <summary>Current durable lifecycle state.</summary>
    public IngestionRunStatus Status { get; private set; }

    /// <summary>The configured repository URL (may be logged at info level, sanitised — AC5).</summary>
    public string RepoUrl { get; private set; }

    /// <summary>The configured branch.</summary>
    public string Branch { get; private set; }

    /// <summary>The observed commit SHA, once fetched.</summary>
    public string? CommitSha { get; private set; }

    /// <summary>The commit author, once fetched.</summary>
    public string? CommitAuthor { get; private set; }

    /// <summary>The commit's authored time, once fetched.</summary>
    public DateTime? CommitTimeUtc { get; private set; }

    /// <summary>The commit message, once fetched (bounded, scrubbed).</summary>
    public string? CommitMessage { get; private set; }

    /// <summary>Correlation id stamped on every log line and audit event for this run.</summary>
    public Guid CorrelationId { get; private set; }

    /// <summary>
    /// The Git provider's webhook delivery id, when <see cref="TriggerType"/> is
    /// <see cref="IngestionTriggerType.Webhook"/> — the replay-protection key (NFR2).
    /// </summary>
    public string? WebhookDeliveryId { get; private set; }

    /// <summary>Stable machine-readable category when the run failed or no rack file validated.</summary>
    public IngestionErrorCategory? ErrorCategory { get; private set; }

    /// <summary>Operator-safe error summary; the stack trace itself stays in the log sink only (AC6).</summary>
    public string? ErrorSummary { get; private set; }

    /// <summary>Attaches the fetched commit's metadata to the (still-running) run.</summary>
    public void RecordCommit(string commitSha, string commitAuthor, DateTime commitTimeUtc, string? commitMessage)
    {
        ArgumentNullException.ThrowIfNull(commitSha);
        ArgumentNullException.ThrowIfNull(commitAuthor);

        CommitSha = commitSha;
        CommitAuthor = commitAuthor;
        CommitTimeUtc = commitTimeUtc;
        CommitMessage = Truncate(commitMessage, MaxCommitMessageLength);
    }

    /// <summary>Every rack file in the commit validated and was materialised.</summary>
    public void Succeed(DateTime completedAtUtc) => Complete(completedAtUtc, IngestionRunStatus.Succeeded);

    /// <summary>Some rack files validated; others failed validation (Q3's partial-accept policy).</summary>
    public void PartiallySucceed(DateTime completedAtUtc) => Complete(completedAtUtc, IngestionRunStatus.PartiallySucceeded);

    /// <summary>The run completed, but no rack file in the commit validated.</summary>
    public void MarkValidationFailed(DateTime completedAtUtc, string? errorSummary = null)
    {
        Complete(completedAtUtc, IngestionRunStatus.ValidationFailed);
        ErrorCategory = IngestionErrorCategory.Validation;
        ErrorSummary = Truncate(errorSummary, MaxErrorSummaryLength);
    }

    /// <summary>The run could not complete for an infrastructure reason.</summary>
    public void Fail(DateTime completedAtUtc, IngestionErrorCategory errorCategory, string? errorSummary)
    {
        Complete(completedAtUtc, IngestionRunStatus.Failed);
        ErrorCategory = errorCategory;
        ErrorSummary = Truncate(errorSummary, MaxErrorSummaryLength);
    }

    private void Complete(DateTime completedAtUtc, IngestionRunStatus status)
    {
        Status = status;
        CompletedAtUtc = completedAtUtc;
    }

    private static string? Truncate(string? message, int maxLength)
    {
        // Finding-#27-style backstop: scrub before bounding so redaction can never push it over the limit.
        var scrubbed = SecretScrubber.Scrub(message);
        return scrubbed is { Length: > 0 } && scrubbed.Length > maxLength ? scrubbed[..maxLength] : scrubbed;
    }
}
