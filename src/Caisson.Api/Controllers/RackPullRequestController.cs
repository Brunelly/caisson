using System.Text.Json;
using Caisson.Api.Contracts;
using Caisson.Domain.Git;
using Caisson.Infrastructure.Persistence;
using Caisson.Infrastructure.Persistence.Queries;
using Caisson.Infrastructure.Persistence.Shaping;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Caisson.Api.Controllers;

/// <summary>
/// Read-only, rack-scoped PR status + transition history endpoints (story #173, Task #213/#215). GET-only,
/// guarded by <see cref="Security.AuthorizationPolicies.TopologyRead"/>. Both endpoints call
/// <see cref="ReadOnlyControllerBase.CheckRackAccessAsync"/> FIRST so no repository metadata leaks on a denied
/// or unknown rack, and an accessible rack without a PR returns a consistent no-link representation. The status
/// endpoint doubles as the SignalR-down UI fallback.
/// </summary>
[ApiController]
[Route("api/racks/{rackId:guid}/git")]
[Authorize(Policy = Security.AuthorizationPolicies.TopologyRead)]
[Produces("application/json")]
public sealed class RackPullRequestController : ReadOnlyControllerBase
{
    private const string EventsEndpoint = "git.pr.events";

    private readonly CaissonDbContext _context;

    public RackPullRequestController(CaissonDbContext context)
        => _context = context ?? throw new ArgumentNullException(nameof(context));

    /// <summary>Returns the rack's current PR status projection (or a no-link representation).</summary>
    [HttpGet("pull-request")]
    [ProducesResponseType(typeof(PullRequestStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PullRequestStatusDto>> GetStatus(Guid rackId, CancellationToken cancellationToken)
    {
        if (await CheckRackAccessAsync(rackId, cancellationToken) is { } denied)
        {
            return denied;
        }

        if (!await _context.RackExistsAsync(rackId, cancellationToken))
        {
            return RackNotFound(rackId);
        }

        // The rack's most recently-updated PR status (a rack may have had several PRs over time).
        var record = await _context.GitPullRequestStatuses
            .AsNoTracking()
            .Where(s => s.RackId == rackId)
            .OrderByDescending(s => s.UpdatedAtUtc)
            .ThenByDescending(s => s.Id)
            .FirstOrDefaultAsync(cancellationToken);

        return Ok(ToDto(record));
    }

    /// <summary>Returns the rack's PR status transition history (git.pr.* audit events), newest-first, paginated.</summary>
    [HttpGet("pull-request/events")]
    [ProducesResponseType(typeof(PagedResult<PrStatusEventDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PagedResult<PrStatusEventDto>>> GetEvents(
        Guid rackId,
        [FromQuery] string? cursor,
        [FromQuery] int? pageSize,
        CancellationToken cancellationToken)
    {
        // Access check FIRST (matches GetStatus and the class-level invariant), before paging validation, so a
        // denied rack cannot distinguish a valid from an invalid page request.
        if (await CheckRackAccessAsync(rackId, cancellationToken) is { } denied)
        {
            return denied;
        }

        if (!RequestPaging.TryResolve(pageSize, cursor, rackId, EventsEndpoint, out var limit, out var after, out var pagingError))
        {
            return ValidationError(pagingError!.Value);
        }

        if (!await _context.RackExistsAsync(rackId, cancellationToken))
        {
            return RackNotFound(rackId);
        }

        var page = await _context.GitPrAuditPageAsync(rackId, after, limit + 1, cancellationToken);
        var (items, next) = Paginate(page, limit, a => CursorCodec.Encode(a.OccurredAtUtc, a.Id, rackId, EventsEndpoint));

        return Ok(new PagedResult<PrStatusEventDto>(items.Select(ToEventDto).ToList(), next));
    }

    private static PullRequestStatusDto ToDto(GitPullRequestStatusRecord? record)
    {
        if (record is null)
        {
            return new PullRequestStatusDto(
                HasPullRequest: false,
                PullRequestNumber: null,
                PullRequestUrl: null,
                State: null,
                HeadSha: null,
                ChecksConclusion: GitPullRequestChecksConclusion.Unknown.ToString(),
                FailingChecksCount: null,
                ChecksSummary: null,
                LastUpdated: null,
                LastChecked: null,
                LastPollFailureReason: null,
                CanApply: false,
                GateReasonCode: GitPrGateReasonCodes.NoPrLinked);
        }

        // The read gate reflects the shown PR: merged → apply allowed; otherwise not merged.
        var canApply = record.State == GitPullRequestStatus.Merged;
        var gateReason = canApply ? GitPrGateReasonCodes.Allowed : GitPrGateReasonCodes.PrNotMerged;

        return new PullRequestStatusDto(
            HasPullRequest: true,
            PullRequestNumber: record.PullRequestNumber,
            PullRequestUrl: record.PullRequestUrl,
            State: record.State.ToString(),
            HeadSha: record.HeadSha,
            ChecksConclusion: record.ChecksConclusion.ToString(),
            FailingChecksCount: record.FailingChecksCount,
            ChecksSummary: record.ChecksSummary,
            LastUpdated: AsUtc(record.UpdatedAtUtc),
            LastChecked: AsUtc(record.LastCheckedAtUtc),
            LastPollFailureReason: record.LastPollFailureReason,
            CanApply: canApply,
            GateReasonCode: gateReason);
    }

    private static PrStatusEventDto ToEventDto(Caisson.Domain.Topology.TopologyAuditEvent auditEvent)
    {
        string? previousState = null, newState = null, previousChecks = null, newChecks = null;
        if (!string.IsNullOrEmpty(auditEvent.DetailsJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(auditEvent.DetailsJson);
                var root = doc.RootElement;
                previousState = GetString(root, "previousState");
                newState = GetString(root, "newState");
                previousChecks = GetString(root, "previousChecks");
                newChecks = GetString(root, "newChecks");
            }
            catch (JsonException)
            {
                // Malformed details never break the history read.
            }
        }

        return new PrStatusEventDto(
            auditEvent.Id,
            AsUtc(auditEvent.OccurredAtUtc),
            auditEvent.Action,
            auditEvent.ActorId,
            previousState,
            newState,
            previousChecks,
            newChecks,
            auditEvent.CorrelationId);
    }

    private static string? GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset AsUtc(DateTime value)
        => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
