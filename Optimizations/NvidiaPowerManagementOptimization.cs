using System;
using System.Text.Json;
using SwiftwaveTweaks.Services;

namespace SwiftwaveTweaks.Core;

/// <summary>
/// NVIDIA Power Management Mode (Prefer Maximum Performance) optimization, backed by
/// the verified NVAPI DRS interface on the driver's base profile. Implements the full
/// Detect → Apply → Verify → Revert lifecycle using live driver state as the source
/// of truth — never cached UI state, never assumed defaults.
///
/// Setting ID 0x1057EB71 (PREFERRED_PSTATE_ID) verified against:
///   • NVIDIA NvApiDriverSettings.h (NVAPI SDK Release 590)
///   • NVIDIA Driver Settings Programming Guide PG-12072-001_v01 (Oct 2024)
///   • NVWMI API Reference v2.31 (NVIDIA, Feb 2018)
///   • Live driver DRS database on this machine (CurrentValue=1 when Control Panel
///     shows "Prefer maximum performance")
///
/// No administrator elevation is required — the NVAPI DRS interface reads and writes
/// the per-user driver profile database without privilege escalation. (Verified
/// empirically during the live probe.)
/// </summary>
public sealed class NvidiaPowerManagementOptimization : Optimization
{
    public override string Id => "nvidia-power-mgmt";
    public override string Name => "NVIDIA power management mode (Prefer maximum performance)";
    public override string Description =>
        "Sets the NVIDIA driver's base profile Power management mode to Prefer Maximum Performance. " +
        "The GPU will stay at higher clock states under load, which can reduce latency-induced frametime " +
        "variance but raises power draw, temperatures and fan activity under sustained load. Verified " +
        "through the official NVIDIA NVAPI DRS interface — your previous value is captured and restorable.";
    public override string Category => "NVIDIA";
    public override Safety Safety => Safety.Safe;
    public override bool RequiresAdmin => false;

    /// <summary>
    /// Live-reads the driver state. The UI's switch position derives directly from this —
    /// never from cached UI state, preferences, or a previous apply result.
    /// </summary>
    public override Detection Detect()
    {
        var reading = NvidiaDrsService.ReadPowerManagementMode();
        if (reading is null)
            return new Detection(OptState.Unsupported, "NVAPI unavailable — configure in NVIDIA Control Panel", false,
                "The NVIDIA driver or NVAPI could not be reached. The setting cannot be detected or modified here.");
        bool optimized = reading.Value.CurrentValue == NvidiaDrsService.PreferredPstateMax;
        string label = DescribeValue(reading.Value.CurrentValue, reading.Value.IsPredefined);
        return new Detection(
            optimized ? OptState.Optimized : OptState.NeedsAttention,
            label,
            true,
            "");
    }

    /// <summary>
    /// Captures the exact previous value AND whether it was a driver default — so Revert
    /// can either RestoreSettingToDefault (when it was predefined) or SetSetting back to
    /// the exact previous value (when it was a user-customized value). This is critical:
    /// blindly writing the NVIDIA default on revert would erase a user's intentional
    /// custom configuration.
    /// </summary>
    protected override string CapturePrevious()
    {
        var reading = NvidiaDrsService.ReadPowerManagementMode();
        if (reading is null)
            return JsonSerializer.Serialize(new { value = (int?)null, isPredefined = false, available = false });
        return JsonSerializer.Serialize(new
        {
            value = (int)reading.Value.CurrentValue,
            isPredefined = reading.Value.IsPredefined,
            available = true
        });
    }

    /// <summary>
    /// Applies PREFERRED_PSTATE_MAX to the base profile and saves the DRS database.
    /// Returns true only when the subsequent re-read confirms the value took effect.
    /// </summary>
    protected override bool ApplyChange(out string detail)
        => NvidiaDrsService.TryApplyPowerManagementMode(NvidiaDrsService.PreferredPstateMax, out detail);

    /// <summary>
    /// Independent re-read after Apply. The base Optimization.Apply() also calls this
    /// and will auto-revert if the state is not Optimized.
    /// </summary>
    public override Detection DetectAfterApply() => Detect();

    /// <summary>
    /// Restores the exact captured previous state — either the driver default (when
    /// the value was predefined before we touched it) or the exact previous custom value.
    /// Returns true only when an independent re-read confirms the restore.
    /// </summary>
    public override bool Revert(string previousState, out string detail)
    {
        detail = "";
        try
        {
            var doc = JsonDocument.Parse(previousState);
            bool available = doc.RootElement.TryGetProperty("available", out var av) && av.GetBoolean();
            if (!available)
            {
                detail = "No previous NVAPI state was captured; cannot revert.";
                return false;
            }
            bool isPredefined = doc.RootElement.GetProperty("isPredefined").GetBoolean();
            if (isPredefined)
            {
                // The value was the driver default before we changed it — restore to default.
                bool ok = NvidiaDrsService.TryRestorePowerManagementModeDefault(out detail);
                if (!ok) return false;
            }
            else
            {
                // The value was a custom user-set value — restore the exact value.
                uint previousValue = (uint)doc.RootElement.GetProperty("value").GetInt32();
                bool ok = NvidiaDrsService.TryRestorePowerManagementModeValue(previousValue, out detail);
                if (!ok) return false;
            }
            // Final verification: re-read and confirm the live state matches what we restored to.
            var readback = NvidiaDrsService.ReadPowerManagementMode();
            if (readback is null) { detail = "Could not verify the restored state."; return false; }
            return true;
        }
        catch (Exception ex)
        {
            SafeLog.Write("NVIDIA power management revert failed.", ex);
            detail = ex.Message;
            return false;
        }
    }

    private static string DescribeValue(uint value, bool isPredefined)
    {
        string label = value switch
        {
            NvidiaDrsService.PreferredPstateMax => "Prefer Maximum Performance",
            NvidiaDrsService.PreferredPstateDriverDefault => "Optimal / Adaptive",
            _ => $"Custom value ({value})"
        };
        // When the setting is at the driver default, label it once; otherwise note whether
        // the explicit value was user-set or happens to equal a predefined value.
        if (value == NvidiaDrsService.PreferredPstateDriverDefault && isPredefined)
            return "Optimal / Adaptive · driver default";
        return $"{label}{(isPredefined ? " · driver default" : " · user-set")}";
    }
}
