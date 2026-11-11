using System.Diagnostics;
using System.IO;
using System.Text.Json;
using SwiftwaveTweaks.Services;

namespace SwiftwaveTweaks.Core;

/// <summary>
/// Fortnite specific optimizations: excludes logical CPU 0 from the
/// FortniteClient-Win64-Shipping process affinity by installing a Swiftwave-owned,
/// fully hidden per-user Startup watcher (a .lnk launcher in shell:startup that runs a
/// PowerShell script with no window). The watcher stays resident for the whole logon
/// session and applies the affinity mask to every new Fortnite process instance, so a
/// relaunch is covered too. Installing it from the app also starts it immediately, so
/// the feature works in the current session rather than only after the next logon.
///
/// Scope and safety (hard boundaries):
///   • Does NOT disable SMT or physical cores, modify BIOS/firmware, the registry,
///     Fortnite files, Windows power settings, CPU priority, GPU settings, or global
///     CPU affinity.
///   • Only the Swiftwave-owned Startup entry is created/removed; unrelated Startup
///     entries are never touched.
///   • Switching OFF stops the resident watcher and never forcibly modifies an
///     already-running Fortnite process.
///   • Detection is based on the actual Startup artifact + script state (exact content
///     match), never a saved boolean: external deletion/modification is reported truthfully.
///   • Per-user shell:startup only — no administrator elevation required.
/// </summary>
public sealed class FortniteAffinityOptimization : Optimization
{
    public override string Id => "fortnite-affinity";
    public override string Name => "Fortnite specific optimizations";
    public override string Description =>
        "Excludes logical CPU 0 from Fortnite’s process affinity. A hidden per-user Startup watcher keeps running for your session and applies the mask every time Fortnite launches — including relaunches.";
    public override string Category => "Gaming";
    public override Safety Safety => Safety.Safe;
    public override bool RequiresAdmin => false;

    /// <summary>Content/version marker — bump when the script logic changes so stale scripts are detected.</summary>
    private const string ScriptMarker = "# SWIFTWAVE-FORTNITE-AFFINITY v2";

    private static string ScriptPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SwiftwaveTweaks", "FortniteAffinity.ps1");

    private static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup),
        "Swiftwave Fortnite Affinity.lnk");

    /// <summary>
    /// The production watcher script. No console output; a session-scoped mutex keeps exactly
    /// one instance alive, the loop stays resident for the logon session, and each new Fortnite
    /// process instance gets the affinity mask applied once (PID-tracked, so relaunches — and
    /// recycled PIDs — are covered).
    /// </summary>
    private static string ScriptContent => ScriptMarker + @"
