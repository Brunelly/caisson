using System.Text.Json;
using Caisson.Api.Auditing;
using Caisson.Api.Contracts;
using Caisson.Api.Security;
using Caisson.Domain.Topology;
using Caisson.Infrastructure.Persistence;
using Caisson.Infrastructure.Persistence.Ingestion;
using Caisson.Infrastructure.Persistence.Queries;
using Caisson.Infrastructure.Persistence.Shaping;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Caisson.Api.Controllers;

/// <summary>
/// Read-only snapshot query endpoints (AC1/AC3, NFR1): latest snapshot, paginated history, snapshot
/// detail, the topology graph, and live drift between two snapshots. Every action is GET-only and
/// guarded by the <see cref="AuthorizationPolicies.TopologyRead"/> policy.
/// </summary>
[ApiController]
[Route("api/racks/{rackId:guid}/topology")]
[Authorize(Policy = AuthorizationPolicies.TopologyRead)]
[Produces("application/json")]
public sealed class RackTopologyController : ControllerBase
{
    private readonly CaissonDbContext _context;
    private readonly IAuditEventWriter _audit;

    public RackTopologyController(CaissonDbContext context, IAuditEventWriter audit)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    /// <summary>Returns the latest snapshot for a rack, with its topology graph.</summary>
    [HttpGet("snapshots/latest")]
    [ProducesResponseType(typeof(SnapshotDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SnapshotDetailDto>> GetLatest(Guid rackId, CancellationToken cancellationToken)
    {
        var snapshot = await _context.LatestSnapshotWithGraphAsync(rackId, cancellationToken);
        if (snapshot is null)
        {
            return await NotFoundSnapshotAsync(rackId, cancellationToken);
        }

        await _audit.WriteReadAsync(User, rackId, "topology.latest.read", "snapshot", snapshot.Id.ToString(), cancellationToken);
        return Ok(ToDetail(snapshot));
    }

    /// <summary>Returns a paginated snapshot history for a rack, newest-first.</summary>
    [HttpGet("snapshots")]
    [ProducesResponseType(typeof(PagedResult<SnapshotMetadataDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PagedResult<SnapshotMetadataDto>>> GetHistory(
        Guid rackId,
        [FromQuery] string? cursor,
        [FromQuery] int? pageSize,
        CancellationToken cancellationToken)
    {
        if (!RequestPaging.TryResolve(pageSize, cursor, out var limit, out var after, out var error))
        {
            return ValidationProblem(error!.Value);
        }

        if (!await _context.RackExistsAsync(rackId, cancellationToken))
        {
            return RackNotFound(rackId);
        }

        var page = await _context.SnapshotHistoryPageAsync(rackId, after, limit + 1, cancellationToken);
        var (items, next) = Paginate(page, limit, s => CursorCodec.Encode(s.CreatedAtUtc, s.Id));

        await _audit.WriteReadAsync(User, rackId, "topology.history.read", "rack", rackId.ToString(), cancellationToken);
        return Ok(new PagedResult<SnapshotMetadataDto>(items.Select(ContractMappers.ToMetadata).ToList(), next));
    }

    /// <summary>Returns a specific snapshot for a rack, with its topology graph.</summary>
    [HttpGet("snapshots/{snapshotId:guid}")]
    [ProducesResponseType(typeof(SnapshotDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SnapshotDetailDto>> GetById(
        Guid rackId, Guid snapshotId, CancellationToken cancellationToken)
    {
        var snapshot = await _context.SnapshotWithGraphAsync(rackId, snapshotId, cancellationToken);
        if (snapshot is null)
        {
            return SnapshotNotFound(rackId, snapshotId);
        }

        await _audit.WriteReadAsync(User, rackId, "topology.snapshot.read", "snapshot", snapshotId.ToString(), cancellationToken);
        return Ok(ToDetail(snapshot));
    }

    /// <summary>Returns the topology graph for the latest snapshot.</summary>
    [HttpGet("snapshots/latest/graph")]
    [ProducesResponseType(typeof(TopologyGraphDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TopologyGraphDto>> GetLatestGraph(Guid rackId, CancellationToken cancellationToken)
    {
        var snapshot = await _context.LatestSnapshotWithGraphAsync(rackId, cancellationToken);
        if (snapshot is null)
        {
            return await NotFoundSnapshotAsync(rackId, cancellationToken);
        }

        await _audit.WriteReadAsync(User, rackId, "topology.graph.read", "snapshot", snapshot.Id.ToString(), cancellationToken);
        return Ok(ContractMappers.ToGraph(TopologyGraphProjector.Project(snapshot)));
    }

    /// <summary>Returns the topology graph for a specific snapshot.</summary>
    [HttpGet("snapshots/{snapshotId:guid}/graph")]
    [ProducesResponseType(typeof(TopologyGraphDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TopologyGraphDto>> GetGraph(
        Guid rackId, Guid snapshotId, CancellationToken cancellationToken)
    {
        var snapshot = await _context.SnapshotWithGraphAsync(rackId, snapshotId, cancellationToken);
        if (snapshot is null)
        {
            return SnapshotNotFound(rackId, snapshotId);
        }

        await _audit.WriteReadAsync(User, rackId, "topology.graph.read", "snapshot", snapshotId.ToString(), cancellationToken);
        return Ok(ContractMappers.ToGraph(TopologyGraphProjector.Project(snapshot)));
    }

    /// <summary>Computes the drift between two snapshots of a rack live (AC3).</summary>
    [HttpGet("diff")]
    [ProducesResponseType(typeof(SnapshotDiffDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SnapshotDiffDto>> GetDiff(
        Guid rackId,
        [FromQuery] Guid from,
        [FromQuery] Guid to,
        CancellationToken cancellationToken)
    {
        var fromSnapshot = await _context.SnapshotWithGraphAsync(rackId, from, cancellationToken);
        if (fromSnapshot is null)
        {
            return SnapshotNotFound(rackId, from);
        }

        var toSnapshot = await _context.SnapshotWithGraphAsync(rackId, to, cancellationToken);
        if (toSnapshot is null)
        {
            return SnapshotNotFound(rackId, to);
        }

        var result = TopologyDiffCalculator.Diff(
            fromSnapshot, toSnapshot, toSnapshot.CorrelationId, DateTime.UtcNow, Guid.NewGuid);

        await _audit.WriteReadAsync(User, rackId, "topology.diff.read", "snapshot", to.ToString(), cancellationToken);

        using var counts = JsonDocument.Parse(result.ChangeCountsJson);
        return Ok(new SnapshotDiffDto(
            from, to, counts.RootElement.Clone(),
            result.Diffs.Select(ContractMappers.ToLiveDiff).ToList()));
    }

    private static SnapshotDetailDto ToDetail(TopologySnapshot snapshot)
        => new(ContractMappers.ToMetadata(snapshot), ContractMappers.ToGraph(TopologyGraphProjector.Project(snapshot)));

    private static (List<T> Items, string? NextCursor) Paginate<T>(
        List<T> page, int limit, Func<T, string> cursorOf)
    {
        if (page.Count <= limit)
        {
            return (page, null);
        }

        var items = page.Take(limit).ToList();
        return (items, cursorOf(items[^1]));
    }

    private async Task<ObjectResult> NotFoundSnapshotAsync(Guid rackId, CancellationToken cancellationToken)
    {
        return await _context.RackExistsAsync(rackId, cancellationToken)
            ? Problem(statusCode: StatusCodes.Status404NotFound, title: "No snapshots", detail: $"Rack '{rackId}' has no snapshots yet.")
            : RackNotFound(rackId);
    }

    private ObjectResult RackNotFound(Guid rackId)
        => Problem(statusCode: StatusCodes.Status404NotFound, title: "Rack not found", detail: $"Rack '{rackId}' does not exist.");

    private ObjectResult SnapshotNotFound(Guid rackId, Guid snapshotId)
        => Problem(statusCode: StatusCodes.Status404NotFound, title: "Snapshot not found", detail: $"Snapshot '{snapshotId}' was not found for rack '{rackId}'.");

    private ActionResult ValidationProblem((string Field, string Message) error)
    {
        ModelState.AddModelError(error.Field, error.Message);
        return ValidationProblem(ModelState);
    }
}
