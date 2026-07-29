using System.Text.Json;
using Caisson.Api.Auditing;
using Caisson.Api.Contracts;
using Caisson.Api.Middleware;
using Caisson.Api.Security;
using Caisson.Domain.Drift;
using Caisson.Domain.Enums;
using Caisson.Infrastructure.Persistence;
using Caisson.Infrastructure.Persistence.Queries;
using Caisson.Orchestration.DriftApply;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Caisson.Api.Controllers;

/// <summary>
/// The single-change drift-correction apply endpoint (story #65, AC1/AC2) — the FIRST write endpoint in
/// the API. Deliberately derives from <see cref="DiscoveryControllerBase"/>, not
/// <see cref="ReadOnlyControllerBase"/>: this is a policy-gated, non-GET, device-mutating action (ADR
/// 0013's precedent). Gated by the elevated <see cref="AuthorizationPolicies.DriftApply"/> permission —
/// distinct from, and NOT satisfied by, <see cref="CaissonRoles.Operator"/> alone.
/// </summary>
[ApiController]
[Route("api/racks/{rackId:guid}/drift")]
[Produces("application/json")]
public sealed class DriftApplyController : DiscoveryControllerBase
{
    private readonly CaissonDbContext _context;
    private readonly IDriftApplyJobService _jobs;
    private readonly IAuditEventWriter _audit;
    private readonly ICorrelationContext _correlation;

    public DriftApplyController(
        CaissonDbContext context, IDriftApplyJobService jobs, IAuditEventWriter audit, ICorrelationContext correlation)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _correlation = correlation ?? throw new ArgumentNullException(nameof(correlation));
    }

    /// <summary>
    /// Applies a single, already-computed drift correction (AC1/AC2). No job is ever created on a
    /// 401/403/400/404/422 path — the policy/rate-limit attributes and every validation check below run
    /// before <see cref="IDriftApplyJobService.RequestApplyAsync"/> is reached.
    /// </summary>
    [HttpPost("apply")]
    [Authorize(Policy = AuthorizationPolicies.DriftApply)]
    [EnableRateLimiting(RateLimitPolicies.DriftApply)]
    [ProducesResponseType(typeof(ApplyDriftCorrectionResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApplyDriftCorrectionResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApplyDriftCorrectionResponse>> Apply(
        Guid rackId, [FromBody] ApplyDriftCorrectionRequest? request, CancellationToken cancellationToken)
    {
        if (request is null || request.DriftItemId == Guid.Empty)
        {
            return ValidationError((nameof(ApplyDriftCorrectionRequest.DriftItemId), "driftItemId is required."));
        }

        if (await CheckRackAccessAsync(rackId, cancellationToken) is { } denied)
        {
            return denied;
        }

        if (!await _context.RackExistsAsync(rackId, cancellationToken))
        {
            return RackNotFound(rackId);
        }

        var item = await _context.ItemByDriftItemIdAsync(rackId, request.DriftItemId, cancellationToken);
        if (item is null)
        {
            return ItemNotFound(rackId, request.DriftItemId);
        }

        if (!IsSupported(item))
        {
            return UnsupportedDriftType(item);
        }

        var (actorType, actorId) = ResolveActor();
        var result = await _jobs.RequestApplyAsync(item, actorId, actorType, _correlation.CorrelationId, cancellationToken);

        var creationDetails = JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["permission"] = AuthorizationPolicies.DriftApply,
            ["correlationId"] = _correlation.CorrelationId,
            ["driftItemId"] = item.DriftItemId,
        });
        await _audit.WriteActionAsync(
            User, rackId, "drift.apply.job.created", "drift-apply-job", result.JobId.ToString(),
            result.Disposition.ToString(), cancellationToken, creationDetails);

        var body = new ApplyDriftCorrectionResponse(result.JobId);
        var location = $"/api/racks/{rackId}/jobs/{result.JobId}";
        return result.Disposition switch
        {
            RequestApplyDisposition.Created => Created(location, body),
            _ => Accepted(location, body),
        };
    }

    /// <summary>
    /// M1 supports exactly one change type: set access VLAN on a single switch port. Non-actionable items
    /// (e.g. an ambiguous <c>UnknownTopologyMapping</c>) are never eligible even if the type otherwise
    /// matched (AC2).
    /// </summary>
    private static bool IsSupported(DriftItem item)
        => item.DriftType == DriftType.AccessVlanMismatch && item.Actionable;

    private (ActorType ActorType, string ActorId) ResolveActor()
    {
        var actorId =
            User.FindFirst("oid")?.Value
            ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("sub")?.Value
            ?? User.Identity?.Name
            ?? "unknown";
        var actorType = User.IsInRole(CaissonRoles.ServiceAccount) ? ActorType.ServiceAccount : ActorType.User;
        return (actorType, actorId);
    }

    private ObjectResult ItemNotFound(Guid rackId, Guid driftItemId)
        => Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Drift item not found",
            detail: $"Drift item '{driftItemId}' was not found for rack '{rackId}'.");

    private ObjectResult UnsupportedDriftType(DriftItem item)
    {
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status422UnprocessableEntity,
            Title = "Unsupported drift type",
            Detail = $"Drift item '{item.DriftItemId}' has drift type '{item.DriftType}', which is not " +
                      "supported for single-change apply (only AccessVlanMismatch is supported in M1).",
        };
        problem.Extensions["reasonCode"] = "unsupported-drift-type";
        return new ObjectResult(problem) { StatusCode = StatusCodes.Status422UnprocessableEntity };
    }
}
