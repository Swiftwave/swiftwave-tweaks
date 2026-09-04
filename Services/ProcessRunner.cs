using System.Diagnostics;
using System.IO;

namespace SwiftwaveTweaks.Services;

internal static class ProcessRunner
{
    /// <summary>Runs a process and captures combined stdout+stderr. Returns null when the process could not be started.</summary>
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
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(15_000);
            return (process.ExitCode, output);
        }
        catch { return null; }
    }

    public static string? Run(string fileName, string arguments)
        => RunCapture(fileName, arguments) is { } r && r.ExitCode == 0 ? r.Output : null;

    /// <summary>Runs a PowerShell script without elevation and captures its output.</summary>
    public static (int ExitCode, string Output) RunPowerShell(string script)
    {
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        var r = RunCapture("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}");
        return r is null ? (-1, "PowerShell could not be started.") : r.Value;
    }

    /// <summary>Runs a PowerShell script elevated through UAC. Returns false when cancelled or when the exit code is non-zero.</summary>
    public static bool RunElevatedPowerShell(string script)
    {
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        try
        {
            using var process = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}")
            {
                Verb = "runas",
                UseShellExecute = true
            });
            if (process is null) return false;
            process.WaitForExit();
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
            process.WaitForExit();
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
