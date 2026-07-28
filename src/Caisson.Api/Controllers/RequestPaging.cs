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
    /// Resolves the page limit and keyset position. Returns <c>false</c> with a <paramref name="error"/>
    /// (field name → message) when the page size is out of range or the cursor is malformed.
    /// </summary>
    public static bool TryResolve(
        int? pageSize,
        string? cursor,
        out int limit,
        out DateTime? afterTimestampUtc,
        out (string Field, string Message)? error)
    {
        limit = pageSize ?? DefaultPageSize;
        afterTimestampUtc = null;
        error = null;

        if (pageSize is { } size && (size < 1 || size > MaxPageSize))
        {
            error = (nameof(pageSize), $"pageSize must be between 1 and {MaxPageSize}.");
            return false;
        }

        if (!string.IsNullOrEmpty(cursor))
        {
            if (!CursorCodec.TryDecode(cursor, out var ts, out _))
            {
                error = (nameof(cursor), "cursor is not a valid pagination cursor.");
                return false;
            }

            afterTimestampUtc = ts;
        }

        return true;
    }
}
