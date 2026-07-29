using Microsoft.AspNetCore.Mvc;

namespace Caisson.Api.Controllers;

/// <summary>
/// Shared helpers for the desired-state read controllers (story #62, AC4). Deliberately does NOT derive
/// from <see cref="ReadOnlyControllerBase"/>: these endpoints key on a string <c>rackSlug</c>, not the
/// Guid-keyed observed-state <c>Rack</c> registry, so there is no rack-access-policy check to reuse —
/// only <see cref="Paginate{T}"/>/<see cref="ValidationError"/> (same shape) and a rackSlug-specific
/// not-found helper.
/// </summary>
public abstract class DesiredStateControllerBase : ControllerBase
{
    protected static (List<T> Items, string? NextCursor) Paginate<T>(List<T> page, int limit, Func<T, string> cursorOf)
    {
        if (page.Count <= limit)
        {
            return (page, null);
        }

        var items = page.Take(limit).ToList();
        return (items, cursorOf(items[^1]));
    }

    protected ActionResult ValidationError((string Field, string Message) error)
    {
        ModelState.AddModelError(error.Field, error.Message);
        return ValidationProblem(ModelState);
    }

    protected ObjectResult DesiredRackNotFound(string rackSlug)
        => Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Desired state not found",
            detail: $"No active desired-state version exists for rack '{rackSlug}'.");
}
