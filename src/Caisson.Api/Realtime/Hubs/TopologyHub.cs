using System.Security.Claims;
using Caisson.Api.Auditing;
using Caisson.Api.Middleware;
using Caisson.Api.Security;
using Caisson.Infrastructure.LiveUpdates;
using Caisson.Infrastructure.Persistence;
using Caisson.Infrastructure.Persistence.Queries;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Caisson.Api.Realtime.Hubs;

/// <summary>
/// The authenticated, strictly READ-ONLY topology hub (story #9, AC3, ADR 0014). It is gated by the
/// existing <see cref="AuthorizationPolicies.TopologyRead"/> policy — the same "ReadOnly and above" gate
/// as the query APIs, so RBAC is single-sourced. Clients only <b>receive</b> events; the only invokable
/// server methods are <see cref="SubscribeToRack"/>/<see cref="UnsubscribeFromRack"/>, which are pure
/// SignalR group mechanics and never mutate state or trigger discovery. Rack-scoping is role-gate +
/// rack-existence (there is no per-rack ACL in this codebase; that is deferred behind the same seam).
/// </summary>
[Authorize(Policy = AuthorizationPolicies.TopologyRead)]
public sealed class TopologyHub : Hub<ITopologyClient>
{
    private const string CorrelationItemKey = "caisson.correlationId";

    private readonly CaissonDbContext _context;
    private readonly IAuditEventWriter _audit;
    private readonly ICorrelationContext _correlation;
    private readonly TopologyMetrics _metrics;
    private readonly ILogger<TopologyHub> _logger;

    public TopologyHub(
        CaissonDbContext context,
        IAuditEventWriter audit,
        ICorrelationContext correlation,
        TopologyMetrics metrics,
        ILogger<TopologyHub> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _correlation = correlation ?? throw new ArgumentNullException(nameof(correlation));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public override async Task OnConnectedAsync()
    {
        _metrics.RecordConnection(+1);
        _logger.LogInformation(
            "Topology hub connected connectionId={ConnectionId} correlationId={CorrelationId} user={User} roles={Roles}",
            Context.ConnectionId, CorrelationId(), UserId(), Roles());
        await base.OnConnectedAsync();
    }

    /// <inheritdoc />
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _metrics.RecordConnection(-1);
        _logger.LogInformation(
            "Topology hub disconnected connectionId={ConnectionId} correlationId={CorrelationId} user={User}",
            Context.ConnectionId, CorrelationId(), UserId());
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Joins the caller to the per-rack group after verifying the rack exists. On a missing rack it joins
    /// no group (fail-closed), writes an audit entry, and throws a client-safe <see cref="HubException"/>.
    /// </summary>
    public async Task SubscribeToRack(Guid rackId)
    {
        StampCorrelation();

        if (!await _context.RackExistsAsync(rackId, Context.ConnectionAborted))
        {
            // The rack does not exist, so it cannot be the audit's rack_id (a FK) — record the attempted
            // rack in targetId with a null rack_id, and join no group (fail-closed).
            await AuditAsync(auditRackId: null, rackId, "topology.hub.subscribe", "rack-not-found");
            _logger.LogWarning(
                "Topology hub subscribe rejected — rack not found rackId={RackId} connectionId={ConnectionId} correlationId={CorrelationId} user={User}",
                rackId, Context.ConnectionId, CorrelationId(), UserId());
            throw new HubException($"Rack '{rackId}' does not exist.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, TopologyGroups.ForRack(rackId), Context.ConnectionAborted);
        await AuditAsync(rackId, rackId, "topology.hub.subscribe", "success");
        _logger.LogInformation(
            "Topology hub subscribed rackId={RackId} connectionId={ConnectionId} correlationId={CorrelationId} user={User}",
            rackId, Context.ConnectionId, CorrelationId(), UserId());
    }

    /// <summary>Leaves the caller's per-rack group.</summary>
    public async Task UnsubscribeFromRack(Guid rackId)
    {
        StampCorrelation();

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, TopologyGroups.ForRack(rackId), Context.ConnectionAborted);
        // A client may unsubscribe from any id (including a non-existent rack), so keep rack_id null (the
        // FK) and record the target in targetId — unsubscribe is pure group mechanics.
        await AuditAsync(auditRackId: null, rackId, "topology.hub.unsubscribe", "success");
        _logger.LogInformation(
            "Topology hub unsubscribed rackId={RackId} connectionId={ConnectionId} correlationId={CorrelationId} user={User}",
            rackId, Context.ConnectionId, CorrelationId(), UserId());
    }

    private Task AuditAsync(Guid? auditRackId, Guid targetRackId, string action, string result)
        => _audit.WriteActionAsync(
            Context.User ?? new ClaimsPrincipal(new ClaimsIdentity()),
            auditRackId, action, targetType: "rack", targetId: targetRackId.ToString(), result, Context.ConnectionAborted);

    // Hub method invocations over an established WebSocket do not pass through the HTTP correlation
    // middleware, so stamp the per-connection id onto the scoped context the audit writer reads.
    private void StampCorrelation()
    {
        if (_correlation is CorrelationContext ctx)
        {
            ctx.CorrelationId = CorrelationId();
        }
    }

    private Guid CorrelationId()
    {
        if (Context.Items.TryGetValue(CorrelationItemKey, out var existing) && existing is Guid cached)
        {
            return cached;
        }

        var httpContext = Context.GetHttpContext();
        var id = httpContext is not null
            && httpContext.Request.Headers.TryGetValue(CorrelationIdMiddleware.HeaderName, out var provided)
            && Guid.TryParse(provided.ToString(), out var parsed)
                ? parsed
                : Guid.NewGuid();

        Context.Items[CorrelationItemKey] = id;
        return id;
    }

    private string UserId()
        => Context.User?.FindFirstValue("oid")
            ?? Context.User?.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? "unknown";

    private string Roles()
        => Context.User is { } user
            ? string.Join(',', user.FindAll(RoleClaimsTransformation.RoleClaimType).Select(c => c.Value))
            : string.Empty;
}
