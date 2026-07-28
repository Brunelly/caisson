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
    private const string Executable = "ipmitool";

    private readonly ILogger<ProcessIpmiCommandRunner> _logger;

    /// <summary>Creates the runner with its logger.</summary>
    public ProcessIpmiCommandRunner(ILogger<ProcessIpmiCommandRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
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

        cancellationToken.ThrowIfCancellationRequested();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(settings.Timeout);

        var startInfo = new ProcessStartInfo(Executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
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
            // ipmitool is not installed / not on PATH — a clean "unavailable", not a failure.
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
