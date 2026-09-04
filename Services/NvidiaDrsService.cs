using System;
using NvAPIWrapper.DRS;
using NvAPIWrapper.Native.DRS;
using SwiftwaveTweaks.Core;

namespace SwiftwaveTweaks.Services;

/// <summary>
/// Thin wrapper around the verified NvAPIWrapper.Net public DRS API.
/// All setting IDs and value mappings used here are verified against the official
/// NVIDIA NvApiDriverSettings.h, the NVAPI Driver Settings Programming Guide
/// PG-12072-001, NVWMI v2.31 and the live driver DRS database on this machine.
/// (spec: NVIDIA NVAPI state-detection plan)
/// </summary>
public static class NvidiaDrsService
{
    /// <summary>
    /// PREFERRED_PSTATE_ID — verified against NvApiDriverSettings.h (Release 590),
    /// NVAPI Driver Settings PG-12072-001_v01 (Oct 2024), NVWMI v2.31, and the live
    /// driver DRS database on this machine. NVIDIA Control Panel label:
    /// "Power management mode".
    /// </summary>
    public const uint PreferredPstateId = 0x1057EB71;

    /// <summary>
    /// PREFERRED_PSTATE_MAX = 1 — value NVIDIA Control Panel labels as
    /// "Prefer maximum performance". Confirmed empirically: reading the base profile
    /// on this machine while Control Panel shows "Prefer maximum performance" returns
    /// CurrentValue=1. The NVIDIA developer forum code sample also uses
    /// PREFERRED_PSTATE_MAX for this setting.
    /// </summary>
    public const uint PreferredPstateMax = 1;

    private static bool _initialized;
    private static readonly object _initLock = new();

    /// <summary>
    /// Initializes NVAPI exactly once per process. Safe to call repeatedly.
    /// Returns false if NVAPI cannot talk to the installed NVIDIA driver — in that
    /// case the caller must present the optimization as Unsupported, not Optimized.
    /// </summary>
    public static bool EnsureInitialized()
    {
        if (_initialized) return true;
        lock (_initLock)
        {
            if (_initialized) return true;
            try
            {
                NvAPIWrapper.NVIDIA.Initialize();
                _initialized = true;
                SafeLog.Write("NVAPI initialized successfully.");
                return true;
            }
            catch (Exception ex)
            {
                SafeLog.Write("NVAPI initialization failed — NVIDIA optimization will be reported as unsupported.", ex);
                return false;
            }
        }
    }

    /// <summary>
    /// The documented NVIDIA driver default for Power Management Mode (PREFERRED_PSTATE)
    /// on the base profile: Optimal Power / Adaptive (value 0). This is NOT Prefer Maximum
    /// Performance. Verified against NvApiDriverSettings.h and the live driver behaviour
    /// on this machine: when the setting is not explicitly set on the profile, the driver
    /// applies this predefined default.
    /// </summary>
    public const uint PreferredPstateDriverDefault = 0;

    /// <summary>
    /// Reads the current Power Management Mode value and whether that value is the
    /// driver-predefined (default) value for the base profile. Returns null ONLY when
    /// NVAPI itself is unavailable — callers must treat that as Unsupported.
    ///
    /// Important behaviour: <c>BaseProfile.GetSetting</c> returns null when the setting is
    /// NOT explicitly set on the profile (i.e. it is using the driver-predefined default).
    /// That is a valid, detectable state — the setting is at Optimal/Adaptive, which is NOT
    /// Prefer Maximum Performance. We return a <see cref="PstateReading"/> with the
    /// documented driver default value (0) and IsPredefined=true in that case, so Detect()
    /// correctly reports NeedsAttention rather than Unsupported. Returning null here is
    /// reserved for genuine NVAPI failure.
    /// </summary>
    public static PstateReading? ReadPowerManagementMode()
    {
        if (!EnsureInitialized()) return null;
        try
        {
            using var session = DriverSettingsSession.CreateAndLoad();
            var profile = session.BaseProfile;
            var setting = profile.GetSetting(PreferredPstateId);
            if (setting is null)
            {
                // The setting is not explicitly set on the base profile — it is using the
                // driver-predefined default (Optimal/Adaptive). NVAPI is working; the setting
                // ID is verified to exist on this driver; GetSetting simply returns null for
                // non-explicit (defaulted) settings. Represent this honestly.
                SafeLog.Write("NVAPI: PREFERRED_PSTATE not explicitly set on base profile — reporting driver default (Optimal/Adaptive).");
                return new PstateReading(PreferredPstateDriverDefault, true);
            }

            // CurrentValue is typed as object; for DWORD settings it is a uint.
            uint current = setting.CurrentValue is uint u ? u : Convert.ToUInt32(setting.CurrentValue);
            bool isPredefined = setting.IsCurrentValuePredefined;
            return new PstateReading(current, isPredefined);
        }
        catch (Exception ex)
        {
            SafeLog.Write("NVAPI read of PREFERRED_PSTATE failed.", ex);
            return null;
        }
    }

