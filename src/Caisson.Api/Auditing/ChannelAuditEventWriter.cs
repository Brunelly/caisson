using System.Security.Claims;
using System.Threading.Channels;
using Caisson.Api.Middleware;
using Caisson.Domain.Enums;

namespace Caisson.Api.Auditing;

/// <summary>
/// The off-request-path Tier 3 (best-effort) audit writer (finding #5; story #308, ADR 0064): every
/// read/action audit write is captured into a plain record and enqueued to
/// <see cref="AuditEventBackgroundWriter"/> instead of performing a synchronous <c>INSERT</c> on the
/// request path. Actor/correlation resolution still happens here, synchronously, because both depend on
/// request-scoped state (<see cref="ClaimsPrincipal"/>, <see cref="ICorrelationContext"/>) that is gone by
/// the time the background writer's own DI scope runs. Enqueueing never throws or blocks the caller — a
/// full channel drops the write (logged) rather than risk the read path failing because the audit trail
/// is momentarily backed up.
/// <para>
/// Implements both the legacy <see cref="IAuditEventWriter"/> and the explicit <see cref="IBestEffortAuditEventWriter"/>
/// during the migration to explicit tiers (ADR 0064) — every current call site is on the legacy
/// interface; new/reclassified call sites resolve <see cref="IBestEffortAuditEventWriter"/> directly.
/// </para>
/// </summary>
public sealed class ChannelAuditEventWriter : IAuditEventWriter, IBestEffortAuditEventWriter
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
        string result, CancellationToken cancellationToken, string? detailsJson = null)
    {
        ArgumentNullException.ThrowIfNull(user);

        var (actorType, actorId) = AuditActorResolver.Resolve(user);
        var request = new AuditWriteRequest(
            Guid.NewGuid(), _time.GetUtcNow().UtcDateTime, actorType, actorId, action, targetType,
            _correlation.CorrelationId, result, rackId, targetId, detailsJson);

        if (!_writer.TryWrite(request))
        {
            _logger.LogWarning(
                "Tier3BestEffort audit event channel is full; dropping event action={Action} correlationId={CorrelationId}.",
                action, request.CorrelationId);
        }

        return Task.CompletedTask;
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
    string? TargetId,
    string? DetailsJson = null);
