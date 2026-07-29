namespace Caisson.Drivers.Redfish.Transport;

/// <summary>
/// The testable command-runner seam for the IPMI fallback. Abstracted so
/// <see cref="Caisson.Drivers.Redfish.RedfishBmcDriver"/> can be unit-tested against a stub that replays
/// canned <c>ipmitool</c> text without spawning a process, while the production
/// <see cref="ProcessIpmiCommandRunner"/> owns the real invocation. The runner validates every argv against
/// <see cref="IpmiReadCommands.IsReadOnly"/> before spawning anything, so the read-only boundary holds for
/// the IPMI path too (NFR1/AC2).
/// </summary>
public interface IIpmiCommandRunner
{
    /// <summary>
    /// Runs a single read-only <c>ipmitool</c> subcommand (e.g. <c>["mc", "info"]</c>) and returns its
    /// captured output. Never throws for an expected failure (missing binary, unreachable BMC, auth
    /// rejection) — those surface as a non-zero <see cref="IpmiCommandResult.ExitCode"/> and/or
    /// <see cref="IpmiCommandResult.Available"/> being <c>false</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown before any process spawn when <paramref name="argv"/> is not on the read-only allowlist.
    /// </exception>
    Task<IpmiCommandResult> RunAsync(
        IReadOnlyList<string> argv, IpmiConnectionSettings settings, CancellationToken cancellationToken);
}

/// <summary>
/// Everything <see cref="ProcessIpmiCommandRunner"/> needs to invoke <c>ipmitool</c>. Built alongside the
/// Redfish settings from the same resolved credential (one shared credential serves both Redfish Basic
/// auth and IPMI lanplus, realistic for iLO). The raw <see cref="Password"/> lives only here, is passed to
/// <c>ipmitool</c> via the <c>IPMI_PASSWORD</c> environment variable (never argv), and is never logged.
/// </summary>
/// <param name="Host">BMC/iLO hostname or IP.</param>
/// <param name="Port">IPMI RMCP+ port (623 by default).</param>
/// <param name="Username">IPMI account username.</param>
/// <param name="Password">IPMI account password.</param>
/// <param name="Timeout">Per-command timeout after which the process is killed.</param>
public sealed record IpmiConnectionSettings(
    string Host, int Port, string Username, string Password, TimeSpan Timeout)
{
    /// <summary>The default IPMI RMCP+ (lanplus) UDP port.</summary>
    public const int DefaultPort = 623;

    /// <summary>
    /// Overrides the compiler-generated record <c>ToString()</c> — which would otherwise print every
    /// positional member, including <see cref="Password"/> — so an accidental <c>settings.ToString()</c>
    /// (e.g. in a debugger watch or a future log call) can never leak the credential.
    /// </summary>
    public override string ToString() => $"{Host}:{Port} as {Username}";
}

/// <summary>
/// The structured result of running one <c>ipmitool</c> subcommand.
/// </summary>
/// <param name="Available">
/// <c>false</c> when <c>ipmitool</c> could not be run at all (the binary is absent), so the driver can map
/// it to an <c>UnsupportedOperation</c>/<c>DeviceUnreachable</c> error rather than a parse failure.
/// </param>
/// <param name="ExitCode">The process exit code (0 = success). Non-zero on an unreachable BMC or auth failure.</param>
/// <param name="StandardOutput">Captured stdout — the text the parser consumes.</param>
/// <param name="StandardError">Captured stderr — used only for secret-free diagnostics, never surfaced raw.</param>
public sealed record IpmiCommandResult(
    bool Available, int ExitCode, string StandardOutput, string StandardError)
{
    /// <summary>Whether the command ran and completed successfully (available and exit code 0).</summary>
    public bool Succeeded => Available && ExitCode == 0;
}
