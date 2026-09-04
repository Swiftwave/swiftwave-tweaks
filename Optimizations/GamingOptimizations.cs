using System.Text.Json;
using Microsoft.Win32;
using SwiftwaveTweaks.Services;

namespace SwiftwaveTweaks.Core;

/// <summary>Shared plumbing for user-scope (HKCU) DWORD toggles with absence-aware rollback.</summary>
public abstract class HkcuDwordOptimization : Optimization
{
    protected abstract string KeyPath { get; }
    protected abstract string ValueName { get; }
    protected abstract int TargetValue { get; }
    protected abstract string StateDescription(int? value);

    private int? _previous;
    private string _previousJson = "{}";

    public override Safety Safety => Safety.Safe;
    public override bool RequiresAdmin => false;
    public override bool RequiresRestart => false;

    public override Detection Detect()
    {
        int? v = RegHelper.ReadDword(RegistryHive.CurrentUser, RegistryView.Default, KeyPath, ValueName);
        bool optimized = v == TargetValue;
        return new Detection(optimized ? OptState.Optimized : OptState.NeedsAttention, StateDescription(v), true, "");
    }

    protected override string CapturePrevious()
    {
        _previous = RegHelper.ReadDword(RegistryHive.CurrentUser, RegistryView.Default, KeyPath, ValueName);
        _previousJson = JsonSerializer.Serialize(new { value = _previous });
        return _previousJson;
    }

    protected override bool ApplyChange(out string detail)
    {
        try
        {
            RegHelper.WriteDword(RegistryHive.CurrentUser, KeyPath, ValueName, TargetValue);
            detail = "";
            return true;
        }
        catch (Exception ex) { detail = ex.Message; return false; }
    }

    public override Detection DetectAfterApply() => Detect();

    public override bool Revert(string previousState, out string detail)
    {
        detail = "";
        try
        {
            int? prev = JsonSerializer.Deserialize<JsonElement>(previousState).GetProperty("value").ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => JsonSerializer.Deserialize<JsonElement>(previousState).GetProperty("value").GetInt32()
            };
            if (prev is int v) RegHelper.WriteDword(RegistryHive.CurrentUser, KeyPath, ValueName, v);
            else RegHelper.DeleteValue(RegistryHive.CurrentUser, KeyPath, ValueName);
            return true;
        }
        catch (Exception ex) { detail = ex.Message; return false; }
    }
}

/// <summary>Disables Xbox Game DVR background recording (user scope; no Xbox components are removed).</summary>
public sealed class GameDvrOptimization : HkcuDwordOptimization
{
    private const string PolicyPath = @"SOFTWARE\Policies\Microsoft\Windows\GameDVR";
    private const string PolicyName = "AllowGameDVR";

    public override string Id => "game-dvr";
    public override string Name => "Disable Game DVR background recording";
    public override string Description => "Turns off Windows background game clip recording (Game DVR). Games may see slightly more available resources. No Xbox components are uninstalled and clip capture can still be started manually.";
    public override string Category => "Gaming";
    protected override string KeyPath => @"Software\Microsoft\Windows\CurrentVersion\GameDVR";
    protected override string ValueName => "AppCaptureEnabled";
    protected override int TargetValue => 0;

    protected override string StateDescription(int? value)
    {
        int? policy = RegHelper.ReadDword(RegistryHive.LocalMachine, RegistryView.Registry64, PolicyPath, PolicyName);
        if (policy == 0) return "Disabled (enforced by system policy)";
        bool on = value is null || value != 0;
        return on ? "Enabled (Windows default)" : "Disabled";
    }

    public override Detection Detect()
    {
        var d = base.Detect();
        if (d.State == OptState.Optimized)
        {
            int? policy = RegHelper.ReadDword(RegistryHive.LocalMachine, RegistryView.Registry64, PolicyPath, PolicyName);
            if (policy == 0) return new Detection(OptState.Optimized, "Disabled (enforced by system policy)", true, "");
        }
        return d;
    }
}

/// <summary>Disables the Xbox Game Bar overlay (Win+G).</summary>
public sealed class GameBarOptimization : HkcuDwordOptimization
{
    public override string Id => "game-bar";
    public override string Name => "Disable Xbox Game Bar overlay";
    public override string Description => "Turns off the Xbox Game Bar overlay (Win+G) and its background recording hooks. The Game Bar app itself is not removed.";
    public override string Category => "Gaming";
    protected override string KeyPath => @"Software\Microsoft\GameBar";
    protected override string ValueName => "UseNexusForGameBarEnabled";
    protected override int TargetValue => 0;
    protected override string StateDescription(int? value) =>
        value is null ? "Enabled (Windows default)" : value == 0 ? "Disabled" : "Enabled";
}

