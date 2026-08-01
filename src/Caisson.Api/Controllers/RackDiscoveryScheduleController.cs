using Caisson.Api.Auditing;
using Caisson.Api.Contracts;
using Caisson.Api.Middleware;
using Caisson.Api.Security;
using Caisson.Domain.Discovery;
using Caisson.Infrastructure.Persistence;
using Caisson.Infrastructure.Persistence.Auditing;
using Caisson.Infrastructure.Persistence.Queries;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Caisson.Api.Controllers;

/// <summary>
/// View and manage a rack's recurring discovery schedule (story #8, AC3/AC4). GET is readable by any
/// recognised role; PUT is Admin-only (<see cref="AuthorizationPolicies.ScheduleManage"/>).
/// </summary>
[ApiController]
[Route("api/racks/{rackId:guid}/discovery-schedule")]
[Produces("application/json")]
public sealed class RackDiscoveryScheduleController : DiscoveryControllerBase
{
    private readonly CaissonDbContext _context;
    private readonly IBestEffortAuditEventWriter _audit;
    private readonly IMandatoryAuditOutbox _auditOutbox;
    private readonly ICorrelationContext _correlation;
    private readonly TimeProvider _time;

    public RackDiscoveryScheduleController(
        CaissonDbContext context, IBestEffortAuditEventWriter audit, IMandatoryAuditOutbox auditOutbox,
        ICorrelationContext correlation, TimeProvider time)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _auditOutbox = auditOutbox ?? throw new ArgumentNullException(nameof(auditOutbox));
        _correlation = correlation ?? throw new ArgumentNullException(nameof(correlation));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>Returns the rack's schedule (a disabled default when none is configured).</summary>
    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.TopologyRead)]
    [ProducesResponseType(typeof(DiscoveryScheduleDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DiscoveryScheduleDto>> Get(Guid rackId, CancellationToken cancellationToken)
    {
        if (!await _context.RackExistsAsync(rackId, cancellationToken))
        {
            return RackNotFound(rackId);
        }

        var schedule = await _context.RackDiscoverySchedules
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.RackId == rackId, cancellationToken);

        await _audit.WriteReadAsync(
            User, rackId, "discovery.schedule.read", "rack", rackId.ToString(), cancellationToken);
        return Ok(schedule is null
            ? new DiscoveryScheduleDto(rackId, false, 0, 0, null, null, null)
            : DiscoveryContractMappers.ToSchedule(schedule));
    }

    /// <summary>Creates or updates the rack's schedule (Admin only, AC4).</summary>
    [HttpPut]
    [Authorize(Policy = AuthorizationPolicies.ScheduleManage)]
    [ProducesResponseType(typeof(DiscoveryScheduleDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DiscoveryScheduleDto>> Put(
        Guid rackId, [FromBody] UpdateScheduleRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Enabled && request.IntervalSeconds < 1)
        {
            return ValidationError((nameof(request.IntervalSeconds), "intervalSeconds must be at least 1 when enabled."));
        }

        if (request.JitterSeconds < 0)
        {
            return ValidationError((nameof(request.JitterSeconds), "jitterSeconds must be zero or greater."));
        }

        if (!await _context.RackExistsAsync(rackId, cancellationToken))
        {
            return RackNotFound(rackId);
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var nextRun = request.Enabled ? now : (DateTime?)null;

        var schedule = await _context.RackDiscoverySchedules
            .FirstOrDefaultAsync(s => s.RackId == rackId, cancellationToken);
        if (schedule is null)
        {
            schedule = new RackDiscoverySchedule(
                rackId, request.Enabled, request.IntervalSeconds, request.JitterSeconds, nextRun);
            _context.RackDiscoverySchedules.Add(schedule);
        }
        else
        {
            schedule.Configure(request.Enabled, request.IntervalSeconds, request.JitterSeconds, nextRun);
        }

        var (actorType, actorId) = AuditActorResolver.Resolve(User);
        var envelope = new AuditEventEnvelope(
            actorType, actorId, "discovery.schedule.updated", "rack", rackId.ToString(),
            _correlation.CorrelationId, request.Enabled ? "enabled" : "disabled", RackId: rackId);
        _auditOutbox.Add(_context, envelope, now);

        await _context.SaveChangesAsync(cancellationToken);
        return Ok(DiscoveryContractMappers.ToSchedule(schedule));
    }
}