$processName = ""FortniteClient-Win64-Shipping""
$logicalProcessors = [Environment]::ProcessorCount
if ($logicalProcessors -lt 2) {
    exit 1
}
if ($logicalProcessors -gt 64) {
    exit 1
}
try {
    $allCpuMask = ([System.Numerics.BigInteger]::One -shl $logicalProcessors) - 1
    $affinityMask = $allCpuMask -bxor 1
    $affinityMaskIntPtr = [IntPtr]::new([long]$affinityMask)
}
catch {
    exit 1
}
# Single-instance guard: exactly one watcher per logon session, even if the Startup
# entry and an in-app install race each other.
$watcherMutex = New-Object System.Threading.Mutex($false, 'Local\SwiftwaveTweaks.FortniteAffinity')
$watcherOwned = $false
try {
    $watcherOwned = $watcherMutex.WaitOne(0)
}
catch [System.Threading.AbandonedMutexException] {
    # A previous watcher crashed while holding the mutex; ownership transferred to us.
    $watcherOwned = $true
}
if (-not $watcherOwned) {
    exit 0
}
# Stays resident for the whole logon session and applies the mask to every new
# Fortnite process instance, so a relaunch is covered as well.
$appliedIds = @{}
while ($true) {
    $fortniteProcesses = Get-Process -Name $processName -ErrorAction SilentlyContinue
    if ($fortniteProcesses) {
        $liveIds = @{}
        foreach ($process in $fortniteProcesses) {
            $liveIds[$process.Id] = $true
            if (-not $appliedIds.ContainsKey($process.Id)) {
                try {
                    $process.ProcessorAffinity = $affinityMaskIntPtr
                    $verifiedMask = $process.ProcessorAffinity.ToInt64()
                    if (($verifiedMask -band 1) -eq 0) {
                        # Verified — remember this instance so it is not touched again.
                        $appliedIds[$process.Id] = $true
                    }
                }
                catch {
                    # Silent background failure
                }
            }
        }
        # Forget instances that exited, so a recycled PID is treated as a new game.
        foreach ($id in @($appliedIds.Keys)) {
            if (-not $liveIds.ContainsKey($id)) {
                $appliedIds.Remove($id)
            }
        }
    }
    Start-Sleep -Milliseconds 500
}
";

    private sealed record ArtifactState(bool ShortcutExists, bool ShortcutValid, bool ScriptExists, bool ScriptCurrent, string Detail);

    private static string Normalize(string text) => text.Replace("\r\n", "\n");

    private static ArtifactState Inspect()
    {
        bool shortcutExists = File.Exists(ShortcutPath);
        bool shortcutValid = false;
        if (shortcutExists)
        {
            try
            {
                var (target, arguments) = ReadShortcut(ShortcutPath);
                shortcutValid = target.EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase)
                    && arguments.Contains("-WindowStyle Hidden", StringComparison.OrdinalIgnoreCase)
                    && arguments.Contains(ScriptPath, StringComparison.OrdinalIgnoreCase);
            }
            catch { shortcutValid = false; }
        }

        bool scriptExists = File.Exists(ScriptPath);
        bool scriptCurrent = false;
        if (scriptExists)
        {
            // Exact content match (line-ending normalized): a marker-only check would accept a
            // tampered script that kept the marker line.
            try { scriptCurrent = Normalize(File.ReadAllText(ScriptPath)) == Normalize(ScriptContent); }
            catch { scriptCurrent = false; }
        }

        string detail = (shortcutExists, shortcutValid, scriptExists, scriptCurrent) switch
        {
            (true, true, true, true) => "Watcher installed in Startup (per-user, hidden)",
            (false, _, false, _) => "Not installed",
            (true, false, _, _) => "Startup entry was modified externally",
            (true, true, false, _) => "Startup entry present but the watcher script is missing",
            (true, true, true, false) => "Watcher script is outdated or was modified externally",
            (false, _, true, _) => "Script present but the Startup entry was removed externally",
        };
        return new ArtifactState(shortcutExists, shortcutValid, scriptExists, scriptCurrent, detail);
    }

    public override Detection Detect()
    {
        int cpus = Environment.ProcessorCount;
        if (cpus < 2 || cpus > 64)
            return new Detection(OptState.Unsupported,
                $"This feature requires 2–64 logical processors; this system reports {cpus}.", false,
                "The affinity mask cannot be computed safely for this processor count.");

        var s = Inspect();
        if (s is { ShortcutExists: true, ShortcutValid: true, ScriptExists: true, ScriptCurrent: true })
            return new Detection(OptState.Optimized, s.Detail, true, "");
        if (!s.ShortcutExists && !s.ScriptExists)
            return new Detection(OptState.NeedsAttention, s.Detail, true, "");
        // Partially present / externally modified — real state is neither cleanly on nor off.
        return new Detection(OptState.Unknown, s.Detail, true,
            "The Swiftwave Fortnite watcher is partially installed or was changed outside the app. Toggling off removes the Swiftwave entry; toggling on reinstalls it cleanly.");
    }

    protected override string CapturePrevious()
    {
        var s = Inspect();
        return JsonSerializer.Serialize(new { shortcut = s.ShortcutExists, script = s.ScriptExists });
    }

    protected override bool ApplyChange(out string detail)
    {
        detail = "";
        int cpus = Environment.ProcessorCount;
        if (cpus < 2 || cpus > 64) { detail = $"Unsupported logical processor count: {cpus}."; return false; }

        try
        {
            // 1. Stop any watcher started earlier (e.g. one running an older script version)
            //    so the refreshed logic is what runs, then write the Swiftwave-owned script.
            StopWatchers();
            Directory.CreateDirectory(Path.GetDirectoryName(ScriptPath)!);
            File.WriteAllText(ScriptPath, ScriptContent);

            // 2. Create the Startup launcher (a .lnk that explicitly launches the script
            //    through hidden PowerShell — never a bare .ps1 in Startup).
            WriteShortcut(ShortcutPath, ScriptPath);
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            return false;
        }

        // 3. Verify both artifacts.
        var s = Inspect();
        if (s is { ShortcutExists: true, ShortcutValid: true, ScriptExists: true, ScriptCurrent: true })
        {
            // 4. Start the watcher for the current session — a fresh install otherwise only
            //    takes effect at the next logon. The script's session mutex makes this safe
            //    even if a logon-started instance already exists (the duplicate exits).
            StartWatcher();
            detail = "Startup watcher installed and verified; it is running for this session.";
            return true;
        }
        detail = "Verification failed after install: " + s.Detail;
        return false;
    }

    public override Detection DetectAfterApply() => Detect();

    public override bool Revert(string previousState, out string detail)
    {
        detail = "";
        try
        {
            // Stop the resident watcher first, then remove only the Swiftwave-owned artifacts
            // (Startup entry + our script); unrelated Startup entries and user files are never touched.
            StopWatchers();
            if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
            if (File.Exists(ScriptPath)) File.Delete(ScriptPath);
        }
        catch (Exception ex) { detail = ex.Message; return false; }

        if (File.Exists(ShortcutPath)) { detail = "The Startup entry could not be removed."; return false; }
        detail = "Startup watcher removed and the watcher process (if running) was stopped. A running Fortnite process was left untouched.";
        return true;
    }

    /// <summary>Starts the watcher immediately for the current session (hidden, per-user, no elevation).</summary>
    private static void StartWatcher()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{ScriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            SafeLog.Write(process is null
                ? "Fortnite watcher could not be started for this session; it will run at the next logon."
                : "Fortnite watcher started for this session.");
        }
        catch (Exception ex) { SafeLog.Write("Fortnite watcher start failed.", ex); }
    }

    /// <summary>
    /// Stops any resident Fortnite watcher, identified by its command line, excluding this
    /// helper process itself ($PID guard). Runs without elevation — the watcher is per-user.
    /// </summary>
    private static void StopWatchers()
    {
        try
        {
            ProcessRunner.RunPowerShell(
                "$ErrorActionPreference='SilentlyContinue'; " +
                "Get-CimInstance Win32_Process -Filter \"Name='powershell.exe'\" | " +
                "Where-Object { $_.ProcessId -ne $PID -and $_.CommandLine -like '*FortniteAffinity.ps1*' } | " +
                "ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }");
        }
        catch { }
    }

    /// <summary>Creates the .lnk launcher through the WScript.Shell COM interface (no elevation needed for the per-user Startup folder).</summary>
    private static void WriteShortcut(string lnkPath, string scriptPath)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell is unavailable on this system.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic lnk = shell.CreateShortcut(lnkPath);
        // -WindowStyle Hidden plus shortcut "Run: Minimized" (7): no window, no console, no flash.
        // -ExecutionPolicy Bypass applies to this process only — the global policy is untouched.
        lnk.TargetPath = "powershell.exe";
        lnk.Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"";
        lnk.WorkingDirectory = Path.GetDirectoryName(scriptPath);
        lnk.WindowStyle = 7;
        lnk.Description = "Swiftwave Tweaks — Fortnite affinity watcher";
        lnk.Save();
    }

    /// <summary>Reads back an existing .lnk for verification (target + arguments).</summary>
    private static (string Target, string Arguments) ReadShortcut(string lnkPath)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell is unavailable on this system.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic lnk = shell.CreateShortcut(lnkPath);
        return ((string)lnk.TargetPath, (string)lnk.Arguments);
    }
}
