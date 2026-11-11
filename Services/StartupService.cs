using System.Text.Json;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;

namespace SwiftwaveTweaks.Services;

public sealed record StartupEntry(string Name, string Command, string Scope, string Classification, bool Enabled)
{
    public const string Important = "Important system component";
    public const string Probably = "Probably unnecessary";
    public const string UserDecision = "User decision";
    public const string Unknown = "Unknown";
}

/// <summary>
/// Startup manager over the documented Run keys with reversible disable
/// (values are backed up under HKCU\Software\SwiftwaveTweaks\StartupBackup). (spec 20)
/// </summary>
public static class StartupService
{
    private const string BackupKey = @"Software\SwiftwaveTweaks\StartupBackup";
    /// <summary>Prefix of the companion backup value that stores the original RegistryValueKind.</summary>
    private const string KindSuffix = "$kind$";
    private static readonly string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static readonly string[] ImportantNames =
    {
        "securityhealth", "windowsdefender", "msmpeng", "nvidia", "nvbackend", "nvcplui", "realtek", "razer",
        "intel", "amd", "synaptics", "elan", "audio", "realtekhd", "logitech", "wacom", "stealth", "corsair"
    };
    private static readonly string[] ProbablyUnnecessary =
    {
        "spotify", "discord", "epic", "steam", "adobe", "skype", "teams", "zoom", "updater", "java", "quickset",
        "ccxprocess", "creative cloud", "epson", "hp ", "onedrive"
    };

    public static string Classify(string name, string command)
    {
        var n = name.ToLowerInvariant() + " " + command.ToLowerInvariant();
        if (ImportantNames.Any(n.Contains)) return StartupEntry.Important;
        if (ProbablyUnnecessary.Any(n.Contains)) return StartupEntry.Probably;
        return StartupEntry.UserDecision;
    }

    public static List<StartupEntry> Enumerate()
    {
        var list = new List<StartupEntry>();
        void Collect(RegistryHive hive, RegistryView view, string scope)
        {
            try
            {
                using var key = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(RunPath);
                if (key is null) return;
                foreach (var name in key.GetValueNames())
                {
                    // The app's own entry is owned by the Settings preference (SelfStartup); listing
                    // it here would give the same entry two conflicting controls.
                    if (hive == RegistryHive.CurrentUser &&
                        string.Equals(name, SelfStartup.ValueName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var command = key.GetValue(name)?.ToString() ?? "";
                    list.Add(new StartupEntry(name, command, scope, Classify(name, command), true));
                }
            }
            catch { }
        }
        Collect(RegistryHive.CurrentUser, RegistryView.Default, "User");
        Collect(RegistryHive.LocalMachine, RegistryView.Registry64, "Machine");

        // Disabled-by-this-tool entries
        try
        {
            using var backup = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default).OpenSubKey(BackupKey);
            if (backup is not null)
                foreach (var scope in backup.GetSubKeyNames())
                    using (var sk = backup.OpenSubKey(scope))
                    {
                        if (sk is null) continue;
                        foreach (var name in sk.GetValueNames())
                        {
                            if (name.StartsWith(KindSuffix, StringComparison.Ordinal)) continue; // kind metadata, not an entry
                            list.Add(new StartupEntry(name, sk.GetValue(name)?.ToString() ?? "", scope, Classify(name, sk.GetValue(name)?.ToString() ?? ""), false));
                        }
                    }
        }
        catch { }

        return list.OrderBy(e => e.Enabled ? 0 : 1).ThenBy(e => e.Name).ToList();
    }

    /// <summary>Disables a startup entry after verifying it is not a critical component. Returns a truthful result message.</summary>
    public static (bool Success, string Message) Disable(StartupEntry entry)
    {
        if (entry.Classification == StartupEntry.Important)
            return (false, "Refused: this entry looks like an important system or driver component. It will not be modified automatically.");

        // Capture the original value type so re-enabling can restore it exactly
        // (Run values are frequently REG_EXPAND_SZ and must not be downgraded to REG_SZ).
        RegistryValueKind kind = RegistryValueKind.String;
        try
        {
            var hive = entry.Scope == "User" ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;
            var view = entry.Scope == "User" ? RegistryView.Default : RegistryView.Registry64;
            using var key = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(RunPath);
            kind = key?.GetValueKind(entry.Name) ?? RegistryValueKind.String;
        }
        catch { kind = RegistryValueKind.String; }

        // Backup (value + original kind). The backup is removed again whenever the
        // disable does not fully succeed, so a cancelled elevation can never leave a
        // phantom "Disabled" entry behind.
        try
        {
            using var backup = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default).CreateSubKey($"{BackupKey}\\{entry.Scope}");
            backup!.SetValue(entry.Name, entry.Command);
            backup.SetValue(KindSuffix + entry.Name, (int)kind, RegistryValueKind.DWord);
        }
        catch (Exception ex) { return (false, "Could not record backup: " + ex.Message); }

        void AbandonBackup()
        {
            try
            {
                using var b = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default).OpenSubKey($"{BackupKey}\\{entry.Scope}", writable: true);
                b?.DeleteValue(entry.Name, throwOnMissingValue: false);
                b?.DeleteValue(KindSuffix + entry.Name, throwOnMissingValue: false);
            }
            catch { }
        }

