using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Caisson.Api.Controllers;

/// <summary>
/// Shared helpers for the desired-state read controllers (story #62 AC4, #63 AC2/AC3). Deliberately does
/// NOT derive from <see cref="ReadOnlyControllerBase"/>: these endpoints key on a string
/// <c>rackSlug</c>, not the Guid-keyed observed-state <c>Rack</c> registry, so there is no
/// rack-access-policy check to reuse — only <see cref="Paginate{T}"/>/<see cref="ValidationError"/> (same
/// shape), rackSlug-specific not-found helpers carrying a machine-readable <c>code</c> extension (AC2),
/// and a strong-ETag helper (AC2).
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
        => NotFoundWithCode(
            "DESIRED_STATE_NOT_FOUND", "Desired state not found",
            $"No active desired-state version exists for rack '{rackSlug}'.");

    protected ObjectResult DesiredRevisionNotFound(string rackSlug, string identifierDescription)
        => NotFoundWithCode(
            "DESIRED_STATE_REVISION_NOT_FOUND", "Desired-state revision not found",
            $"No desired-state revision {identifierDescription} exists for rack '{rackSlug}'.");

    /// <summary>Sets a strong <c>ETag</c> response header derived from a revision's content hash (AC2).</summary>
    protected void SetContentHashETag(string contentHash)
        => Response.GetTypedHeaders().ETag = new EntityTagHeaderValue($"\"{contentHash}\"");

    /// <summary>
    /// <c>true</c> when the request's <c>If-None-Match</c> header already carries the current strong ETag
    /// (or <c>*</c>) — the caller should answer 304 rather than re-sending the body (AC2).
    /// </summary>
    protected bool IsNotModified(string contentHash)
    {
        var ifNoneMatch = Request.GetTypedHeaders().IfNoneMatch;
        if (ifNoneMatch is null || ifNoneMatch.Count == 0)
        {
            return false;
        }

        var etag = new EntityTagHeaderValue($"\"{contentHash}\"");
        foreach (var candidate in ifNoneMatch)
        {
            if (candidate.Equals(EntityTagHeaderValue.Any)
                || (!candidate.IsWeak && candidate.Tag.Equals(etag.Tag, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    private ObjectResult NotFoundWithCode(string code, string title, string detail)
    {
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status404NotFound,
            Title = title,
            Detail = detail,
        };
        problem.Extensions["code"] = code;
        return new ObjectResult(problem) { StatusCode = StatusCodes.Status404NotFound };
    }
}
