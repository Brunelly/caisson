using System.Text.Json;
using Caisson.Api.Auditing;
using Caisson.Api.Middleware;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Caisson.Api.Security;

/// <summary>
/// Decorates the framework's default <see cref="AuthorizationMiddlewareResultHandler"/> with a structured
/// warning log AND the Tier 2 (durable-first-N + bounded counter) <c>authorization.forbidden</c> audit via
/// <see cref="IAuthorizationDenialAuditWriter"/> on every Forbidden authorization result (story #65 AC1;
/// story #68 AC3 — a 403 never reaches a controller, so no controller-level audit write ever fires for
/// it; story #308, ADR 0064 — durable-first-N replaces the prior best-effort channel write). This is
/// generic hardening — it applies to every policy in the API, not just
/// <see cref="AuthorizationPolicies.DriftApply"/> — and always delegates to the wrapped handler for the
/// actual response, so response behaviour is unchanged. A forbidden request must NEVER become a 500
/// because auditing failed; every step beyond the existing log line is best-effort and swallows its own
/// exceptions (matched by <see cref="IAuthorizationDenialAuditWriter"/>'s own best-effort contract).
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

            await TryRecordDenialAsync(context, policy, correlation?.CorrelationId);
        }

        await _inner.HandleAsync(next, context, policy, authorizeResult);
    }

    /// <summary>
    /// Never throws (delegates to <see cref="IAuthorizationDenialAuditWriter"/>'s own best-effort
    /// contract). Resolves the rack id from route values (present for every rack-scoped policy check),
    /// the STABLE <c>"{method} {routeTemplate}"</c> bucket key — never the raw path or query string, or an
    /// unauthorized caller could control bucket cardinality (story #308, ADR 0064) — and, ONLY for the
    /// <see cref="AuthorizationPolicies.DriftApply"/> policy AND only when a JSON body is present, peeks
    /// the not-yet-model-bound request body for <c>driftItemId</c> (via enable-buffering + read-and-reset,
    /// so the body is still intact for whatever runs next — which for a Forbidden result is nothing, but
    /// this must never assume that).
    /// </summary>
    private static async Task TryRecordDenialAsync(HttpContext context, AuthorizationPolicy policy, Guid? correlationId)
    {
        var writer = context.RequestServices.GetService<IAuthorizationDenialAuditWriter>();
        if (writer is null)
        {
            return;
        }

        var (actorType, actorId) = AuditActorResolver.Resolve(context.User);

        Guid? rackId = context.Request.RouteValues.TryGetValue("rackId", out var rackIdValue)
            && Guid.TryParse(rackIdValue?.ToString(), out var parsedRackId)
                ? parsedRackId
                : null;

        var driftItemId = await TryPeekDriftItemIdAsync(context, policy);

        var detailsJson = JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["endpoint"] = ResolveEndpoint(context),
            ["driftItemId"] = driftItemId,
        });

        await writer.RecordDenialAsync(
            actorType, actorId, ResolveEndpoint(context), "403", rackId,
            correlationId ?? Guid.Empty, detailsJson, context.RequestAborted);
    }

    /// <summary>
    /// The STABLE Tier 2 bucket key: <c>"{httpMethod} {routeTemplate}"</c>, resolved from the endpoint the
    /// routing middleware already matched (authorization always runs after routing). Falls back to the raw
    /// path only in the defensive case no endpoint/route pattern is available — which never happens for a
    /// matched, policy-checked request in practice.
    /// </summary>
    private static string ResolveEndpoint(HttpContext context)
    {
        var routeTemplate = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? context.Request.Path.ToString();
        return $"{context.Request.Method} {routeTemplate}";
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