        bool removed;
        if (entry.Scope == "User")
        {
            try { using var key = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default).OpenSubKey(RunPath, writable: true); key!.DeleteValue(entry.Name, false); removed = true; }
            catch (Exception ex) { AbandonBackup(); return (false, "Could not disable entry: " + ex.Message); }
        }
        else
        {
            var script = $"Remove-ItemProperty -Path 'HKLM:\\{RunPath}' -Name '{entry.Name.Replace("'", "''")}' -ErrorAction Stop; \"WRITE:$?\"";
            var r = ProcessRunner.RunElevatedPowerShellWithOutput(script);
            removed = r.ExitCode == 0 && r.Output.Contains("WRITE:True");
            if (!removed) { AbandonBackup(); return (false, "Elevation was cancelled or the entry could not be removed."); }
        }

        // Independent verification
        bool gone = entry.Scope == "User"
            ? RegHelper.ReadValue(RegistryHive.CurrentUser, RegistryView.Default, RunPath, entry.Name) is null
            : RegHelper.ReadValue(RegistryHive.LocalMachine, RegistryView.Registry64, RunPath, entry.Name) is null;
        bool backed = RegHelper.ReadString(RegistryHive.CurrentUser, RegistryView.Default, $"{BackupKey}\\{entry.Scope}", entry.Name) is not null;
        if (gone && backed) { SafeLog.Write($"Startup disabled: {entry.Name} ({entry.Scope})"); return (true, $"Disabled and verified: {entry.Name}"); }
        AbandonBackup();
        SafeLog.Write($"Startup disable verification failed: {entry.Name}");
        return (false, $"The change could not be verified (present: {!gone}, backup: {backed}).");
    }

    /// <summary>Re-enables a previously disabled entry from the backup, restoring the original value type.</summary>
    public static (bool Success, string Message) Enable(StartupEntry entry)
    {
        string command = RegHelper.ReadString(RegistryHive.CurrentUser, RegistryView.Default, $"{BackupKey}\\{entry.Scope}", entry.Name) ?? "";
        if (command.Length == 0) return (false, "No backup recorded for this entry.");

        var kindInt = RegHelper.ReadDword(RegistryHive.CurrentUser, RegistryView.Default, $"{BackupKey}\\{entry.Scope}", KindSuffix + entry.Name);
        var kind = kindInt is int k && Enum.IsDefined(typeof(RegistryValueKind), k) ? (RegistryValueKind)k : RegistryValueKind.String;
        if (kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString)) kind = RegistryValueKind.String;

        bool written;
        if (entry.Scope == "User")
        {
            try { RegHelper.WriteStringAsKind(RegistryHive.CurrentUser, RunPath, entry.Name, command, kind); written = true; }
            catch (Exception ex) { return (false, ex.Message); }
        }
        else
        {
            var propertyType = kind == RegistryValueKind.ExpandString ? "ExpandString" : "String";
            var script = $"New-ItemProperty -Path 'HKLM:\\{RunPath}' -Name '{entry.Name.Replace("'", "''")}' -Value '{command.Replace("'", "''")}' -PropertyType {propertyType} -Force | Out-Null; \"WRITE:$?\"";
            var r = ProcessRunner.RunElevatedPowerShellWithOutput(script);
            written = r.ExitCode == 0 && r.Output.Contains("WRITE:True");
            if (!written) return (false, "Elevation was cancelled or the entry could not be restored.");
        }

        bool present = entry.Scope == "User"
            ? RegHelper.ReadValue(RegistryHive.CurrentUser, RegistryView.Default, RunPath, entry.Name) is not null
            : RegHelper.ReadValue(RegistryHive.LocalMachine, RegistryView.Registry64, RunPath, entry.Name) is not null;
        if (present)
        {
            RegHelper.DeleteValue(RegistryHive.CurrentUser, $"{BackupKey}\\{entry.Scope}", entry.Name);
            RegHelper.DeleteValue(RegistryHive.CurrentUser, $"{BackupKey}\\{entry.Scope}", KindSuffix + entry.Name);
            SafeLog.Write($"Startup enabled: {entry.Name} ({entry.Scope})");
            return (true, $"Enabled and verified: {entry.Name}");
        }
        return (false, "The change could not be verified.");
    }
}
