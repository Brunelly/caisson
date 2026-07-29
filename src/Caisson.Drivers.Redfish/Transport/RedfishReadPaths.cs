using System.Collections.Frozen;

namespace Caisson.Drivers.Redfish.Transport;

/// <summary>
/// The complete, code-reviewable read-only Redfish safety boundary (NFR1/AC1), mirroring
/// <see cref="Caisson.Drivers.MikroTik.Transport.RouterOsReadCommands"/>. Every Redfish request the
/// driver may issue must satisfy <see cref="IsReadOnlyGet"/>: it must be an HTTP <c>GET</c>, its path must
/// live under <c>/redfish/v1</c>, it must not touch an <c>/Actions/</c> segment (which is how Redfish
/// exposes every mutating operation — reset, power, virtual media) nor a <c>/Settings</c> segment (the
/// pending-config write resource), and it must fall inside the allowlisted resource prefix set. The
/// <see cref="RedfishClient"/> chokepoint runs this guard <b>before any I/O</b>, so a mutating or
/// off-allowlist request can never leave the process — the read-only boundary is enforced in the
/// transport itself, not merely by the driver's public surface.
/// </summary>
public static class RedfishReadPaths
{
    /// <summary>The Redfish service root, the single navigation entry point.</summary>
    public const string ServiceRoot = "/redfish/v1";

    /// <summary>The <c>ComputerSystem</c> collection (feeds system inventory).</summary>
    public const string Systems = "/redfish/v1/Systems";

    /// <summary>The <c>Manager</c> collection — the BMC/iLO itself (feeds identity/firmware).</summary>
    public const string Managers = "/redfish/v1/Managers";

    /// <summary>The <c>Chassis</c> collection (physical enclosure inventory).</summary>
    public const string Chassis = "/redfish/v1/Chassis";

    /// <summary>The path segment through which Redfish exposes every mutating operation — hard-rejected.</summary>
    private const string ActionsSegment = "/Actions/";

    /// <summary>The pending-configuration write resource — hard-rejected so a read can never touch it.</summary>
    private const string SettingsSegment = "/Settings";

    /// <summary>
    /// Upper bound on an accepted path length. Real Redfish resource paths are short (a few dozen
    /// characters); this generous cap exists only to bound the cost of validating and logging a
    /// device-supplied <c>@odata.id</c>, not to accommodate any legitimate long path.
    /// </summary>
    private const int MaxPathLength = 512;

    /// <summary>Truncation length for a path projected into a log line or exception message.</summary>
    private const int MaxLoggedPathLength = 256;

    /// <summary>
    /// The positive character allow-list for a Redfish resource path: letters, digits, and the small set
    /// of punctuation that legitimately appears in a path/query — <c>/ . _ - : % ? & = , ~ # $</c> (the
    /// trailing <c>$</c> is required for OData query options such as <c>$select</c>/<c>$filter</c>).
    /// Anything outside this set (in particular control characters such as CR/LF) is rejected outright,
    /// closing off log-injection via a crafted <c>@odata.id</c> (the control-character check below is
    /// redundant with this allow-list but kept as explicit, self-documenting defence in depth).
    /// </summary>
    private static bool IsAllowedPathCharacter(char c)
        => char.IsAsciiLetterOrDigit(c) || c is '/' or '.' or '_' or '-' or ':' or '%' or '?' or '&' or '=' or ',' or '~' or '#' or '$';

