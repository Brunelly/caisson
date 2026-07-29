using System.Security.Claims;
using System.Threading.Channels;
using Caisson.Api.Middleware;
using Caisson.Domain.Enums;

namespace Caisson.Api.Auditing;

/// <summary>
/// The off-request-path <see cref="IAuditEventWriter"/> (finding #5): every read/action audit write is
/// captured into a plain record and enqueued to <see cref="AuditEventBackgroundWriter"/> instead of
/// performing a synchronous <c>INSERT</c> on the request path. Actor/correlation resolution still happens
/// here, synchronously, because both depend on request-scoped state (<see cref="ClaimsPrincipal"/>,
/// <see cref="ICorrelationContext"/>) that is gone by the time the background writer's own DI scope runs.
/// Enqueueing never throws or blocks the caller — a full channel drops the write (logged) rather than
/// risk the read path failing because the audit trail is momentarily backed up (ADR: audit is now
/// eventually consistent, not synchronously durable, as a deliberate trade-off).
/// </summary>
public sealed class ChannelAuditEventWriter : IAuditEventWriter
{
    private readonly ChannelWriter<AuditWriteRequest> _writer;
    private readonly ICorrelationContext _correlation;
    private readonly TimeProvider _time;
    private readonly Microsoft.Extensions.Logging.ILogger<ChannelAuditEventWriter> _logger;

    public ChannelAuditEventWriter(
        ChannelWriter<AuditWriteRequest> writer,
        ICorrelationContext correlation,
        TimeProvider time,
        Microsoft.Extensions.Logging.ILogger<ChannelAuditEventWriter> logger)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _correlation = correlation ?? throw new ArgumentNullException(nameof(correlation));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task WriteReadAsync(
        ClaimsPrincipal user, Guid? rackId, string action, string targetType, string? targetId,
        CancellationToken cancellationToken)
        => WriteActionAsync(user, rackId, action, targetType, targetId, "success", cancellationToken);

    /// <inheritdoc />
    public Task WriteActionAsync(
        ClaimsPrincipal user, Guid? rackId, string action, string targetType, string? targetId,
        string result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        var (actorType, actorId) = ResolveActor(user);
        var request = new AuditWriteRequest(
            Guid.NewGuid(), _time.GetUtcNow().UtcDateTime, actorType, actorId, action, targetType,
            _correlation.CorrelationId, result, rackId, targetId);

        if (!_writer.TryWrite(request))
        {
            _logger.LogWarning(
                "Audit event channel is full; dropping event action={Action} correlationId={CorrelationId}.",
                action, request.CorrelationId);
        }

        return Task.CompletedTask;
    }

    private static (ActorType ActorType, string ActorId) ResolveActor(ClaimsPrincipal user)
    {
        var actorId =
            user.FindFirstValue("oid")
            ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? user.FindFirstValue("sub")
            ?? user.Identity?.Name
            ?? "unknown";

        var actorType = user.IsInRole(Security.CaissonRoles.ServiceAccount) ? ActorType.ServiceAccount : ActorType.User;
        return (actorType, actorId);
    }
}

/// <summary>A captured audit write, queued for the background writer — plain data, no request-scoped dependencies.</summary>
public sealed record AuditWriteRequest(
    Guid Id,
    DateTime OccurredAtUtc,
    ActorType ActorType,
    string ActorId,
    string Action,
    string TargetType,
    Guid CorrelationId,
    string Result,
    Guid? RackId,
    string? TargetId);
