namespace Caisson.Api.Contracts;

/// <summary>
/// Story #62 desired-state read contracts. Every DTO here is a read-only projection of typed intent or
/// ingestion metadata — NEVER a webhook secret, repo credential, or token (AC5).
/// </summary>
public sealed record GitWebhookAcceptedResponse(Guid CorrelationId);

public sealed record DesiredStateStatusDto(
    DateTime? LastSuccessAtUtc,
    DateTime? LastAttemptAtUtc,
    string? LatestCommitSha,
    string OverallStatus);

public sealed record DesiredStateRackSummaryDto(string RackSlug, string CommitSha, DateTime CreatedAtUtc);

public sealed record DesiredPortIntentDto(
    string PortName,
    string StableKey,
    int AccessVlan,
    string? Description,
    string? NeighborSystemName,
    string? NeighborPortId);

public sealed record DesiredSwitchIntentDto(string SwitchName, string StableKey, IReadOnlyList<DesiredPortIntentDto> Ports);

public sealed record DesiredRackIntentDto(string RackSlug, string StableKey, IReadOnlyList<DesiredSwitchIntentDto> Switches);

public sealed record DesiredStateActiveDto(
    Guid VersionId, string RackSlug, string CommitSha, DateTime CreatedAtUtc, DesiredRackIntentDto Rack);

public sealed record DesiredStateIngestionRunSummaryDto(
    Guid RunId,
    string TriggerType,
    string Status,
    DateTime StartedAtUtc,
    DateTime? CompletedAtUtc,
    string RepoUrl,
    string Branch,
    string? CommitSha,
    string? CommitAuthor,
    DateTime? CommitTimeUtc,
    string? ErrorCategory,
    string? ErrorSummary);

public sealed record DesiredStateValidationErrorDto(
    Guid Id,
    Guid IngestionRunId,
    string RackSlug,
    string FilePath,
    string Location,
    string Message,
    string Severity,
    int? Line,
    int? Column);
