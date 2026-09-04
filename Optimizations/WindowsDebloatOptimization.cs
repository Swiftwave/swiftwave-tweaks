using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Win32;
using SwiftwaveTweaks.Services;

namespace SwiftwaveTweaks.Core;

/// <summary>
/// Reduces Windows suggestions, tips and promotional content by disabling the individual
/// ContentDeliveryManager suggestion surfaces (HKCU DWORDs). Each surface is a separate,
/// documented Windows configuration key; disabling it turns off that specific suggestion
/// type without broadly disabling unrelated Windows functionality.
///
/// This is the only debloat feature exposed in the UI because it is the only one with a
/// safe, documented, detectable, reversible, and independently verifiable mechanism on this
/// Windows build. Background-activity reduction, preinstalled-app background control, and
/// the broad ContentDeliveryAllowed master toggle are intentionally NOT exposed:
///   • No single safe global "disable background apps" toggle exists in Windows 11 25H2.
///   • Per-app background permissions are managed through Settings, not a single registry key.
///   • ContentDeliveryAllowed is too broad and could disable legitimate content delivery.
///
/// Scope and safety:
///   • Only HKCU ContentDeliveryManager suggestion keys are modified — nothing under HKLM.
///   • No Windows services are disabled.
///   • No Windows components are removed.
///   • No Microsoft Store apps are uninstalled.
///   • No security, Windows Update, Defender, Firewall, UAC, Xbox, or core settings touched.
///   • Each key's exact previous value (including absence) is captured and restorable.
///   • Detection reads the live state; the UI reflects the real system, never cached state.
/// </summary>
public sealed class WindowsSuggestionsOptimization : Optimization
{
    private const string CdmPath = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";

    /// <summary>
    /// Each ContentDeliveryManager suggestion key, with a human-readable label.
    /// Target value is always 0 (disabled/optimized) for every key.
    /// </summary>
    private static readonly (string ValueName, string Label)[] SuggestionKeys = new[]
    {
        ("SubscribedContent-338389Enabled",  "Windows tips"),
        ("SubscribedContent-338388Enabled",  "Lock screen tips and background"),
        ("SubscribedContent-310093Enabled",  "Welcome experiences after updates"),
        ("SubscribedContent-338393Enabled",  "Suggested apps"),
        ("SubscribedContent-353694Enabled",  "Get tips"),
        ("SubscribedContent-353696Enabled",  "Get even more out of Windows"),
        ("SoftLandingEnabled",              "Get Started tips"),
        ("SystemPaneSuggestionsEnabled",    "System pane suggestions"),
        ("SilentInstalledAppsEnabled",      "Silent app installation"),
    };

    public override string Id => "windows-suggestions";
    public override string Name => "Reduce Windows suggestions, tips and promotional content";
    public override string Description =>
        "Turns off individual Windows suggestion surfaces (lock screen tips, welcome experiences, suggested apps, Get Started, system tips, silent app installs) through their documented HKCU ContentDeliveryManager keys. " +
        "Each key is detected and verified independently; your previous values are captured and restorable. " +
        "This does NOT disable Windows Update, Defender, Firewall, UAC, Xbox services, or any core Windows functionality — and no apps or components are removed.";
    public override string Category => "Gaming";
    public override Safety Safety => Safety.Safe;
    public override bool RequiresAdmin => false;

    public override Detection Detect()
    {
        var readings = ReadAll();
        int enabledCount = readings.Count(r => r.Value == 1);
        if (readings.Count == 0)
            return new Detection(OptState.Unsupported, "ContentDeliveryManager keys not found on this Windows build.", false, "");
        if (enabledCount == 0)
            return new Detection(OptState.Optimized, "All suggestion surfaces disabled", true, "");
        var enabledLabels = readings.Where(r => r.Value == 1).Select(r => r.Label);
        return new Detection(OptState.NeedsAttention,
            $"{enabledCount} suggestion surface(s) still enabled: {string.Join(", ", enabledLabels)}",
            true, "");
    }

    protected override string CapturePrevious()
    {
        var readings = ReadAll();
        var data = readings.Select(r => new { k = r.ValueName, v = r.Value?.ToString() });
        return JsonSerializer.Serialize(new { settings = data });
    }

    protected override bool ApplyChange(out string detail)
    {
        detail = "";
        var readings = ReadAll();
        var toDisable = readings.Where(r => r.Value == 1).ToList();
        if (toDisable.Count == 0) { detail = "All suggestion surfaces are already disabled."; return true; }

        var failures = new List<string>();
        foreach (var r in toDisable)
        {
            try { RegHelper.WriteDword(RegistryHive.CurrentUser, CdmPath, r.ValueName, 0); }
            catch (System.Exception ex) { failures.Add($"{r.Label}: {ex.Message}"); }
        }

        // Independent re-read to verify every key is now 0.
        var afterRead = ReadAll();
        var stillOn = afterRead.Where(r => r.Value == 1).ToList();
        if (stillOn.Count > 0)
        {
            detail = $"{stillOn.Count} key(s) could not be verified as disabled: {string.Join(", ", stillOn.Select(r => r.Label))}.";
            return false;
        }
        detail = $"Disabled {toDisable.Count} suggestion surface(s); verified all are now off.";
        return failures.Count == 0;
    }

    public override Detection DetectAfterApply() => Detect();

    public override bool Revert(string previousState, out string detail)
    {
        detail = "";
        try
        {
            var doc = JsonDocument.Parse(previousState);
            if (!doc.RootElement.TryGetProperty("settings", out var settingsArr))
            { detail = "No previous settings found in captured state."; return false; }

            foreach (var el in settingsArr.EnumerateArray())
            {
                string name = el.GetProperty("k").GetString() ?? "";
                if (string.IsNullOrEmpty(name)) continue;
                if (el.GetProperty("v").ValueKind == JsonValueKind.Null)
                    RegHelper.DeleteValue(RegistryHive.CurrentUser, CdmPath, name);
                else
                {
                    int val = el.GetProperty("v").GetInt32();
                    RegHelper.WriteDword(RegistryHive.CurrentUser, CdmPath, name, val);
                }
            }

            // Independent re-read to verify restoration.
            var after = ReadAll();
            var mismatches = new List<string>();
            foreach (var el in settingsArr.EnumerateArray())
            {
                string name = el.GetProperty("k").GetString() ?? "";
                var now = after.FirstOrDefault(r => r.ValueName == name);
                if (el.GetProperty("v").ValueKind == JsonValueKind.Null)
                {
                    if (now is not null && now.Value is not null)
                        mismatches.Add($"{name}: expected absent, got {now.Value}");
                }
                else
                {
                    int expected = el.GetProperty("v").GetInt32();
                    int? actual = now?.Value;
                    if (actual != expected)
                        mismatches.Add($"{name}: expected {expected}, got {actual?.ToString() ?? "absent"}");
                }
            }
            if (mismatches.Count > 0)
            { detail = $"Revert verification failed: {string.Join("; ", mismatches)}"; return false; }
            detail = "All suggestion surfaces restored to their previous values.";
            return true;
        }
        catch (System.Exception ex)
        {
            SafeLog.Write("Windows suggestions revert failed.", ex);
            detail = ex.Message;
            return false;
        }
    }

    private record KeyReading(string ValueName, string Label, int? Value);

    private static List<KeyReading> ReadAll()
    {
        var list = new List<KeyReading>();
        foreach (var (name, label) in SuggestionKeys)
        {
            int? v = RegHelper.ReadDword(RegistryHive.CurrentUser, RegistryView.Default, CdmPath, name);
            list.Add(new KeyReading(name, label, v));
        }
        return list;
    }
}
