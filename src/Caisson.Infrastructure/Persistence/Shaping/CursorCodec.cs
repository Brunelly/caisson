using System.Globalization;
using System.Text;

namespace Caisson.Infrastructure.Persistence.Shaping;

/// <summary>
/// A decoded keyset position: the full composite sort key <c>(timestamp, id)</c> a page continues
/// strictly after. Both halves are carried so the page queries can apply the composite predicate
/// <c>ts &lt; TimestampUtc OR (ts == TimestampUtc AND id &lt; Id)</c> and never skip rows that share the
/// boundary timestamp but sort lower on the <c>id</c> tie-break.
/// </summary>
public readonly record struct KeysetPosition(DateTime TimestampUtc, Guid Id);

/// <summary>
/// An opaque, URL-safe base64 keyset cursor over the composite sort key <c>(timestamp, id)</c> used by
/// the snapshot-history and audit pagination endpoints. The cursor is deliberately opaque to callers;
/// invalid or malformed cursors are rejected (return <c>false</c>) so the API can answer with a 400
/// problem-details rather than silently resetting pagination. Pure and DB-free.
/// </summary>
public static class CursorCodec
{
    /// <summary>Encodes a <c>(timestamp, id)</c> keyset position into an opaque cursor string.</summary>
    public static string Encode(DateTime timestampUtc, Guid id)
    {
        var raw = timestampUtc.Ticks.ToString(CultureInfo.InvariantCulture) + "|" + id.ToString("N");
        return ToBase64Url(Encoding.UTF8.GetBytes(raw));
    }

    /// <summary>Attempts to decode an opaque cursor into its <c>(timestamp, id)</c> keyset position.</summary>
    public static bool TryDecode(string? cursor, out DateTime timestampUtc, out Guid id)
    {
        timestampUtc = default;
        id = default;

        if (string.IsNullOrWhiteSpace(cursor))
        {
            return false;
        }

        byte[] bytes;
        try
        {
            bytes = FromBase64Url(cursor);
        }
        catch (FormatException)
        {
            return false;
        }

        var raw = Encoding.UTF8.GetString(bytes);
        var separator = raw.IndexOf('|', StringComparison.Ordinal);
        if (separator <= 0 || separator == raw.Length - 1)
        {
            return false;
        }

        if (!long.TryParse(
                raw.AsSpan(0, separator), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)
            || ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks
            || !Guid.TryParseExact(raw.AsSpan(separator + 1), "N", out var parsedId))
        {
            return false;
        }

        timestampUtc = new DateTime(ticks, DateTimeKind.Utc);
        id = parsedId;
        return true;
    }

    private static string ToBase64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        switch (normalized.Length % 4)
        {
            case 2: normalized += "=="; break;
            case 3: normalized += "="; break;
            case 1: throw new FormatException("Invalid base64url length.");
        }

        // Convert.FromBase64String throws FormatException on any non-base64 content, giving callers a
        // clean rejection path.
        return Convert.FromBase64String(normalized);
    }
}
