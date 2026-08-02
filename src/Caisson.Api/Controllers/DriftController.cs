using Caisson.Api.Auditing;
using Caisson.Api.Contracts;
using Caisson.Api.Security;
using Caisson.Domain.Drift;
using Caisson.Domain.Enums;
using Caisson.Infrastructure.Persistence;
using Caisson.Infrastructure.Persistence.Queries;
using Caisson.Infrastructure.Persistence.Shaping;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Caisson.Api.Controllers;

/// <summary>
/// Read-only, RBAC-protected drift query endpoints (story #64, AC5): latest drift, drift history, a
/// specific report's detail, and a single item's detail. Every action is GET-only and guarded by the
/// <see cref="AuthorizationPolicies.TopologyRead"/> policy — mirrors <see cref="RackTopologyController"/>'s
/// shape exactly (Guid <c>rackId</c> routing, so <c>CheckRackAccessAsync</c>/<c>RackNotFound</c>/
/// <c>Paginate</c> apply directly).
/// </summary>
[ApiController]
[Route("api/racks/{rackId:guid}/drift")]
[Authorize(Policy = AuthorizationPolicies.TopologyRead)]
[Produces("application/json")]
public sealed class DriftController : ReadOnlyControllerBase
{
    private readonly CaissonDbContext _context;
    private readonly IBestEffortAuditEventWriter _audit;

