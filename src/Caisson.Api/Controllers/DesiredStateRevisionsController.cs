using Caisson.Api.Auditing;
using Caisson.Api.Contracts;
using Caisson.Api.Security;
using Caisson.Domain.DesiredState;
using Caisson.Infrastructure.Persistence;
using Caisson.Infrastructure.Persistence.Queries;
using Caisson.Infrastructure.Persistence.Shaping;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Caisson.Api.Controllers;

/// <summary>
/// Read-only desired-state revision history endpoints (story #63, AC3): a keyset-paginated, metadata-only
/// list of a rack's revisions, and full-payload lookups by revision id or commit SHA. GET-only, guarded by
/// <see cref="AuthorizationPolicies.TopologyRead"/>. Keyed by string <c>rackSlug</c> like
/// <see cref="DesiredStateRacksController"/> (ADR 0025); cursors are bound to the rack slug via
/// <see cref="CursorCodec"/>'s string-subject overload so a history cursor for one rack can never be
/// replayed against another's pagination (NFR1).
/// </summary>
[ApiController]
[Route("api/desired-state/racks")]
[Authorize(Policy = AuthorizationPolicies.TopologyRead)]
[Produces("application/json")]
public sealed class DesiredStateRevisionsController : DesiredStateControllerBase
{
    private const string RevisionsEndpoint = "desired-state-revisions";

    private readonly CaissonDbContext _context;
    private readonly IBestEffortAuditEventWriter _audit;

    public DesiredStateRevisionsController(CaissonDbContext context, IBestEffortAuditEventWriter audit)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    /// <summary>A keyset page of one rack's revision metadata, newest-first. Never includes the payload (AC3, NFR3).</summary>
    [HttpGet("{rackSlug}/revisions")]
    [ProducesResponseType(typeof(PagedResult<DesiredStateRevisionMetadataDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResult<DesiredStateRevisionMetadataDto>>> ListRevisions(
        string rackSlug, [FromQuery] string? cursor, [FromQuery] int? pageSize, CancellationToken cancellationToken)
    {
        if (!RequestPaging.TryResolve(pageSize, cursor, rackSlug, RevisionsEndpoint, out var limit, out var after, out var error))
        {
            return ValidationError(error!.Value);
        }

        var page = await _context.RevisionHistoryPageAsync(rackSlug, after, limit + 1, cancellationToken);
        var (items, next) = Paginate(
            page, limit, m => CursorCodec.Encode(m.CreatedAtUtc, m.Id, rackSlug, RevisionsEndpoint));

        await _audit.WriteReadAsync(User, rackId: null, "desired-state.revisions.read", "desired-state-rack", rackSlug, cancellationToken);
        return Ok(new PagedResult<DesiredStateRevisionMetadataDto>(
            items.Select(DesiredStateContractMappers.ToRevisionMetadata).ToList(), next));
    }

    /// <summary>One rack's revision by id, with its full payload. 404s (scoped to the rack) if the id belongs to another rack or does not exist.</summary>
    [HttpGet("{rackSlug}/revisions/{revisionId:guid}")]
    [ProducesResponseType(typeof(DesiredStateRevisionDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DesiredStateRevisionDetailDto>> GetRevisionById(
        string rackSlug, Guid revisionId, CancellationToken cancellationToken)
    {
        if (!DesiredStateSchema.IsValidRackSlug(rackSlug))
        {
            return DesiredRevisionNotFound(rackSlug, $"with id '{revisionId}'");
        }

        var version = await _context.RevisionByIdAsync(rackSlug, revisionId, cancellationToken);
        if (version is null)
        {
            return DesiredRevisionNotFound(rackSlug, $"with id '{revisionId}'");
        }

        await _audit.WriteReadAsync(
            User, rackId: null, "desired-state.revision.read", "desired-state-version", version.Id.ToString(), cancellationToken);

        SetContentHashETag(version.ContentHash);
        if (IsNotModified(version.ContentHash))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return Ok(DesiredStateContractMappers.ToRevisionDetail(version));
    }

    /// <summary>One rack's revision by git commit SHA, with its full payload. 404s (scoped to the rack) if the commit belongs to another rack or does not exist.</summary>
    [HttpGet("{rackSlug}/revisions/by-commit/{commitSha}")]
    [ProducesResponseType(typeof(DesiredStateRevisionDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DesiredStateRevisionDetailDto>> GetRevisionByCommit(
        string rackSlug, string commitSha, CancellationToken cancellationToken)
    {
        if (!DesiredStateSchema.IsValidRackSlug(rackSlug))
        {
            return DesiredRevisionNotFound(rackSlug, $"for commit '{commitSha}'");
        }

        var version = await _context.RevisionByCommitShaAsync(rackSlug, commitSha, cancellationToken);
        if (version is null)
        {
            return DesiredRevisionNotFound(rackSlug, $"for commit '{commitSha}'");
        }

        await _audit.WriteReadAsync(
            User, rackId: null, "desired-state.revision.read", "desired-state-version", version.Id.ToString(), cancellationToken);

        SetContentHashETag(version.ContentHash);
        if (IsNotModified(version.ContentHash))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return Ok(DesiredStateContractMappers.ToRevisionDetail(version));
    }
}