    /// <summary>
    /// The allowlisted resource-path prefixes. Every accepted path must equal, or begin with one of, these
    /// prefixes; combined with the <c>/Actions/</c> and <c>/Settings</c> rejections this scopes the driver
    /// to service-root navigation plus system/manager/chassis inventory and the read-only sub-resources of a
    /// system (network interfaces/adapters and BIOS). Ordinal set — membership is exact and culture-independent.
    /// </summary>
    public static readonly FrozenSet<string> AllowedPrefixes = new[]
    {
        ServiceRoot,
        Systems,
        Managers,
        Chassis,
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Returns <c>true</c> only when <paramref name="method"/> is <c>GET</c> and <paramref name="path"/> is
    /// an allowlisted, non-mutating Redfish resource path. Navigation that follows an <c>@odata.id</c> link
    /// re-passes this guard, and any <c>.</c>/<c>..</c> dot-segment is hard-rejected, so a link a device hands
    /// back can never widen the boundary by traversal (a raw path such as
    /// <c>/redfish/v1/Systems/../AccountService</c> would pass a naïve prefix check yet resolve, once
    /// <see cref="Uri"/>/<see cref="System.Net.Http.HttpClient"/> collapse the dot-segments, to an off-allowlist
    /// resource). A query string (a <c>?</c>-suffixed path) is evaluated on its resource portion only.
    /// </summary>
    public static bool IsReadOnlyGet(string method, string? path)
    {
        if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        // Reject an implausibly long path and any control character (CR/LF in particular — a device that
        // returns a crafted @odata.id must never be able to inject a fake log line or exception message)
        // before doing anything else with it. The positive allow-list below is the primary defence; this
        // is a fast, explicit up-front reject.
        if (path.Length > MaxPathLength || path.Any(char.IsControl))
        {
            return false;
        }

        if (!path.All(IsAllowedPathCharacter))
        {
            return false;
        }

        // Evaluate the resource portion only; a fragment/query never changes what resource is addressed.
        var resource = path;
        var cut = resource.IndexOfAny(new[] { '?', '#' });
        if (cut >= 0)
        {
            resource = resource[..cut];
        }

        // A legitimate Redfish resource path never contains a backslash. .NET's Uri/HttpClient treat '\'
        // as '/', so a device-supplied "Systems/..\..\AccountService" — whose single '/'-delimited segment
        // hides the dot-segments from the split below — would collapse on the wire to an off-allowlist
        // resource (e.g. /redfish/v1/AccountService). Reject any backslash, raw or percent-encoded, up front.
        if (resource.Contains('\\') || Uri.UnescapeDataString(resource).Contains('\\'))
        {
            return false;
        }

        // Hard-reject any dot-segment BEFORE the prefix check. HttpClient/Uri canonicalize the request path
        // just before it goes on the wire, collapsing "Systems/../AccountService" to "AccountService"; if we
        // validated the raw string we would admit a device-supplied @odata.id that then escapes the allowlisted
        // subtree. Rejecting "." and ".." segments (literal or percent-encoded) closes that traversal.
        if (ContainsDotSegment(resource))
        {
            return false;
        }

        // Trim any trailing slash so "/redfish/v1/Systems/" and "/redfish/v1/Systems" are treated alike.
        if (resource.Length > ServiceRoot.Length && resource[^1] == '/')
        {
            resource = resource.TrimEnd('/');
        }

        if (!resource.StartsWith(ServiceRoot, StringComparison.Ordinal))
        {
            return false;
        }

        // Hard-reject every action endpoint and the pending-settings write resource, wherever they appear.
        if (resource.Contains(ActionsSegment, StringComparison.OrdinalIgnoreCase)
            || EndsWithOrContainsSettings(resource))
        {
            return false;
        }

        return MatchesAllowedPrefix(resource);
    }

    private static bool EndsWithOrContainsSettings(string resource)
        => resource.EndsWith(SettingsSegment, StringComparison.OrdinalIgnoreCase)
            || resource.Contains(SettingsSegment + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns <c>true</c> if any <c>/</c>-delimited segment of <paramref name="resource"/> is a <c>.</c> or
    /// <c>..</c> dot-segment — checked on both the raw path and its percent-decoded form so an encoded
    /// <c>%2e%2e</c> (or an encoded separator hiding one) is caught too. A legitimate Redfish resource path
    /// never contains a dot-segment, so this is a safe, unconditional reject of every traversal attempt.
    /// </summary>
    private static bool ContainsDotSegment(string resource)
        => HasDotSegment(resource) || HasDotSegment(Uri.UnescapeDataString(resource));

    private static bool HasDotSegment(string resource)
    {
        // Split on '\' as well as '/': .NET's Uri treats a backslash as a path separator, so a "..\.."
        // sequence is a real traversal even though it contains no '/'. (Backslashes are also rejected
        // outright in IsReadOnlyGet; splitting here keeps this helper correct as defence in depth.)
        foreach (var segment in resource.Split('/', '\\'))
        {
            if (segment is "." or "..")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Projects a device-supplied path into a form safe to embed in a log line or exception message: CR/LF
    /// (and any other control character) stripped, then truncated to <see cref="MaxLoggedPathLength"/>. Used
    /// even for paths that already passed <see cref="IsReadOnlyGet"/> so a future change to that allowlist
    /// can never reopen the log-injection path here silently.
    /// </summary>
    public static string SanitizeForLog(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder(Math.Min(path.Length, MaxLoggedPathLength));
        foreach (var c in path)
        {
            if (builder.Length >= MaxLoggedPathLength)
            {
                builder.Append("...(truncated)");
                break;
            }

            if (!char.IsControl(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static bool MatchesAllowedPrefix(string resource)
    {
        foreach (var prefix in AllowedPrefixes)
        {
            if (string.Equals(resource, prefix, StringComparison.Ordinal))
            {
                return true;
            }

            // Subtree (boundary-prefix) matches are allowed for the collection roots only — "/redfish/v1/Systems/1"
            // is in, "/redfish/v1/SystemsX" is not — but NOT for the service root itself: allowing
            // "/redfish/v1" as a prefix would admit every /redfish/v1/* resource (e.g. /AccountService) and
            // defeat the allowlist.
            if (!string.Equals(prefix, ServiceRoot, StringComparison.Ordinal)
                && resource.Length > prefix.Length
                && resource.StartsWith(prefix, StringComparison.Ordinal)
                && resource[prefix.Length] == '/')
            {
                return true;
            }
        }

        return false;
    }
}
