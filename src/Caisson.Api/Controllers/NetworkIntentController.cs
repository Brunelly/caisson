using System.Text.Json;
using Caisson.Api.Auditing;
using Caisson.Api.Contracts;
using Caisson.Api.Middleware;
using Caisson.Api.Security;
using Caisson.Domain.NetworkConfig;
using Caisson.Infrastructure.Persistence;
using Caisson.Infrastructure.Persistence.Auditing;
using Caisson.Infrastructure.Persistence.Queries;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace Caisson.Api.Controllers;

/// <summary>
/// Rack-scoped network-intent authoring endpoints (story #168/#176): a VLAN catalogue plus per-port
/// access-VLAN intent, persisted as the rack's single saved <see cref="RackNetworkIntent"/> draft.
/// Deliberately derives from <see cref="DiscoveryControllerBase"/> (not <see cref="ReadOnlyControllerBase"/>):
/// PUT/validate are policy-gated, non-GET actions (ADR 0013's precedent, mirroring
/// <see cref="DriftApplyController"/>). GET is read-only and viewable by any recognised role
/// (<see cref="AuthorizationPolicies.TopologyRead"/>); PUT/validate require the elevated
/// <see cref="AuthorizationPolicies.NetworkConfigAuthor"/> permission.
/// </summary>
[ApiController]
[Route("api/racks/{rackId:guid}/network-intent")]
[Produces("application/json")]
public sealed class NetworkIntentController : DiscoveryControllerBase
{
    private readonly CaissonDbContext _context;
    private readonly IMandatoryAuditOutbox _auditOutbox;
    private readonly ICorrelationContext _correlation;
    private readonly TimeProvider _time;

