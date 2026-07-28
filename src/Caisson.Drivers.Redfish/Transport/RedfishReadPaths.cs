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
    /// re-passes this guard, so a link a device hands back can never widen the boundary. A query string (a
    /// <c>?</c>-suffixed path) is evaluated on its resource portion only.
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

        // Evaluate the resource portion only; a fragment/query never changes what resource is addressed.
        var resource = path;
        var cut = resource.IndexOfAny(new[] { '?', '#' });
        if (cut >= 0)
        {
            resource = resource[..cut];
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