    public DriftController(CaissonDbContext context, IBestEffortAuditEventWriter audit)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    /// <summary>Returns the latest drift report for a rack, with a page of its items.</summary>
    [HttpGet("latest")]
    [ProducesResponseType(typeof(DriftReportDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DriftReportDetailDto>> GetLatest(
        Guid rackId,
        [FromQuery] string? cursor,
        [FromQuery] int? pageSize,
        CancellationToken cancellationToken)
    {
        const string endpoint = "drift.latest.items";
        if (!RequestPaging.TryResolve(pageSize, cursor, rackId, endpoint, out var limit, out var after, out var error))
        {
            return ValidationError(error!.Value);
        }

        if (await CheckRackAccessAsync(rackId, cancellationToken) is { } denied)
        {
            return denied;
        }

        var report = await _context.LatestReportForRackAsync(rackId, cancellationToken);
        if (report is null)
        {
            return await NoDriftYetAsync(rackId, cancellationToken);
        }

        var detail = await BuildDetailAsync(report, severity: null, driftType: null, actionable: null, after, limit, endpoint, rackId, cancellationToken);

        await _audit.WriteReadAsync(User, rackId, "drift.latest.read", "drift-report", report.Id.ToString(), cancellationToken);
        return Ok(detail);
    }

    /// <summary>Returns a paginated drift-report history for a rack, newest-first.</summary>
    [HttpGet("history")]
    [ProducesResponseType(typeof(PagedResult<DriftReportSummaryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PagedResult<DriftReportSummaryDto>>> GetHistory(
        Guid rackId,
        [FromQuery] string? cursor,
        [FromQuery] int? pageSize,
        CancellationToken cancellationToken)
    {
        const string endpoint = "drift.history";
        if (!RequestPaging.TryResolve(pageSize, cursor, rackId, endpoint, out var limit, out var after, out var error))
        {
            return ValidationError(error!.Value);
        }

        if (await CheckRackAccessAsync(rackId, cancellationToken) is { } denied)
        {
            return denied;
        }

        if (!await _context.RackExistsAsync(rackId, cancellationToken))
        {
            return RackNotFound(rackId);
        }

        var page = await _context.ReportHistoryPageAsync(rackId, after, limit + 1, cancellationToken);
        var (items, next) = Paginate(page, limit, r => CursorCodec.Encode(r.ComputedAtUtc, r.Id, rackId, endpoint));

        await _audit.WriteReadAsync(User, rackId, "drift.history.read", "rack", rackId.ToString(), cancellationToken);
        return Ok(new PagedResult<DriftReportSummaryDto>(items.Select(DriftContractMappers.ToSummary).ToList(), next));
    }

    /// <summary>
    /// Returns a specific drift report for a rack, with a filtered, paginated page of its items. 404s
    /// when the report does not exist, does not belong to this rack, or has since been pruned by
    /// retention (indistinguishable from a never-existing id — retention pruning is a normal operational
    /// outcome, not an error condition callers can query around).
    /// </summary>
    [HttpGet("reports/{driftReportId:guid}")]
    [ProducesResponseType(typeof(DriftReportDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DriftReportDetailDto>> GetReportById(
        Guid rackId,
        Guid driftReportId,
        [FromQuery] DriftSeverity? severity,
        [FromQuery] DriftType? driftType,
        [FromQuery] bool? actionable,
        [FromQuery] string? cursor,
        [FromQuery] int? pageSize,
        CancellationToken cancellationToken)
    {
        const string endpoint = "drift.report.items";
        if (!RequestPaging.TryResolve(pageSize, cursor, rackId, endpoint, out var limit, out var after, out var error))
        {
            return ValidationError(error!.Value);
        }

        if (await CheckRackAccessAsync(rackId, cancellationToken) is { } denied)
        {
            return denied;
        }

        var report = await _context.ReportByIdAsync(rackId, driftReportId, cancellationToken);
        if (report is null)
        {
            return ReportNotFound(rackId, driftReportId);
        }

        var detail = await BuildDetailAsync(report, severity, driftType, actionable, after, limit, endpoint, rackId, cancellationToken);

        await _audit.WriteReadAsync(User, rackId, "drift.report.read", "drift-report", report.Id.ToString(), cancellationToken);
        return Ok(detail);
    }

    /// <summary>
    /// Returns a single drift item's detail, resolved to the latest report containing it (ADR 0029). 404s
    /// when the item does not exist, does not belong to this rack, or its report has since been pruned by
    /// retention.
    /// </summary>
    [HttpGet("items/{driftItemId:guid}")]
    [ProducesResponseType(typeof(DriftItemDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DriftItemDto>> GetItemById(Guid rackId, Guid driftItemId, CancellationToken cancellationToken)
    {
        if (await CheckRackAccessAsync(rackId, cancellationToken) is { } denied)
        {
            return denied;
        }

        var item = await _context.ItemByDriftItemIdAsync(rackId, driftItemId, cancellationToken);
        if (item is null)
        {
            return ItemNotFound(rackId, driftItemId);
        }

        await _audit.WriteReadAsync(User, rackId, "drift.item.read", "drift-item", driftItemId.ToString(), cancellationToken);
        return Ok(DriftContractMappers.ToItemDto(item));
    }

    private async Task<DriftReportDetailDto> BuildDetailAsync(
        DriftReport report,
        DriftSeverity? severity,
        DriftType? driftType,
        bool? actionable,
        KeysetPosition? after,
        int limit,
        string endpoint,
        Guid rackId,
        CancellationToken cancellationToken)
    {
        var page = await _context.ItemsPageAsync(report.Id, severity, driftType, actionable, after, limit + 1, cancellationToken);
        var (items, next) = Paginate(page, limit, i => CursorCodec.Encode(i.CreatedAtUtc, i.Id, rackId, endpoint));

        return new DriftReportDetailDto(
            DriftContractMappers.ToSummary(report),
            new PagedResult<DriftItemDto>(items.Select(DriftContractMappers.ToItemDto).ToList(), next));
    }

    private async Task<ObjectResult> NoDriftYetAsync(Guid rackId, CancellationToken cancellationToken)
    {
        return await _context.RackExistsAsync(rackId, cancellationToken)
            ? Problem(statusCode: StatusCodes.Status404NotFound, title: "No drift computed", detail: $"Rack '{rackId}' has no drift report yet.")
            : RackNotFound(rackId);
    }

    private ObjectResult ReportNotFound(Guid rackId, Guid driftReportId)
        => Problem(statusCode: StatusCodes.Status404NotFound, title: "Drift report not found", detail: $"Drift report '{driftReportId}' was not found for rack '{rackId}'.");

    private ObjectResult ItemNotFound(Guid rackId, Guid driftItemId)
        => Problem(statusCode: StatusCodes.Status404NotFound, title: "Drift item not found", detail: $"Drift item '{driftItemId}' was not found for rack '{rackId}'.");
}