    /// <summary>
    /// Sets Power Management Mode on the base profile and persists the change to the
    /// driver DRS database. Returns true only if an independent re-read confirms the
    /// target value took effect.
    /// </summary>
    public static bool TryApplyPowerManagementMode(uint targetValue, out string detail)
    {
        detail = "";
        if (!EnsureInitialized()) { detail = "NVAPI unavailable."; return false; }
        try
        {
            using var session = DriverSettingsSession.CreateAndLoad();
            var profile = session.BaseProfile;
            profile.SetSetting(PreferredPstateId, targetValue);
            session.Save();

            // Independent re-read on a fresh session — never trust that SetSetting + Save
            // "worked" without verifying the persisted state.
            var readback = ReadPowerManagementMode();
            if (readback is null) { detail = "Could not re-read the setting after applying."; return false; }
            if (readback.Value.CurrentValue != targetValue)
            {
                detail = $"Readback mismatch: expected {targetValue}, got {readback.Value.CurrentValue}.";
                return false;
            }
            detail = $"Verified: PREFERRED_PSTATE = {readback.Value.CurrentValue} on base profile.";
            return true;
        }
        catch (Exception ex)
        {
            SafeLog.Write("NVAPI apply of PREFERRED_PSTATE failed.", ex);
            detail = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Restores Power Management Mode to its driver-predefined (default) value, used
    /// when the previously captured state indicates the value was a driver default
    /// before Swiftwave Tweaks touched it. Returns true only when an independent
    /// re-read confirms the restore happened.
    /// </summary>
    public static bool TryRestorePowerManagementModeDefault(out string detail)
    {
        detail = "";
        if (!EnsureInitialized()) { detail = "NVAPI unavailable."; return false; }
        try
        {
            using var session = DriverSettingsSession.CreateAndLoad();
            var profile = session.BaseProfile;
            profile.RestoreSettingToDefault(PreferredPstateId);
            session.Save();
            // After RestoreSettingToDefault the setting is no longer explicitly set on the
            // profile, so GetSetting returns null and ReadPowerManagementMode now reports the
            // driver-default reading (value 0, IsPredefined=true). We verify the restore by
            // confirming IsPredefined is true — that is the honest proof the setting returned
            // to the driver default rather than holding a stale explicit value.
            var readback = ReadPowerManagementMode();
            if (readback is null) { detail = "Could not re-read after restore."; return false; }
            if (!readback.Value.IsPredefined)
            {
                detail = $"Restore may not have taken effect — setting still explicitly set (value={readback.Value.CurrentValue}, predefined=false).";
                return false;
            }
            detail = $"Restored to driver default; current value = {readback.Value.CurrentValue} (predefined=true).";
            return true;
        }
        catch (Exception ex)
        {
            SafeLog.Write("NVAPI restore-to-default of PREFERRED_PSTATE failed.", ex);
            detail = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Restores an exact previous Power Management Mode value (when it was NOT the
    /// driver default before Swiftwave Tweaks changed it). Returns true only when an
    /// independent re-read confirms the value was restored.
    /// </summary>
    public static bool TryRestorePowerManagementModeValue(uint previousValue, out string detail)
    {
        detail = "";
        if (!EnsureInitialized()) { detail = "NVAPI unavailable."; return false; }
        try
        {
            using var session = DriverSettingsSession.CreateAndLoad();
            var profile = session.BaseProfile;
            profile.SetSetting(PreferredPstateId, previousValue);
            session.Save();
            var readback = ReadPowerManagementMode();
            if (readback is null) { detail = "Could not re-read after restore."; return false; }
            if (readback.Value.CurrentValue != previousValue)
            {
                detail = $"Readback mismatch after restore: expected {previousValue}, got {readback.Value.CurrentValue}.";
                return false;
            }
            detail = $"Restored to previous value {previousValue}.";
            return true;
        }
        catch (Exception ex)
        {
            SafeLog.Write("NVAPI restore-to-previous-value of PREFERRED_PSTATE failed.", ex);
            detail = ex.Message;
            return false;
        }
    }
}

/// <summary>
/// Immutable snapshot of a PREFERRED_PSTATE read: the current DWORD value and whether
/// that value is the driver-predefined (default) value for this profile. The
/// <c>IsPredefined</c> flag is essential for honest revert: it lets us restore to the
/// driver default when that was the prior state, rather than blindly writing a value.
/// </summary>
public readonly record struct PstateReading(uint CurrentValue, bool IsPredefined);
