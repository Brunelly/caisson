using Microsoft.AspNetCore.Mvc;

namespace Caisson.Api.Controllers;

/// <summary>
/// Shared helpers for the discovery orchestration controllers. Deliberately does NOT derive from
/// <see cref="ReadOnlyControllerBase"/>: discovery controllers carry policy-gated non-GET actions, and
/// the read-only guard test keys "GET-only" off <see cref="ReadOnlyControllerBase"/> membership. The
/// read-only safety boundary is about drivers/HTTP-writes-to-devices, not control-plane verbs (ADR 0013).
/// </summary>
public abstract class DiscoveryControllerBase : ControllerBase
{
    /// <summary>Splits an over-fetched page into items + a next cursor.</summary>
    protected static (List<T> Items, string? NextCursor) Paginate<T>(
        List<T> page, int limit, Func<T, string> cursorOf)
    {
        if (page.Count <= limit)
        {
            return (page, null);
        }

        var items = page.Take(limit).ToList();
        return (items, cursorOf(items[^1]));
    }

    /// <summary>Returns a 400 ProblemDetails for an invalid field.</summary>
    protected ActionResult ValidationError((string Field, string Message) error)
    {
        ModelState.AddModelError(error.Field, error.Message);
        return ValidationProblem(ModelState);
    }

    /// <summary>Returns a 404 ProblemDetails for an unknown rack.</summary>
    protected ObjectResult RackNotFound(Guid rackId)
        => Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Rack not found",
            detail: $"Rack '{rackId}' does not exist.");
}
