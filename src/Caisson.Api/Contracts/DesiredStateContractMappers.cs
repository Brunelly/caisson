using System.Text.Json;
using Caisson.Domain.DesiredState;
using Caisson.Infrastructure.Persistence.Queries;

namespace Caisson.Api.Contracts;

/// <summary>Pure entity → DTO mappers for the story #62/#63 desired-state read surface. Never the reverse.</summary>
public static class DesiredStateContractMappers
{
    public static DesiredStateRackSummaryDto ToRackSummary(DesiredStateVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return new DesiredStateRackSummaryDto(version.RackSlug, version.CommitSha, version.CreatedAtUtc);
    }

    public static DesiredStateActiveDto ToActive(DesiredStateVersionTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        return new DesiredStateActiveDto(
            tree.Version.Id, tree.Version.RackSlug, tree.Version.CommitSha, tree.Version.CreatedAtUtc,
            ToRackIntent(tree.Rack, tree.Switches, tree.Ports),
            tree.Version.AuthorName, tree.Version.AuthorEmail, tree.Version.AuthorWhenUtc,
            tree.Version.ContentHash, ParsePayload(tree.Version.DesiredStateJson));
    }

    public static DesiredStateRevisionMetadataDto ToRevisionMetadata(DesiredStateRevisionMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return new DesiredStateRevisionMetadataDto(
            metadata.Id, metadata.RackSlug, metadata.CommitSha, metadata.CreatedAtUtc, metadata.AuthorName,
            metadata.AuthorEmail, metadata.AuthorWhenUtc, metadata.ContentHash, metadata.SchemaVersion,
            metadata.IngestedBy);
    }

    public static DesiredStateRevisionDetailDto ToRevisionDetail(DesiredStateVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return new DesiredStateRevisionDetailDto(
            version.Id, version.RackSlug, version.CommitSha, version.CreatedAtUtc, version.AuthorName,
            version.AuthorEmail, version.AuthorWhenUtc, version.ContentHash, version.SchemaVersion,
            version.IngestedBy, ParsePayload(version.DesiredStateJson));
    }

    /// <summary>
    /// Re-emits the stored canonical payload verbatim as a raw <see cref="JsonElement"/> rather than
    /// reconstructing it from the normalised intent tables — the by-id/by-commit queries deliberately
    /// return only the <see cref="DesiredStateVersion"/> row (NFR3: no extra joins for a payload the row
    /// already carries).
    /// </summary>
    private static JsonElement ParsePayload(string desiredStateJson)
    {
        using var document = JsonDocument.Parse(desiredStateJson);
        return document.RootElement.Clone();
    }

    private static DesiredRackIntentDto ToRackIntent(
        DesiredRackIntent rack, IReadOnlyList<DesiredSwitchIntent> switches, IReadOnlyList<DesiredPortIntent> ports)
    {
        var switchDtos = switches
            .Select(s => new DesiredSwitchIntentDto(
                s.SwitchName,
                s.StableKey,
                ports.Where(p => p.DesiredSwitchIntentId == s.Id).Select(ToPortIntent).ToList()))
            .ToList();
        return new DesiredRackIntentDto(rack.RackSlug, rack.StableKey, switchDtos);
    }

    private static DesiredPortIntentDto ToPortIntent(DesiredPortIntent port) => new(
        port.PortName, port.StableKey, port.AccessVlan, port.Description, port.NeighborSystemName, port.NeighborPortId);

    public static DesiredStateIngestionRunSummaryDto ToRunSummary(DesiredStateIngestionRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return new DesiredStateIngestionRunSummaryDto(
            run.Id, run.TriggerType.ToString(), run.Status.ToString(), run.StartedAtUtc, run.CompletedAtUtc,
            run.RepoUrl, run.Branch, run.CommitSha, run.CommitAuthor, run.CommitTimeUtc,
            run.ErrorCategory?.ToString(), run.ErrorSummary);
    }

    public static DesiredStateValidationErrorDto ToValidationError(DesiredStateValidationError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new DesiredStateValidationErrorDto(
            error.Id, error.IngestionRunId, error.RackSlug, error.FilePath, error.Location, error.Message,
            error.Severity.ToString(), error.Line, error.Column);
    }
}
