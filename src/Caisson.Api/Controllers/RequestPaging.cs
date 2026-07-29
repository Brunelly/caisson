using Caisson.Infrastructure.Persistence.Shaping;

namespace Caisson.Api.Controllers;

/// <summary>
/// Validates and resolves pagination parameters shared by the history and audit endpoints. Invalid
/// page sizes or malformed cursors are surfaced as a validation error the controller turns into a 400
/// problem-details (AC3), never silently corrected.
/// </summary>
internal static class RequestPaging
{
    /// <summary>Default page size when the caller does not specify one.</summary>
    public const int DefaultPageSize = 50;

    /// <summary>Maximum permitted page size.</summary>
    public const int MaxPageSize = 200;

    /// <summary>
    /// Resolves the page limit and the full composite keyset position <c>(timestamp, id)</c>. Returns
    /// <c>false</c> with a <paramref name="error"/> (field name → message) when the page size is out of
    /// range or the cursor is malformed. Both halves of the cursor are propagated so the page queries can
    /// apply the composite predicate <c>ts &lt; cur.ts OR (ts == cur.ts AND id &lt; cur.id)</c> and never
    /// drop rows that share the boundary timestamp.
    /// </summary>
    /// <summary>
    /// As the other overload, additionally binding the cursor's HMAC to <paramref name="rackId"/> and
    /// <paramref name="endpoint"/> (finding #21) — a cursor forged or replayed across a different rack or
    /// endpoint is rejected the same clean way a malformed one is.
    /// </summary>
    public static bool TryResolve(
        int? pageSize,
        string? cursor,
        Guid rackId,
        string endpoint,
        out int limit,
        out KeysetPosition? after,
        out (string Field, string Message)? error)
    {
        limit = pageSize ?? DefaultPageSize;
        after = null;
        error = null;

        if (pageSize is { } size && (size < 1 || size > MaxPageSize))
        {
            error = (nameof(pageSize), $"pageSize must be between 1 and {MaxPageSize}.");
            return false;
        }

        if (!string.IsNullOrEmpty(cursor))
        {
            if (!CursorCodec.TryDecode(cursor, rackId, endpoint, out var ts, out var id))
            {
                error = (nameof(cursor), "cursor is not a valid pagination cursor.");
                return false;
            }

            after = new KeysetPosition(ts, id);
        }

        return true;
    }
}
