using Microsoft.AspNetCore.Mvc;

namespace Caisson.Api.Controllers;

/// <summary>
/// Shared base for the read-only topology/audit controllers. Owns the concerns every paginated,
/// rack-scoped read endpoint repeats — keyset page trimming, a validation-error → 400 problem-details
/// path, and the canonical rack-not-found 404 — so the individual controllers stay focused on their
/// query and shaping logic and the three surfaces never drift.
/// </summary>
public abstract class ReadOnlyControllerBase : ControllerBase
{
    /// <summary>
    /// Trims a page that was over-fetched by one row to the requested <paramref name="limit"/> and, when a
    /// further page exists, computes its continuation cursor from the last returned item.
    /// </summary>
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

    /// <summary>Turns a (field, message) validation error into an RFC 7807 400 problem-details.</summary>
    protected ActionResult ValidationError((string Field, string Message) error)
    {
        ModelState.AddModelError(error.Field, error.Message);
        return ValidationProblem(ModelState);
    }

    /// <summary>The canonical rack-not-found 404 problem-details, shared across the read endpoints.</summary>
    protected ObjectResult RackNotFound(Guid rackId)
        => Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Rack not found",
            detail: $"Rack '{rackId}' does not exist.");
}
