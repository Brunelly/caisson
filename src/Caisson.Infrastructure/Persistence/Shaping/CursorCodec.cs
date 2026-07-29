using System.Globalization;
using System.Security.Cryptography;
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
/// <remarks>
/// Finding #21: the cursor carries a truncated HMAC-SHA256 over the payload, bound to the rack id and
/// endpoint it was issued for — a forged cursor (or one replayed across a different rack/endpoint) is
/// rejected the same way a malformed one is (a clean <c>TryDecode</c> <c>false</c>, never a throw). The
/// key is resolved once from <c>CAISSON_CURSOR_HMAC_KEY</c>; outside a positively-identified Production
/// environment (<c>ASPNETCORE_ENVIRONMENT=Production</c>) an unset key falls back to a fixed, documented
/// development value so the existing round-trip tests — and any environment that doesn't explicitly
/// declare itself Production — keep working without extra setup.
/// </remarks>
public static class CursorCodec
{
    private const string DevelopmentKey = "insecure-development-only-cursor-hmac-key-do-not-use-in-production";
    private const int MacBytes = 16;

    private static readonly Lazy<byte[]> HmacKey = new(ResolveKey);

    /// <summary>Encodes a <c>(timestamp, id)</c> keyset position, bound to <paramref name="rackId"/>/<paramref name="endpoint"/>, into an opaque cursor string.</summary>
    public static string Encode(DateTime timestampUtc, Guid id, Guid rackId, string endpoint)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpoint);

        var payload = timestampUtc.Ticks.ToString(CultureInfo.InvariantCulture) + "|" + id.ToString("N");
        var mac = ComputeMac(payload, rackId, endpoint);
        return ToBase64Url(Encoding.UTF8.GetBytes(payload + "|" + mac));
    }

    /// <summary>
    /// Attempts to decode an opaque cursor into its <c>(timestamp, id)</c> keyset position, verifying the
    /// embedded HMAC is bound to <paramref name="rackId"/>/<paramref name="endpoint"/> before parsing.
    /// </summary>
    public static bool TryDecode(
        string? cursor, Guid rackId, string endpoint, out DateTime timestampUtc, out Guid id)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpoint);
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
        var parts = raw.Split('|');
        if (parts.Length != 3)
        {
            return false;
        }

        var payload = parts[0] + "|" + parts[1];
        var expectedMac = ComputeMac(payload, rackId, endpoint);
        if (!FixedTimeEquals(expectedMac, parts[2]))
        {
            return false;
        }

        if (!long.TryParse(
                parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)
            || ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks
            || !Guid.TryParseExact(parts[1], "N", out var parsedId))
        {
            return false;
        }

        timestampUtc = new DateTime(ticks, DateTimeKind.Utc);
        id = parsedId;
        return true;
    }

    private static string ComputeMac(string payload, Guid rackId, string endpoint)
    {
        var canonical = rackId.ToString("N") + "|" + endpoint + "|" + payload;
        var mac = HMACSHA256.HashData(HmacKey.Value, Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(mac.AsSpan(0, MacBytes)).ToLowerInvariant();
    }

    private static bool FixedTimeEquals(string expectedHex, string suppliedHex)
    {
        // Both are our own fixed-length hex output on the happy path, but a forged cursor can supply any
        // length string here — compare bytes only after confirming equal length to avoid indexing past
        // either buffer, still via a constant-time comparison for the (well-formed) equal-length case.
        if (expectedHex.Length != suppliedHex.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expectedHex), Encoding.ASCII.GetBytes(suppliedHex));
    }

    private static byte[] ResolveKey()
    {
        var configured = Environment.GetEnvironmentVariable("CAISSON_CURSOR_HMAC_KEY");
        if (!string.IsNullOrEmpty(configured))
        {
            return Encoding.UTF8.GetBytes(configured);
        }

        if (IsProductionEnvironment())
        {
            throw new InvalidOperationException(
                "CAISSON_CURSOR_HMAC_KEY must be configured under ASPNETCORE_ENVIRONMENT=Production — " +
                "refusing to fall back to the fixed development key.");
        }

        return Encoding.UTF8.GetBytes(DevelopmentKey);
    }

    private static bool IsProductionEnvironment()
    {
        var env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        return string.Equals(env, "Production", StringComparison.OrdinalIgnoreCase);
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
