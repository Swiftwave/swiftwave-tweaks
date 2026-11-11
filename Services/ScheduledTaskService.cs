namespace SwiftwaveTweaks.Services;

/// <summary>
/// Thin wrapper over the Task Scheduler for enable/disable operations. Uses PowerShell
/// (Disable-ScheduledTask / Enable-ScheduledTask / Get-ScheduledTask) because the
/// managed Microsoft.Win32.TaskScheduler assembly is not present as a NuGet dependency
/// in this project and schtasks.exe parsing is brittle with paths containing spaces.
///
/// Every task is identified by its *full path* under the root:
///   "\Microsoft\Windows\Windows Error Reporting\QueueReporting"
/// This matches what Hone wrote via the COM API §3.8 C.2 and avoids ambiguity when
/// two tasks with the same name live in different folders.
/// </summary>
public static class ScheduledTaskService
{
    private static Dictionary<string, bool?>? _cache;
    private static DateTime _cacheAtUtc;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Loads every task's Enabled state in one PowerShell call. Detect() over 30+ tasks must not
    /// spawn 30+ processes; the cache is valid for <see cref="CacheTtl"/> and invalidated by writes.
    /// </summary>
    public static void PrimeCache(IEnumerable<string> fullPaths)
    {
        try
        {
            var (code, outp) = ProcessRunner.RunPowerShell(
                "Get-ScheduledTask | ForEach-Object { ($_.TaskPath + $_.TaskName) + '|' + $_.State }");
            if (code != 0) return;
            var map = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in outp.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                int split = line.LastIndexOf('|');
                if (split <= 0) continue;
                string path = line[..split];
                string state = line[(split + 1)..].Trim();
                map[path] = !string.Equals(state, "Disabled", StringComparison.OrdinalIgnoreCase);
            }
            _cache = map;
            _cacheAtUtc = DateTime.UtcNow;
        }
        catch { }
    }

    private static void Invalidate() => _cache = null;

    /// <summary>True if a task with the given full path exists on this system.</summary>
    public static bool Exists(string fullPath)
    {
        if (_cache is not null && DateTime.UtcNow - _cacheAtUtc < CacheTtl)
            return _cache.ContainsKey(fullPath);
        var (code, outp) = ProcessRunner.RunPowerShell(
            $"if ($null -ne (Get-ScheduledTask -TaskPath '{DirOf(fullPath)}' -TaskName '{NameOf(fullPath)}' -ErrorAction SilentlyContinue)) {{ 'YES' }} else {{ 'NO' }}");
        return code == 0 && outp.Contains("YES");
    }

    /// <summary>Current Enabled state of a task. Returns null if the task does not exist or the query failed.</summary>
    public static bool? IsEnabled(string fullPath)
    {
        if (_cache is not null && DateTime.UtcNow - _cacheAtUtc < CacheTtl)
            return _cache.TryGetValue(fullPath, out var cached) ? cached : null;

        var (code, outp) = ProcessRunner.RunPowerShell($@"
$task = Get-ScheduledTask -TaskPath '{DirOf(fullPath)}' -TaskName '{NameOf(fullPath)}' -ErrorAction SilentlyContinue
if (-not $task) {{ 'MISSING' }}
else {{ if ($task.State -eq 'Disabled') {{ 'OFF' }} else {{ 'ON' }} }}
");
        if (code != 0) return null;
        var trimmed = outp.Trim();
        return trimmed switch
        {
            "OFF" => false,
            "ON" => true,
            _ => null
        };
    }

    /// <summary>
    /// Disables the task. Returns true if the task was disabled or was already disabled.
    /// Returns null if the task does not exist (caller should treat as "not applicable").
    /// </summary>
    public static bool? Disable(string fullPath)
    {
        Invalidate();
        var (code, outp) = ProcessRunner.RunPowerShell($@"
$task = Get-ScheduledTask -TaskPath '{DirOf(fullPath)}' -TaskName '{NameOf(fullPath)}' -ErrorAction SilentlyContinue
if (-not $task) {{ 'MISSING' }}
else {{ Disable-ScheduledTask -InputObject $task -ErrorAction Stop | Out-Null; 'OK' }}
");
        if (code != 0) return false;
        return outp.Trim() switch
        {
            "OK" => true,
            "MISSING" => null,
            _ => false
        };
    }

    /// <summary>Re-enables a previously disabled task.</summary>
    public static bool ReEnable(string fullPath)
    {
        Invalidate();
        var (code, outp) = ProcessRunner.RunPowerShell($@"
$task = Get-ScheduledTask -TaskPath '{DirOf(fullPath)}' -TaskName '{NameOf(fullPath)}' -ErrorAction SilentlyContinue
if (-not $task) {{ 'MISSING' }}
else {{ Enable-ScheduledTask -InputObject $task -ErrorAction Stop | Out-Null; 'OK' }}
");
        if (code != 0) return false;
        return outp.Trim() == "OK";
    }

    /// <summary>Deletes a task entirely (used for Hone-created tasks, not Windows stock tasks).</summary>
    public static bool Delete(string fullPath)
    {
        Invalidate();
        var (code, outp) = ProcessRunner.RunPowerShell($@"
$task = Get-ScheduledTask -TaskPath '{DirOf(fullPath)}' -TaskName '{NameOf(fullPath)}' -ErrorAction SilentlyContinue
if (-not $task) {{ 'MISSING' }}
else {{ Unregister-ScheduledTask -InputObject $task -Confirm:$false -ErrorAction Stop; 'OK' }}
");
        if (code != 0) return false;
        return outp.Trim() == "OK";
    }

    private static string DirOf(string fullPath)
    {
        int idx = fullPath.LastIndexOf('\\');
        return idx <= 0 ? "\\" : fullPath[..idx];
    }

    private static string NameOf(string fullPath)
    {
        int idx = fullPath.LastIndexOf('\\');
        return idx < 0 ? fullPath : fullPath[(idx + 1)..];
    }
}
