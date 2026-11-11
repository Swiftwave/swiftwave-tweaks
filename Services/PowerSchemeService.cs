using System.Text.RegularExpressions;

namespace SwiftwaveTweaks.Services;

public record PowerSchemeInfo(string Guid, string Name, bool Active);

/// <summary>
/// Power-scheme lifecycle helpers. Two creation paths are supported by D-PWR:
///   - <see cref="ImportScheme"/> for the desktop "Ultimate" plan (a .pow file), and
///   - <see cref="DuplicateScheme"/> for the laptop "High Performance" copy.
///
/// Neither path reuses Hone's fixed GUID 44444444-…4452: <c>powercfg /import</c> is invoked
/// without a GUID argument, so Windows allocates a fresh one.
/// </summary>
public static class PowerSchemeService
{
    private const string ActiveSchemeKey = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";
    private const string ActiveSchemeValue = "ActivePowerScheme";

    /// <summary>Canonical Windows High Performance scheme.</summary>
    public const string HighPerformanceGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";

    // Locale-independent: match the GUID and the trailing "(Name)" rather than the English label.
    private static readonly Regex GuidToken = new(
        @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})", RegexOptions.Compiled);
    private static readonly Regex NameToken = new(@"\(([^)]*)\)", RegexOptions.Compiled);

    public static string? ReadActiveScheme() =>
        RegHelper.ReadString(Microsoft.Win32.RegistryHive.LocalMachine,
            Microsoft.Win32.RegistryView.Registry64, ActiveSchemeKey, ActiveSchemeValue);

    public static List<PowerSchemeInfo> ListSchemes()
    {
        var list = new List<PowerSchemeInfo>();
        var r = ProcessRunner.RunCapture("powercfg.exe", "/list");
        if (r is null || r.Value.ExitCode != 0) return list;
        var active = ReadActiveScheme();
        foreach (var line in r.Value.Output.Split('\n'))
        {
            var gm = GuidToken.Match(line);
            if (!gm.Success) continue;
            string g = gm.Groups[1].Value.ToLowerInvariant();
            var nm = NameToken.Match(line);
            list.Add(new PowerSchemeInfo(g, nm.Success ? nm.Groups[1].Value.Trim() : "",
                string.Equals(g, active, StringComparison.OrdinalIgnoreCase)));
        }
        return list;
    }

    public static bool Exists(string schemeGuid) =>
        ListSchemes().Any(s => string.Equals(s.Guid, schemeGuid, StringComparison.OrdinalIgnoreCase));

    public static string? GetFriendlyName(string schemeGuid) =>
        ListSchemes().FirstOrDefault(s => string.Equals(s.Guid, schemeGuid, StringComparison.OrdinalIgnoreCase))?.Name;

    /// <summary>Imports a .pow file. The optional GUID is intentionally omitted so Windows creates a new one.</summary>
    public static string? ImportScheme(string powPath)
    {
        var script = $@"
$output = & powercfg /import ""{powPath}"" 2>&1 | Out-String
Write-Output 'BEGIN_OUTPUT'
Write-Output $output
Write-Output 'END_OUTPUT'
";
        var r = ProcessRunner.RunElevatedPowerShellWithOutput(script);
        if (r.ExitCode != 0) return null;
        return FirstGuid(r.Output);
    }

    /// <summary>Duplicates an existing scheme and returns the new GUID.</summary>
    public static string? DuplicateScheme(string? sourceGuid = null)
    {
        var source = sourceGuid ?? ReadActiveScheme();
        if (source is null) return null;
        var r = ProcessRunner.RunElevatedPowerShellWithOutput($"powercfg /duplicatescheme {source}");
        if (r.ExitCode != 0) return null;
        return FirstGuid(r.Output);
    }

    public static bool RenameScheme(string schemeGuid, string friendlyName)
    {
        var r = ProcessRunner.RunElevatedPowerShellWithOutput(
            $"powercfg /changename {schemeGuid} \"{friendlyName}\"; \"WRITE:$?\"");
        return r.ExitCode == 0 && r.Output.Contains("WRITE:True");
    }

    public static bool Activate(string schemeGuid)
    {
        var r = ProcessRunner.RunElevatedPowerShellWithOutput(
            $"powercfg /setactive {schemeGuid}; \"WRITE:$?\"");
        return r.ExitCode == 0 && r.Output.Contains("WRITE:True");
    }

    public static bool DeleteScheme(string schemeGuid)
    {
        var r = ProcessRunner.RunElevatedPowerShellWithOutput(
            $"powercfg /deletescheme {schemeGuid}; \"WRITE:$?\"");
        return r.ExitCode == 0 && r.Output.Contains("WRITE:True");
    }

    private static string? FirstGuid(string output)
    {
        var m = Regex.Match(output,
            @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
        return m.Success ? m.Value.ToLowerInvariant() : null;
    }
}
