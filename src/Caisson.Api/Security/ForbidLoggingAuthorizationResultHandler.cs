using System.Text.Json;
using Caisson.Api.Auditing;
using Caisson.Api.Middleware;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Caisson.Api.Security;

/// <summary>
/// Decorates the framework's default <see cref="AuthorizationMiddlewareResultHandler"/> with a structured
/// warning log AND a persisted <c>authorization.forbidden</c> <see cref="Caisson.Domain.Topology.TopologyAuditEvent"/>
/// on every Forbidden authorization result (story #65 AC1; story #68 AC3 — a 403 never reaches a
/// controller, so no controller-level audit write ever fires for it). This is generic hardening — it
/// applies to every policy in the API, not just <see cref="AuthorizationPolicies.DriftApply"/> — and
/// always delegates to the wrapped handler for the actual response, so response behaviour is unchanged. A
/// forbidden request must NEVER become a 500 because auditing failed; every step beyond the existing log
/// line is best-effort and swallows its own exceptions.
/// </summary>
public sealed class ForbidLoggingAuthorizationResultHandler : Microsoft.AspNetCore.Authorization.IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _inner = new();
    private readonly ILogger<ForbidLoggingAuthorizationResultHandler> _logger;

    public ForbidLoggingAuthorizationResultHandler(ILogger<ForbidLoggingAuthorizationResultHandler> logger)
        => _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(authorizeResult);

        if (authorizeResult.Forbidden)
        {
            var subject = AuditActorResolver.ResolveActorId(context.User);

            var correlation = context.RequestServices.GetService<ICorrelationContext>();

            _logger.LogWarning(
                "Authorization forbidden subject={Subject} path={Path} correlationId={CorrelationId}",
                subject, context.Request.Path, correlation?.CorrelationId);

            await TryWriteAuditEventAsync(context, policy, correlation?.CorrelationId);
        }

        await _inner.HandleAsync(next, context, policy, authorizeResult);
    }

    /// <summary>
    /// Best-effort: never throws. Resolves the rack id from route values (present for every rack-scoped
    /// policy check) and, ONLY for the <see cref="AuthorizationPolicies.DriftApply"/> policy AND only when
    /// a JSON body is present, peeks the not-yet-model-bound request body for <c>driftItemId</c> (via
    /// enable-buffering + read-and-reset, so the body is still intact for whatever runs next — which for a
    /// Forbidden result is nothing, but this must never assume that).
    /// </summary>
    private static async Task TryWriteAuditEventAsync(HttpContext context, AuthorizationPolicy policy, Guid? correlationId)
    {
        try
        {
            var audit = context.RequestServices.GetService<IAuditEventWriter>();
            if (audit is null)
            {
                return;
            }

            Guid? rackId = context.Request.RouteValues.TryGetValue("rackId", out var rackIdValue)
                && Guid.TryParse(rackIdValue?.ToString(), out var parsedRackId)
                    ? parsedRackId
                    : null;

            var driftItemId = await TryPeekDriftItemIdAsync(context, policy);

            var detailsJson = JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["path"] = context.Request.Path.ToString(),
                ["correlationId"] = correlationId,
                ["driftItemId"] = driftItemId,
            });

            await audit.WriteActionAsync(
                context.User, rackId, "authorization.forbidden", "http-request", context.Request.Path.ToString(),
                "403", context.RequestAborted, detailsJson);
        }
        catch
        {
            // Best-effort only — a forbidden request must never become a 500 because auditing failed.
        }
    }

    private static async Task<Guid?> TryPeekDriftItemIdAsync(HttpContext context, AuthorizationPolicy policy)
    {
        if (!RequiresDriftApplyRole(policy))
        {
            return null;
        }

        if (context.Request.ContentType is null
            || !context.Request.ContentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            if (!context.Request.Body.CanSeek)
            {
                context.Request.EnableBuffering();
            }

            context.Request.Body.Position = 0;
            using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            context.Request.Body.Position = 0;

            return document.RootElement.TryGetProperty("driftItemId", out var idProperty) && idProperty.TryGetGuid(out var driftItemId)
                ? driftItemId
                : null;
        }
        catch
        {
            // Absent/malformed/non-JSON body — no driftItemId to report, never throw.
            return null;
        }
    }

    private static bool RequiresDriftApplyRole(AuthorizationPolicy policy)
        => policy.Requirements.OfType<RolesAuthorizationRequirement>()
            .Any(r => r.AllowedRoles.Contains(CaissonRoles.DriftApply, StringComparer.Ordinal));
}
