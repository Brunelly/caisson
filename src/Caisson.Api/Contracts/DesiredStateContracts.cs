using System.Text.Json;

namespace Caisson.Api.Contracts;

/// <summary>
/// Story #62/#63 desired-state read contracts. Every DTO here is a read-only projection of typed intent,
/// revision metadata, or the raw payload snapshot — NEVER a webhook secret, repo credential, or token
/// (AC5).
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
    Guid VersionId,
    string RackSlug,
    string CommitSha,
    DateTime CreatedAtUtc,
    DesiredRackIntentDto Rack,
    string? AuthorName,
    string? AuthorEmail,
    DateTime? AuthorWhenUtc,
    string ContentHash,
    JsonElement DesiredStateJson);

/// <summary>One revision's metadata only (story #63, AC3) — never the payload; see <see cref="DesiredStateRevisionDetailDto"/>.</summary>
public sealed record DesiredStateRevisionMetadataDto(
    Guid Id,
    string RackSlug,
    string CommitSha,
    DateTime CreatedAtUtc,
    string? AuthorName,
    string? AuthorEmail,
    DateTime? AuthorWhenUtc,
    string ContentHash,
    int SchemaVersion,
    string IngestedBy);

/// <summary>One revision's metadata plus its full materialised desired-state payload (story #63, AC3).</summary>
public sealed record DesiredStateRevisionDetailDto(
    Guid Id,
    string RackSlug,
    string CommitSha,
    DateTime CreatedAtUtc,
    string? AuthorName,
    string? AuthorEmail,
    DateTime? AuthorWhenUtc,
    string ContentHash,
    int SchemaVersion,
    string IngestedBy,
    JsonElement DesiredStateJson);

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