/// <summary>Ensures Windows Game Mode is enabled (recommended default) — it is not disabled by this tool.</summary>
public sealed class GameModeOptimization : HkcuDwordOptimization
{
    public override string Id => "game-mode";
    public override string Name => "Ensure Game Mode is enabled";
    public override string Description => "Windows Game Mode prioritises game processes and stabilises background activity. Microsoft recommends keeping it enabled; disabling is only justified by specific compatibility problems, so this tool does not offer to disable it.";
    public override string Category => "Gaming";
    protected override string KeyPath => @"Software\Microsoft\GameBar";
    protected override string ValueName => "AutoGameModeEnabled";
    protected override int TargetValue => 1;
    protected override string StateDescription(int? value) =>
        value is null ? "Enabled (Windows default)" : value == 1 ? "Enabled" : "Disabled";
}

/// <summary>Hardware-accelerated GPU scheduling (HAGS) toggle with restart requirement and honest expectations.</summary>
public sealed class HagsOptimization : Optimization
{
    private const string KeyPath = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
    private const string ValueName = "HwSchMode";

    public override string Id => "hags";
    public override string Name => "Hardware-accelerated GPU scheduling";
    public override string Description => "Lets the GPU manage its own video memory scheduling. Effects vary by GPU, driver, game and Windows build — this is not a guaranteed FPS improvement. A restart is required after changing it.";
    public override string Category => "Gaming";
    public override Safety Safety => Safety.Moderate;
    public override bool RequiresAdmin => true;
    public override bool RequiresRestart => true;

    private int? _previous;
    private string _previousJson = "{}";

    public override Detection Detect()
    {
        int? v = RegHelper.ReadDword(RegistryHive.LocalMachine, RegistryView.Registry64, KeyPath, ValueName);
        return v switch
        {
            2 => new Detection(OptState.Optimized, "Enabled", true, ""),
            1 => new Detection(OptState.NeedsAttention, "Disabled", true, ""),
            _ => new Detection(OptState.NeedsAttention, "Not configured (Windows/driver default)", true, "")
        };
    }

    protected override string CapturePrevious()
    {
        _previous = RegHelper.ReadDword(RegistryHive.LocalMachine, RegistryView.Registry64, KeyPath, ValueName);
        _previousJson = JsonSerializer.Serialize(new { value = _previous });
        return _previousJson;
    }

    protected override bool ApplyChange(out string detail)
    {
        var r = ProcessRunner.RunElevatedPowerShellWithOutput(
            $"New-Item -Path 'HKLM:\\{KeyPath}' -Force | Out-Null; Set-ItemProperty -Path 'HKLM:\\{KeyPath}' -Name {ValueName} -Type DWord -Value 2; \"WRITE:$?\"");
        detail = r.Output.Trim();
        return r.ExitCode == 0 && r.Output.Contains("WRITE:True");
    }

    public override Detection DetectAfterApply() => Detect();

    public override bool Revert(string previousState, out string detail)
    {
        detail = "";
        try
        {
            int? prev = JsonSerializer.Deserialize<JsonElement>(previousState).GetProperty("value").ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => JsonSerializer.Deserialize<JsonElement>(previousState).GetProperty("value").GetInt32()
            };
            var script = prev is int v
                ? $"Set-ItemProperty -Path 'HKLM:\\{KeyPath}' -Name {ValueName} -Type DWord -Value {v}; \"WRITE:$?\""
                : $"Remove-ItemProperty -Path 'HKLM:\\{KeyPath}' -Name {ValueName} -ErrorAction SilentlyContinue; \"WRITE:$?\"";
            var r = ProcessRunner.RunElevatedPowerShellWithOutput(script);
            bool ok = r.ExitCode == 0 && r.Output.Contains("WRITE:True");
            detail = ok ? "Previous HAGS state restored." : "Elevated restore failed or was cancelled.";
            return ok;
        }
        catch (Exception ex) { detail = ex.Message; return false; }
    }
}

/// <summary>Honest placeholder: NVIDIA profile manipulation requires NVAPI; it is not automated by this tool.</summary>
public sealed class NvidiaProfileManualState : Optimization
{
    private readonly string _title;
    private readonly string _why;

    public NvidiaProfileManualState(string id, string title, string why)
    { _title = title; _why = why; Id = id; Name = title; Description = why; }

    public override string Id { get; }
    public override string Name { get; }
    public override string Description { get; }
    public override string Category => "NVIDIA";
    public override Safety Safety => Safety.Safe;
    public override bool RequiresAdmin => false;

    public override Detection Detect() => new(OptState.Manual, "Manual — configure in the NVIDIA App / NVIDIA Control Panel", false, _why);
    protected override string CapturePrevious() => "{}";
    protected override bool ApplyChange(out string detail) { detail = "Manual"; return false; }
    public override Detection DetectAfterApply() => Detect();
    public override bool Revert(string previousState, out string detail) { detail = "Manual"; return false; }
}
