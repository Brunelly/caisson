using Caisson.Api.Auditing;
using Caisson.Api.Contracts;
using Caisson.Api.Security;
using Caisson.Infrastructure.Persistence;
using Caisson.Infrastructure.Persistence.Queries;
using Caisson.Infrastructure.Persistence.Shaping;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Caisson.Api.Controllers;

/// <summary>
/// Read-only, keyset-paginated diagnostics for desired-state ingestion (story #62, AC2/AC4): every
/// ingestion run, and validation errors optionally scoped to one run. GET-only, guarded by
/// <see cref="AuthorizationPolicies.TopologyRead"/>. Reuses <c>RequestPaging</c>/<c>CursorCodec</c>
/// exactly as <c>DiscoveryJobsController.List</c> does, binding the cursor's rack slot to
/// <see cref="Guid.Empty"/> (these endpoints are not rack-scoped) with a distinct endpoint string per
/// list so a cursor can never be replayed across the two.
/// </summary>
[ApiController]
[Route("api/desired-state")]
[Authorize(Policy = AuthorizationPolicies.TopologyRead)]
[Produces("application/json")]
public sealed class DesiredStateIngestionRunsController : DesiredStateControllerBase
{
    private readonly CaissonDbContext _context;
    private readonly IAuditEventWriter _audit;

    public DesiredStateIngestionRunsController(CaissonDbContext context, IAuditEventWriter audit)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    /// <summary>A keyset page of ingestion runs, newest-first.</summary>
    [HttpGet("ingestion-runs")]
    [ProducesResponseType(typeof(PagedResult<DesiredStateIngestionRunSummaryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResult<DesiredStateIngestionRunSummaryDto>>> ListRuns(
        [FromQuery] string? cursor, [FromQuery] int? pageSize, CancellationToken cancellationToken)
    {
        const string endpoint = "desired-state-ingestion-runs";
        if (!RequestPaging.TryResolve(pageSize, cursor, Guid.Empty, endpoint, out var limit, out var after, out var error))
        {
            return ValidationError(error!.Value);
        }

        var page = await _context.IngestionRunsPageAsync(after, limit + 1, cancellationToken);
        var (items, next) = Paginate(page, limit, r => CursorCodec.Encode(r.StartedAtUtc, r.Id, Guid.Empty, endpoint));

        await _audit.WriteReadAsync(User, rackId: null, "desired-state.ingestion-runs.read", "desired-state-ingestion-run", null, cancellationToken);
        return Ok(new PagedResult<DesiredStateIngestionRunSummaryDto>(
            items.Select(DesiredStateContractMappers.ToRunSummary).ToList(), next));
    }

    /// <summary>A keyset page of validation errors, newest-first, optionally scoped to one run.</summary>
    [HttpGet("validation-errors")]
    [ProducesResponseType(typeof(PagedResult<DesiredStateValidationErrorDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResult<DesiredStateValidationErrorDto>>> ListValidationErrors(
        [FromQuery] Guid? runId, [FromQuery] string? cursor, [FromQuery] int? pageSize,
        CancellationToken cancellationToken)
    {
        const string endpoint = "desired-state-validation-errors";
        if (!RequestPaging.TryResolve(pageSize, cursor, Guid.Empty, endpoint, out var limit, out var after, out var error))
        {
            return ValidationError(error!.Value);
        }

        var page = await _context.ValidationErrorsPageAsync(runId, after, limit + 1, cancellationToken);
        var (items, next) = Paginate(page, limit, e => CursorCodec.Encode(e.CreatedAtUtc, e.Id, Guid.Empty, endpoint));

        await _audit.WriteReadAsync(
            User, rackId: null, "desired-state.validation-errors.read", "desired-state-ingestion-run",
            runId?.ToString(), cancellationToken);
        return Ok(new PagedResult<DesiredStateValidationErrorDto>(
            items.Select(DesiredStateContractMappers.ToValidationError).ToList(), next));
    }
}
