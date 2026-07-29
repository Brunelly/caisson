using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Caisson.Drivers.Redfish.Transport;

/// <summary>
/// The production <see cref="IIpmiCommandRunner"/>: invokes <c>ipmitool -I lanplus</c> as an external
/// process. It validates every argv against <see cref="IpmiReadCommands.IsReadOnly"/> <b>before spawning
/// anything</b> (NFR1/AC2), passes the password to <c>ipmitool</c> via the <c>IPMI_PASSWORD</c> environment
/// variable and the <c>-E</c> flag — never on the argv or in logs (NFR3) — and kills the process if it
/// exceeds the per-command timeout. An absent <c>ipmitool</c> binary surfaces as an unavailable result the
/// driver maps to a clean <c>UnsupportedOperation</c>/<c>DeviceUnreachable</c> error rather than a crash.
/// </summary>
public sealed class ProcessIpmiCommandRunner : IIpmiCommandRunner
{
    /// <summary>The default, pinned absolute path to the <c>ipmitool</c> binary. See docs/redfish-discovery.md.</summary>
    public const string DefaultExecutablePath = "/usr/bin/ipmitool";

    private readonly ILogger<ProcessIpmiCommandRunner> _logger;
    private readonly string _executablePath;
    private readonly bool _executableUsable;

    /// <summary>Creates the runner with its logger, resolving <see cref="DefaultExecutablePath"/> once.</summary>
    public ProcessIpmiCommandRunner(ILogger<ProcessIpmiCommandRunner> logger)
        : this(logger, DefaultExecutablePath)
    {
    }

    /// <summary>
    /// Creates the runner with a configurable absolute <paramref name="executablePath"/>, resolved once
    /// here rather than left to PATH lookup at spawn time (finding #23): the resolved path is verified to
    /// exist and to not be group/world-writable, so a compromised or misconfigured host cannot substitute
    /// a different binary at the well-known name. Verification failure never throws — it routes to the
    /// same clean <c>Available: false</c> path as a genuinely missing binary.
    /// </summary>
    public ProcessIpmiCommandRunner(ILogger<ProcessIpmiCommandRunner> logger, string executablePath)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        _logger = logger;
        _executablePath = executablePath;
        _executableUsable = VerifyExecutable(executablePath, logger);
    }

    private static bool VerifyExecutable(string path, ILogger logger)
    {
        if (!Path.IsPathRooted(path))
        {
            logger.LogWarning(
                "The configured ipmitool path '{Path}' is not absolute; the IPMI fallback will be unavailable.", path);
            return false;
        }

        if (!File.Exists(path))
        {
            logger.LogWarning(
                "The configured ipmitool path '{Path}' does not exist; the IPMI fallback will be unavailable.", path);
            return false;
        }

        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(path);
            if ((mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
            {
                logger.LogWarning(
                    "The configured ipmitool path '{Path}' is group- or world-writable ({Mode}); refusing to " +
                    "run it. The IPMI fallback will be unavailable.", path, mode);
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public async Task<IpmiCommandResult> RunAsync(
        IReadOnlyList<string> argv, IpmiConnectionSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(argv);
        ArgumentNullException.ThrowIfNull(settings);

        // Read-only safety boundary: reject anything not on the allowlist BEFORE spawning a process. This is
        // a programmer error, not an expected device failure, so it throws.
        if (!IpmiReadCommands.IsReadOnly(argv))
        {
            throw new InvalidOperationException(
                $"The ipmitool subcommand '{string.Join(' ', argv)}' is not on the read-only allowlist and will not be run.");
        }

        if (!_executableUsable)
        {
            return new IpmiCommandResult(Available: false, ExitCode: -1, string.Empty, string.Empty);
        }

        cancellationToken.ThrowIfCancellationRequested();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(settings.Timeout);

        var startInfo = new ProcessStartInfo(_executablePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // Fixed, non-CWD-dependent working directory (the resolved binary's own directory) so the
            // spawned process's relative-path resolution can never be influenced by the caller's CWD.
            WorkingDirectory = Path.GetDirectoryName(_executablePath) ?? Path.GetTempPath(),
        };

        // Connection flags first, then the read-only subcommand. -E reads the password from IPMI_PASSWORD,
        // so the secret never appears on the argument vector (which is world-readable via /proc).
        startInfo.ArgumentList.Add("-I");
        startInfo.ArgumentList.Add("lanplus");
        startInfo.ArgumentList.Add("-H");
        startInfo.ArgumentList.Add(settings.Host);
        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add(settings.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-U");
        startInfo.ArgumentList.Add(settings.Username);
        startInfo.ArgumentList.Add("-E");
        foreach (var arg in argv)
        {
            startInfo.ArgumentList.Add(arg);
        }

        // A minimal, explicit environment rather than the inherited process environment — only what
        // ipmitool needs (the password, via IPMI_PASSWORD) is passed through.
        startInfo.Environment.Clear();
        startInfo.Environment["IPMI_PASSWORD"] = settings.Password;

        using var process = new Process { StartInfo = startInfo };

        try
        {
            if (!process.Start())
            {
                return new IpmiCommandResult(Available: false, ExitCode: -1, string.Empty, string.Empty);
            }
        }
        catch (Win32Exception)
        {
            // ipmitool could not be spawned — a clean "unavailable", not a failure.
            _logger.LogWarning(
                "ipmitool is not available on this host; the IPMI fallback for {Host} cannot run.", settings.Host);
            return new IpmiCommandResult(Available: false, ExitCode: -1, string.Empty, string.Empty);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(linked.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(linked.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw;
        }
        catch (OperationCanceledException)
        {
            // Per-command timeout — kill and report as an unavailable result the driver treats as unreachable.
            TryKill(process);
            _logger.LogWarning(
                "ipmitool '{Subcommand}' for {Host} exceeded {Timeout} and was terminated.",
                string.Join(' ', argv), settings.Host, settings.Timeout);
            return new IpmiCommandResult(Available: true, ExitCode: -1, string.Empty, "timed out");
        }

        var stdout = await SafeRead(stdoutTask).ConfigureAwait(false);
        var stderr = await SafeRead(stderrTask).ConfigureAwait(false);

        _logger.LogInformation(
            "ipmitool {Subcommand} host {Host} exit {ExitCode}",
            string.Join(' ', argv), settings.Host, process.ExitCode);

        return new IpmiCommandResult(Available: true, process.ExitCode, stdout, stderr);
    }

    private static async Task<string> SafeRead(Task<string> readTask)
    {
        try
        {
            return await readTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return string.Empty;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Process already exited between the check and the kill — nothing to do.
        }
        catch (Win32Exception)
        {
            // The OS refused the kill (already dead / permissions) — best-effort only.
        }
    }
}
