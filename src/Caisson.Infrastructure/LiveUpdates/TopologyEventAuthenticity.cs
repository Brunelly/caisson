using System.Security.Cryptography;
using System.Text;

namespace Caisson.Infrastructure.LiveUpdates;

/// <summary>
/// HMAC-SHA256 authenticity for the Redis pub/sub wire envelope (finding #2). Every API instance both
/// publishes to and relays from the single events channel, so anything that can write to that channel —
/// a Redis ACL misconfiguration, a misrouted publish from another app sharing the instance — can inject
/// an event straight into every connected client's UI. The publisher appends a MAC over the serialized
/// payload; the subscriber verifies and strips it before deserializing, dropping (never throwing — this
/// runs inside a fire-and-forget pub/sub callback) anything whose tag is missing or wrong. Mirrors
/// <c>CursorCodec</c>'s key-resolution convention, with its own env var so the two keys can rotate
/// independently. See ADR 0021.
/// </summary>
public static class TopologyEventAuthenticity
{
    private const string DevelopmentKey = "insecure-development-only-redis-event-hmac-key-do-not-use-in-production";

    // Full SHA-256 output, hex-encoded — unlike CursorCodec's cursor this isn't carried in a URL, so
    // there is no reason to truncate it.
    private const int MacHexLength = 64;

    private static readonly Lazy<byte[]> HmacKey = new(ResolveKey);

    /// <summary>Appends a hex HMAC tag to a serialized event payload.</summary>
    public static string Sign(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return payload + ComputeMac(payload);
    }

    /// <summary>
    /// Verifies and strips the trailing HMAC tag, returning the original payload, or null when the tag is
    /// missing, malformed, or does not match — never throws.
    /// </summary>
    public static string? Verify(string? signed)
    {
        if (string.IsNullOrEmpty(signed) || signed.Length <= MacHexLength)
        {
            return null;
        }

        var payload = signed[..^MacHexLength];
        var suppliedMac = signed[^MacHexLength..];
        var expectedMac = ComputeMac(payload);
        return FixedTimeEquals(expectedMac, suppliedMac) ? payload : null;
    }

    private static string ComputeMac(string payload)
    {
        var mac = HMACSHA256.HashData(HmacKey.Value, Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(mac).ToLowerInvariant();
    }

    private static bool FixedTimeEquals(string expectedHex, string suppliedHex)
    {
        // suppliedHex is always MacHexLength here (sliced by fixed offset), but guard the length anyway
        // before the constant-time compare so a future caller change can't index past either buffer.
        if (expectedHex.Length != suppliedHex.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expectedHex), Encoding.ASCII.GetBytes(suppliedHex));
    }

    private static byte[] ResolveKey()
    {
        var configured = Environment.GetEnvironmentVariable("CAISSON_REDIS_HMAC_KEY");
        if (!string.IsNullOrEmpty(configured))
        {
            return Encoding.UTF8.GetBytes(configured);
        }

        if (IsProductionEnvironment())
        {
            throw new InvalidOperationException(
                "CAISSON_REDIS_HMAC_KEY must be configured under ASPNETCORE_ENVIRONMENT=Production — " +
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
}
