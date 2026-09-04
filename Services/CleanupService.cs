using System.IO;
using System.Linq;

namespace SwiftwaveTweaks.Services;

public sealed record CleanupCategory(string Id, string Name, string Description, bool RequiresAdmin, double SizeMb, string MeasureNote);

/// <summary>
/// Safe cleanup over a strictly limited set of categories, with a measured estimate before
/// deletion and a real after-measurement used as verification. (spec 23)
/// Never touches documents, downloads, games, system files, browser profiles or app data.
/// </summary>
public static class CleanupService
{
    private static double MeasureDir(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return 0;
            return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } }) / 1_048_576.0;
        }
        catch { return 0; }
    }

    public static List<CleanupCategory> Measure()
    {
        var list = new List<CleanupCategory>
        {
            new("user-temp", "User temporary files",
                "Files in your per-user Temp folder that are not currently in use.", false,
                MeasureDir(Path.Combine(Path.GetTempPath())), ""),
            new("windows-temp", "Windows temporary files",
                "Files in the system Temp folder (C:\\Windows\\Temp).", true,
                0, "Size is measured during elevation."),
            new("recycle-bin", "Recycle Bin",
                "Empties the Recycle Bin for all drives. Deleted items cannot be recovered afterwards.", false,
                0, "Measured during cleanup."),
        };
        return list;
    }

    /// <summary>Cleans the given category and returns the verified reclaimed amount. Never pretends success.</summary>
    public static (bool Success, double ReclaimedMb, string Message) Clean(CleanupCategory category)
    {
        try
        {
            switch (category.Id)
            {
                case "user-temp":
                {
                    var path = Path.GetTempPath();
                    double before = MeasureDir(path);
                    long failed = 0;
                    foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                        try { File.Delete(f); } catch { failed++; }
                    foreach (var d in Directory.EnumerateDirectories(path))
                        try { Directory.Delete(d, true); } catch { failed++; }
                    double after = MeasureDir(path);
                    double reclaimed = Math.Max(0, before - after);
                    bool ok = reclaimed > 0 || failed > 0; // in-use files legitimately block deletion
                    SafeLog.Write($"Cleanup user-temp: {reclaimed:N1} MB reclaimed, {failed} items skipped");
                    return (ok, reclaimed, ok
                        ? $"{reclaimed:N1} MB reclaimed; {failed} in-use item(s) left in place."
                        : "Nothing to remove.");
                }
                case "windows-temp":
                {
                    var script = "$p='C:\\Windows\\Temp\\*'; $b=(Get-ChildItem $p -Recurse -Force -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum; " +
                                 "Get-ChildItem $p -Recurse -Force -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue; " +
                                 "$a=(Get-ChildItem $p -Recurse -Force -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum; \"RESULT:{0}\" -f [math]::Round(($b-$a)/1MB,1)";
                    var r = ProcessRunner.RunElevatedPowerShellWithOutput(script);
                    var m = System.Text.RegularExpressions.Regex.Match(r.Output, @"RESULT:(-?[\d.]+)");
                    if (r.ExitCode == 0 && m.Success)
                    {
                        double mb = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                        SafeLog.Write($"Cleanup windows-temp: {mb:N1} MB reclaimed");
                        return (mb > 0, Math.Max(0, mb), $"{mb:N1} MB reclaimed.");
                    }
                    return (false, 0, "The elevated cleanup failed or was cancelled" + (r.Output.Trim().Length > 0 ? ": " + r.Output.Trim().Split('\n')[0] : "."));
                }
                case "recycle-bin":
                {
                    var before = MeasureRecycleBin();
                    var r = ProcessRunner.RunPowerShell("Clear-RecycleBin -Force -ErrorAction SilentlyContinue");
                    var after = MeasureRecycleBin();
                    double reclaimed = Math.Max(0, before - after);
                    SafeLog.Write($"Cleanup recycle-bin: {reclaimed:N1} MB reclaimed");
                    return (r.ExitCode == 0 && reclaimed > 0, reclaimed, reclaimed > 0 ? $"{reclaimed:N1} MB reclaimed." : "The Recycle Bin was already empty.");
                }
                default:
                    return (false, 0, "Unknown cleanup category.");
            }
        }
        catch (Exception ex)
        {
            SafeLog.Write($"Cleanup failed: {category.Id}", ex);
            return (false, 0, "Cleanup failed: " + ex.Message);
        }
    }

    private static double MeasureRecycleBin()
    {
        var r = ProcessRunner.RunPowerShell(
            "(New-Object -ComObject Shell.Application).Namespace(0xA).Items() | ForEach-Object { $_.ExtendedProperty('System.Size') } | Measure-Object -Sum | ForEach-Object { [math]::Round($_.Sum/1MB,1) }");
        return r.ExitCode == 0 && double.TryParse(r.Output.Trim(), System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
}
