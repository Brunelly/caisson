using System.Security.Claims;
using Caisson.Api.Middleware;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Caisson.Api.Security;

/// <summary>
/// Decorates the framework's default <see cref="AuthorizationMiddlewareResultHandler"/> with a structured
/// warning log on every Forbidden authorization result (story #65, AC1: "an authorization failure is
/// logged with correlation ID"). This is generic hardening — it applies to every policy in the API, not
/// just <see cref="AuthorizationPolicies.DriftApply"/> — and always delegates to the wrapped handler for
/// the actual response, so response behaviour is unchanged.
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
            var subject =
                context.User.FindFirstValue("oid")
                ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? context.User.FindFirstValue("sub")
                ?? context.User.Identity?.Name
                ?? "unknown";

            var correlation = context.RequestServices.GetService<ICorrelationContext>();

            _logger.LogWarning(
                "Authorization forbidden subject={Subject} path={Path} correlationId={CorrelationId}",
                subject, context.Request.Path, correlation?.CorrelationId);
        }

        await _inner.HandleAsync(next, context, policy, authorizeResult);
    }
}
