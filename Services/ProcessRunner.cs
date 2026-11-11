using System.Diagnostics;
using System.IO;

namespace SwiftwaveTweaks.Services;

internal static class ProcessRunner
{
    /// <summary>
    /// Longest an elevated child may run before it is killed and the caller gets a failure.
    /// Generous enough for Optimize-Volume defrags and first-time restore-point creation.
    /// </summary>
    private static readonly TimeSpan ElevatedTimeout = TimeSpan.FromMinutes(30);

    /// <summary>Runs a process and captures combined stdout+stderr. Returns null when the process could not be started or timed out.</summary>
    public static (int ExitCode, string Output)? RunCapture(string fileName, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null) return null;
            // Read asynchronously so a hung child cannot block us past the timeout.
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(15_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                SafeLog.Write($"{fileName} capture timed out after 15s and was killed.");
                return (-1, "");
            }
            return (process.ExitCode, stdout.Result + stderr.Result);
        }
        catch { return null; }
    }

    public static string? Run(string fileName, string arguments)
        => RunCapture(fileName, arguments) is { } r && r.ExitCode == 0 ? r.Output : null;

    /// <summary>Runs a PowerShell script without elevation and captures its output.</summary>
    public static (int ExitCode, string Output) RunPowerShell(string script)
    {
        // Suppress the progress stream: when stderr is redirected, PowerShell serialises progress
        // records as a trailing "#< CLIXML" block, which breaks exact-output parsing downstream.
        var wrapped = "$ProgressPreference='SilentlyContinue';" + script;
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(wrapped));
        var r = RunCapture("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}");
        return r is null ? (-1, "PowerShell could not be started.") : r.Value;
    }

    /// <summary>Runs a PowerShell script elevated through UAC. Returns false when cancelled or when the exit code is non-zero.</summary>
    public static bool RunElevatedPowerShell(string script)
    {
        // Every elevated mutation gets one best-effort session restore point first (no-op after
        // the first attempt; reentrant calls from the restore-point creation itself are skipped).
        RestorePointService.EnsureSessionRestorePoint();
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        try
        {
            using var process = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}")
            {
                Verb = "runas",
                UseShellExecute = true
            });
            if (process is null) return false;
            if (!process.WaitForExit(ElevatedTimeout))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                SafeLog.Write("Elevated PowerShell timed out after 30 minutes and was killed.");
                return false;
            }
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // UAC prompt cancelled
        }
    }

    /// <summary>
    /// Runs a PowerShell script elevated and captures its standard output by having the elevated
    /// process write to a temp file (elevated processes cannot pipe stdout to a non-elevated parent).
    /// </summary>
    public static (int ExitCode, string Output) RunElevatedPowerShellWithOutput(string script)
    {
        RestorePointService.EnsureSessionRestorePoint();
        var outFile = Path.Combine(Path.GetTempPath(), $"swt-elev-{Guid.NewGuid():N}.txt");
        var wrapped = $"& {{ {script} }} *>> '{outFile}'";
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(wrapped));
        int exitCode;
        try
        {
            using var process = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}")
            {
                Verb = "runas",
                UseShellExecute = true
            });
            if (process is null) return (-1, "Elevated PowerShell could not be started.");
            if (!process.WaitForExit(ElevatedTimeout))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                SafeLog.Write("Elevated PowerShell (with output) timed out after 30 minutes and was killed.");
                return (-1, "Elevated operation timed out.");
            }
            exitCode = process.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (-1, "Elevation was cancelled.");
        }
        string output = "";
        try { if (File.Exists(outFile)) output = File.ReadAllText(outFile); }
        catch { }
        finally { try { File.Delete(outFile); } catch { } }
        return (exitCode, output);
    }
}
