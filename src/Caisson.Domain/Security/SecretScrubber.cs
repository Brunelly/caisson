using System.Text.RegularExpressions;

namespace Caisson.Domain.Security;

/// <summary>
/// Value-level redaction (finding #27) applied to the three free-text columns the property-name guard
/// cannot reach because their values are unstructured: <c>TopologyAuditEvent.DetailsJson</c>,
/// <c>TopologyEntityDiff.DiffPayloadJson</c> and <c>DiscoveryJob.ErrorMessage</c>. None of these are
/// expected to legitimately carry a credential — this is a defensive backstop for the case a device
/// error message, a driver exception, or a future diagnostic accidentally embeds one (e.g. a connection
/// string echoed by a database driver's own exception text).
/// </summary>
public static partial class SecretScrubber
{
    private const string Redacted = "[REDACTED]";

    /// <summary>Redacts userinfo-in-URI, Authorization headers, token/password/secret/key query params, and PEM blocks.</summary>
    public static string? Scrub(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var scrubbed = PemBlockPattern().Replace(value, "[REDACTED PEM BLOCK]");
        scrubbed = UriUserInfoPattern().Replace(scrubbed, "${scheme}" + Redacted + "@");
        scrubbed = AuthorizationHeaderPattern().Replace(scrubbed, $"Authorization: {Redacted}");
        scrubbed = SecretQueryParamPattern().Replace(scrubbed, $"${{key}}={Redacted}");
        return scrubbed;
    }

    // "scheme://user:pass@host" -> "scheme://[REDACTED]@host". Captures the scheme+"://" so it round-trips.
    [GeneratedRegex(@"(?<scheme>[A-Za-z][A-Za-z0-9+.-]*://)[^/@\s""]+:[^/@\s""]+@", RegexOptions.IgnoreCase)]
    private static partial Regex UriUserInfoPattern();

    // Matches through to end-of-line so both the scheme (Bearer/Basic) and the credential are redacted,
    // not just the first whitespace-delimited token.
    [GeneratedRegex(@"Authorization\s*:\s*[^\r\n]+", RegexOptions.IgnoreCase)]
    private static partial Regex AuthorizationHeaderPattern();

    // token=/password=/secret=/apikey=/api_key= query-string-style params, up to the next & " or whitespace.
    [GeneratedRegex(@"(?<key>token|password|secret|api[_-]?key)=[^&\s""]+", RegexOptions.IgnoreCase)]
    private static partial Regex SecretQueryParamPattern();

    [GeneratedRegex(@"-----BEGIN [^-]+-----[\s\S]*?-----END [^-]+-----")]
    private static partial Regex PemBlockPattern();
}
