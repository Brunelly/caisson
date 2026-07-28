using System.Security.Claims;
using Caisson.Api.Security;
using Microsoft.AspNetCore.SignalR;

namespace Caisson.Api.Realtime.Hubs;

/// <summary>
/// A hub filter that logs every inbound hub invocation for the audit trail (story #9, AC3 — "an audit
/// log entry is emitted for unexpected invocation attempts"). Any method a client invokes — including one
/// that does not exist on the hub — is logged with the caller's identity and connection before dispatch.
/// </summary>
public sealed class TopologyHubLoggingFilter : IHubFilter
{
    private readonly ILogger<TopologyHubLoggingFilter> _logger;

    public TopologyHubLoggingFilter(ILogger<TopologyHubLoggingFilter> logger)
        => _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        ArgumentNullException.ThrowIfNull(invocationContext);
        ArgumentNullException.ThrowIfNull(next);

        var user = invocationContext.Context.User;
        _logger.LogInformation(
            "Topology hub invocation method={Method} connectionId={ConnectionId} user={User} roles={Roles}",
            invocationContext.HubMethodName,
            invocationContext.Context.ConnectionId,
            user?.FindFirstValue("oid") ?? user?.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown",
            user is null ? string.Empty : string.Join(',', user.FindAll(RoleClaimsTransformation.RoleClaimType).Select(c => c.Value)));

        return await next(invocationContext);
    }
}