    public NetworkIntentController(
        CaissonDbContext context, IMandatoryAuditOutbox auditOutbox, ICorrelationContext correlation, TimeProvider time)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _auditOutbox = auditOutbox ?? throw new ArgumentNullException(nameof(auditOutbox));
        _correlation = correlation ?? throw new ArgumentNullException(nameof(correlation));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>
    /// Returns the rack's currently saved network intent (AC1/AC5). Never 404s for a rack with no saved
    /// intent yet — returns the empty default shape instead, so a Read Only user can view the (empty)
    /// authoring state before anyone has saved anything. Sets a weak <c>ETag</c> from the row's <c>xmin</c>
    /// concurrency token when a saved state exists; the client echoes it back via <c>If-Match</c> on PUT.
    /// </summary>
    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.TopologyRead)]
    [ProducesResponseType(typeof(NetworkIntentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<NetworkIntentDto>> Get(Guid rackId, CancellationToken cancellationToken)
    {
        if (await CheckRackAccessAsync(rackId, cancellationToken) is { } denied)
        {
            return denied;
        }

        if (!await _context.RackExistsAsync(rackId, cancellationToken))
        {
            return RackNotFound(rackId);
        }

        var entity = await _context.RackNetworkIntents.FirstOrDefaultAsync(x => x.RackId == rackId, cancellationToken);
        if (entity is null)
        {
            return Ok(NetworkIntentContractMappers.ToEmptyDto(rackId));
        }

        SetIntentETag(entity);
        return Ok(NetworkIntentContractMappers.ToDto(entity));
    }

    /// <summary>
    /// Saves the rack's network intent (AC1/AC2/AC5): checks per-rack access/existence first (mirroring
    /// GET/Validate, so a caller without rack access gets 404 rather than paying for validation of a rack
    /// it can't see), then validates (400 on any field error, DB untouched), then upserts under an
    /// xmin-based optimistic-concurrency check (409 with an actionable "reload and reapply" detail on a
    /// stale <c>If-Match</c> token). A Tier 1 (mandatory-durable) <c>network-intent.saved</c> audit event
    /// (rackId, actor, timestamp, VLAN/port-intent counts, correlationId — never the full payload, NFR3)
    /// is staged onto the SAME transaction as the upsert (story #308, ADR 0064): a stale-xmin conflict
    /// leaves neither the intent nor the audit row.
    /// </summary>
    [HttpPut]
    [Authorize(Policy = AuthorizationPolicies.NetworkConfigAuthor)]
    [ProducesResponseType(typeof(NetworkIntentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<NetworkIntentDto>> Put(
        Guid rackId, [FromBody] NetworkIntentSaveRequest? request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return ValidationError((nameof(request), "A request body is required."));
        }

        if (await CheckRackAccessAsync(rackId, cancellationToken) is { } denied)
        {
            return denied;
        }

        if (!await _context.RackExistsAsync(rackId, cancellationToken))
        {
            return RackNotFound(rackId);
        }

        var (vlanCatalogue, portIntents) = NetworkIntentContractMappers.FromRequest(request);
        if (FieldErrors(vlanCatalogue, portIntents) is { } validationResult)
        {
            return validationResult;
        }

        var entity = await _context.RackNetworkIntents.FirstOrDefaultAsync(x => x.RackId == rackId, cancellationToken);
        var nowUtc = _time.GetUtcNow().UtcDateTime;
        var (actorType, actorId) = AuditActorResolver.Resolve(User);
        var intentJson = NetworkIntentContractMappers.ToIntentJson(vlanCatalogue, portIntents);

        if (entity is null)
        {
            entity = new RackNetworkIntent(Guid.NewGuid(), rackId, intentJson, actorId, nowUtc);
            _context.RackNetworkIntents.Add(entity);
        }
        else
        {
            var providedToken = ParseIfMatchToken();
            var currentToken = GetXmin(entity);
            if (providedToken is null || providedToken != currentToken)
            {
                return StaleIntentConflict(rackId);
            }

            // Belt-and-braces: also arm EF's own concurrency check against a race between this read and
            // the write below, so a same-millisecond concurrent save still surfaces as 409, not a silent
            // last-write-wins overwrite.
            _context.Entry(entity).Property("xmin").OriginalValue = currentToken;
            entity.Update(intentJson, actorId, nowUtc);
        }

        var detailsJson = JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["permission"] = AuthorizationPolicies.NetworkConfigAuthor,
            ["correlationId"] = _correlation.CorrelationId,
            ["vlanCount"] = vlanCatalogue.Count,
            ["portIntentCount"] = portIntents.Count,
        });
        var envelope = new AuditEventEnvelope(
            actorType, actorId, "network-intent.saved", "rack-network-intent", entity.Id.ToString(),
            _correlation.CorrelationId, "success", RackId: rackId, DetailsJson: detailsJson);
        _auditOutbox.Add(_context, envelope, nowUtc);

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return StaleIntentConflict(rackId);
        }

        SetIntentETag(entity);
        return Ok(NetworkIntentContractMappers.ToDto(entity));
    }

    /// <summary>
    /// Server-side pre-validation stub (story #176, NFR5): runs the exact same
    /// <see cref="NetworkIntentValidator.Validate"/> method the PUT save path uses and persists nothing.
    /// Full pre-flight validation cross-checked against live discovered inventory is story #170.
    /// </summary>
    [HttpPost("validate")]
    [Authorize(Policy = AuthorizationPolicies.NetworkConfigAuthor)]
    [ProducesResponseType(typeof(NetworkIntentValidationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<NetworkIntentValidationResponse>> Validate(
        Guid rackId, [FromBody] NetworkIntentSaveRequest? request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return ValidationError((nameof(request), "A request body is required."));
        }

        if (await CheckRackAccessAsync(rackId, cancellationToken) is { } denied)
        {
            return denied;
        }

        if (!await _context.RackExistsAsync(rackId, cancellationToken))
        {
            return RackNotFound(rackId);
        }

        var (vlanCatalogue, portIntents) = NetworkIntentContractMappers.FromRequest(request);
        var errors = NetworkIntentValidator.Validate(vlanCatalogue, portIntents);
        var response = new NetworkIntentValidationResponse(
            errors.Count == 0,
            errors.Select(e => new NetworkIntentValidationErrorDto(e.Field, e.Message)).ToList());
        return Ok(response);
    }

    /// <summary>Runs the shared validator and, on any error, returns the 400 ActionResult to short-circuit with.</summary>
    private ActionResult? FieldErrors(
        IReadOnlyList<VlanCatalogueEntry> vlanCatalogue, IReadOnlyList<PortAccessIntent> portIntents)
    {
        var errors = NetworkIntentValidator.Validate(vlanCatalogue, portIntents);
        if (errors.Count == 0)
        {
            return null;
        }

        foreach (var (field, message) in errors)
        {
            ModelState.AddModelError(field, message);
        }

        return ValidationProblem(ModelState);
    }

    private void SetIntentETag(RackNetworkIntent entity)
        => Response.GetTypedHeaders().ETag = new EntityTagHeaderValue($"\"{GetXmin(entity)}\"", isWeak: true);

    private uint? ParseIfMatchToken()
    {
        var ifMatch = Request.GetTypedHeaders().IfMatch;
        if (ifMatch is not { Count: > 0 })
        {
            return null;
        }

        var tag = ifMatch[0].Tag.Value?.Trim('"');
        return uint.TryParse(tag, out var value) ? value : null;
    }

    private uint GetXmin(RackNetworkIntent entity)
        => (uint)_context.Entry(entity).Property("xmin").CurrentValue!;

    private ObjectResult StaleIntentConflict(Guid rackId)
    {
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status409Conflict,
            Title = "Network intent changed elsewhere",
            Detail = $"The network intent for rack '{rackId}' was changed by someone else since it was " +
                      "last loaded. Reload the current state and reapply your changes.",
        };
        problem.Extensions["reasonCode"] = "stale-network-intent";
        return new ObjectResult(problem) { StatusCode = StatusCodes.Status409Conflict };
    }
}
