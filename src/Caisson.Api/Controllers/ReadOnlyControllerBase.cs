using Caisson.Api.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

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

    /// <summary>
    /// The per-rack access seam (finding #29): returns the 404 result to short-circuit with when
    /// <see cref="IRackAccessPolicy"/> denies access, or <c>null</c> when the caller may proceed.
    /// Resolved via <see cref="HttpContext"/> rather than constructor injection so every existing
    /// controller derived from this base picks it up without a constructor-signature change. A denial is
    /// surfaced as the same 404 as a missing rack (never 403), so rack existence is never an oracle.
    /// </summary>
    protected async Task<ObjectResult?> CheckRackAccessAsync(Guid rackId, CancellationToken cancellationToken)
    {
        var policy = HttpContext.RequestServices.GetRequiredService<IRackAccessPolicy>();
        return await policy.CanReadAsync(User, rackId, cancellationToken) ? null : RackNotFound(rackId);
    }
}
