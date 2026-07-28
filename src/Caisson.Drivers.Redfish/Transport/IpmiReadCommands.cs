using System.Collections.Frozen;

namespace Caisson.Drivers.Redfish.Transport;

/// <summary>
/// The complete, code-reviewable set of read-only <c>ipmitool</c> subcommands the IPMI fallback is
/// permitted to run (NFR1/AC2), the IPMI analogue of <see cref="RedfishReadPaths"/>. Every entry is a
/// query — <c>mc info</c>, <c>fru print</c>, <c>lan print</c>, <c>sdr elist</c>/<c>sdr type</c>,
/// <c>chassis status</c> — with no power, reset, raw, SOL, user, boot-order or SEL-clear verb.
/// <see cref="IsReadOnly"/> is checked by <see cref="ProcessIpmiCommandRunner"/> <b>before any process is
/// spawned</b>, so a mutating subcommand can never reach <c>ipmitool</c>.
/// </summary>
public static class IpmiReadCommands
{
    /// <summary>BMC/management-controller identity and firmware (feeds system inventory).</summary>
    public static readonly string[] McInfo = { "mc", "info" };

    /// <summary>FRU inventory — product/board serial, model, manufacturer (feeds system inventory).</summary>
    public static readonly string[] FruPrint = { "fru", "print" };

    /// <summary>LAN channel configuration incl. the BMC MAC address (feeds network interfaces).</summary>
    public static readonly string[] LanPrint = { "lan", "print" };

    /// <summary>Sensor Data Record list, extended form (feeds sensor/interface evidence).</summary>
    public static readonly string[] SdrElist = { "sdr", "elist" };

    /// <summary>Sensor Data Records filtered by type (feeds sensor/interface evidence).</summary>
    public static readonly string[] SdrType = { "sdr", "type" };

    /// <summary>Chassis power/health status — read-only (feeds power/health evidence).</summary>
    public static readonly string[] ChassisStatus = { "chassis", "status" };

    /// <summary>
    /// The allowlisted subcommand prefixes (token sequences). An argv is read-only only if it begins with
    /// one of these <em>and</em> contains no mutating token (see <see cref="MutatingTokens"/>).
    /// </summary>
    private static readonly string[][] AllowedPrefixes =
    {
        McInfo, FruPrint, LanPrint, SdrElist, SdrType, ChassisStatus,
    };

    /// <summary>
    /// Tokens that unambiguously indicate a write/control operation. Their presence anywhere in an argv is
    /// a hard reject — defence in depth on top of the allowlist so a crafted argv like
    /// <c>["chassis", "power", "off"]</c> or <c>["sel", "clear"]</c> can never slip through.
    /// </summary>
    private static readonly FrozenSet<string> MutatingTokens = new[]
    {
        "power", "reset", "raw", "sol", "user", "set", "clear",
        "on", "off", "cycle", "reboot", "boot", "bootdev", "cold", "warm", "sel",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns <c>true</c> only when <paramref name="argv"/> is one of the allowlisted read-only
    /// subcommands (optionally with a trailing selector such as a channel number or sensor type) and
    /// contains no mutating token. Any argv that is empty, off-allowlist, or carries a write verb is rejected.
    /// </summary>
    public static bool IsReadOnly(IReadOnlyList<string>? argv)
    {
        if (argv is null || argv.Count == 0)
        {
            return false;
        }

        // Defence in depth: a mutating token anywhere is a hard reject, even inside an otherwise-allowlisted
        // prefix (e.g. the SdrType prefix "sdr type" followed by a "reset" argument).
        for (var i = 0; i < argv.Count; i++)
        {
            var token = argv[i];
            if (token is not null && MutatingTokens.Contains(token))
            {
                return false;
            }
        }

        foreach (var prefix in AllowedPrefixes)
        {
            if (StartsWith(argv, prefix))
            {
                return true;
            }
        }

        return false;
    }

    private static bool StartsWith(IReadOnlyList<string> argv, string[] prefix)
    {
        if (argv.Count < prefix.Length)
        {
            return false;
        }

        for (var i = 0; i < prefix.Length; i++)
        {
            if (!string.Equals(argv[i], prefix[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
