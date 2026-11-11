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
            foreach (var raw in Clean(outp).Split('\n'))
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

    /// <summary>
    /// Applies a consecutive group of task state changes in one PowerShell process. Results are
    /// true for success/already in the desired state, null for missing or ACL-protected tasks,
    /// and false for a real failure. Processing stops at the first failure so later tasks cannot
    /// be changed without being included in partial-application rollback.
    /// </summary>
    public static IReadOnlyList<bool?> SetEnabled(IReadOnlyList<(string FullPath, bool Enabled)> tasks)
    {
        var results = new bool?[tasks.Count];
        if (tasks.Count == 0) return results;

        var specs = string.Join("\n", tasks.Select(task =>
            $"    @{{ Path = '{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(task.FullPath))}'; Enable = {(task.Enabled ? "$true" : "$false")} }},"));
        var script = $@"
$tasks = @(
{specs}
)
for ($i = 0; $i -lt $tasks.Count; $i++) {{
    $fullPath = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($tasks[$i].Path))
    $split = $fullPath.LastIndexOf('\')
    $dir = if ($split -le 0) {{ '\' }} else {{ $fullPath.Substring(0, $split + 1) }}
    $name = $fullPath.Substring($split + 1)
    try {{
        $task = Get-ScheduledTask -TaskPath $dir -TaskName $name -ErrorAction SilentlyContinue
        if (-not $task) {{ ""$i|MISSING""; continue }}
        if ($tasks[$i].Enable) {{
            if ($task.State -eq 'Disabled') {{ Enable-ScheduledTask -InputObject $task -ErrorAction Stop | Out-Null }}
        }}
        elseif ($task.State -ne 'Disabled') {{ Disable-ScheduledTask -InputObject $task -ErrorAction Stop | Out-Null }}
        ""$i|OK""
    }}
    catch {{
        if ($_.Exception.ToString() -match '(?i)denied') {{ ""$i|SKIP"" }}
        else {{ ""$i|ERROR""; break }}
    }}
}}
";
        Invalidate();
        var (_, outp) = ProcessRunner.RunPowerShell(script);
        Invalidate();
        foreach (var raw in Clean(outp).Split('\n'))
        {
            var line = raw.Trim();
            int split = line.IndexOf('|');
            if (split <= 0 || !int.TryParse(line[..split], out int index) ||
                (uint)index >= (uint)results.Length) continue;
            results[index] = line[(split + 1)..].Trim() switch
            {
                "OK" => true,
                "ERROR" => false,
                _ => null
            };
        }
        return results;
    }

    /// <summary>True if a task with the given full path exists on this system.</summary>
    public static bool Exists(string fullPath)
    {
        if (_cache is not null && DateTime.UtcNow - _cacheAtUtc < CacheTtl)
            return _cache.ContainsKey(fullPath);
        var (code, outp) = ProcessRunner.RunPowerShell(
            $"if ($null -ne (Get-ScheduledTask -TaskPath '{DirOf(fullPath)}' -TaskName '{NameOf(fullPath)}' -ErrorAction SilentlyContinue)) {{ 'YES' }} else {{ 'NO' }}");
        return code == 0 && Clean(outp).Contains("YES");
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
        var trimmed = Clean(outp);
        return trimmed switch
        {
            "OFF" => false,
            "ON" => true,
            _ => null
        };
    }

    /// <summary>
    /// Disables the task. Returns true if the task was disabled or was already disabled.
    /// Returns null if the task does not exist or is protected by Windows ACLs (not applicable).
    /// </summary>
    public static bool? Disable(string fullPath)
    {
        Invalidate();
        var (code, outp) = ProcessRunner.RunPowerShell($@"
$task = Get-ScheduledTask -TaskPath '{DirOf(fullPath)}' -TaskName '{NameOf(fullPath)}' -ErrorAction SilentlyContinue
if (-not $task) {{ 'MISSING' }}
else {{ Disable-ScheduledTask -InputObject $task -ErrorAction Stop | Out-Null; 'OK' }}
");
        var cleaned = Clean(outp);
        if (cleaned == "OK") return true;
        if (cleaned == "MISSING") return null;
        // Some stock tasks are ACL-protected even for administrators. That is not a feature
        // failure: skip the task instead of aborting the whole card. Check the raw stream too,
        // because thrown errors are serialised inside the CLIXML block.
        if (outp.Contains("denied", StringComparison.OrdinalIgnoreCase)) return null;
        return false;
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
        return Clean(outp) == "OK";
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
        return Clean(outp) == "OK";
    }

    /// <summary>
    /// Strips PowerShell's redirected-progress trailer ("#&lt; CLIXML ...") and trims whitespace,
    /// so callers can compare the script's real result token instead of the raw stream.
    /// </summary>
    private static string Clean(string output)
    {
        int marker = output.IndexOf("#< CLIXML", StringComparison.Ordinal);
        if (marker >= 0) output = output[..marker];
        return output.Trim();
    }

    private static string DirOf(string fullPath)
    {
        int idx = fullPath.LastIndexOf('\\');
        // Get-ScheduledTask -TaskPath only matches when the folder ends with a backslash.
        return idx <= 0 ? "\\" : fullPath[..idx] + "\\";
    }

    private static string NameOf(string fullPath)
    {
        int idx = fullPath.LastIndexOf('\\');
        return idx < 0 ? fullPath : fullPath[(idx + 1)..];
    }
}
