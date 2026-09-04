using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using SwiftwaveTweaks.Services;

namespace SwiftwaveTweaks.Core;

/// <summary>
/// Disables link-level energy/power-saving features (Energy Efficient Ethernet, Green
/// Ethernet and the vendor Power Saving Mode) on the active wired adapter, so the NIC does
/// not dip into low-power link states that can add latency variance under load. This is the
/// network equivalent of the GPU "Prefer Maximum Performance" setting.
///
/// Scope and safety:
///   • Detection reads the documented NetAdapter cmdlets un-elevated.
///   • Apply/Revert use Set-NetAdapterAdvancedProperty (the supported driver interface)
///     through UAC elevation — no undocumented registry edits.
///   • Only the energy keywords exposed by the adapter's own driver are touched. RSS,
///     interrupt moderation, checksum/LSO offloads, flow control, buffers, speed/duplex,
///     VLAN, MAC and Wake-on-LAN are NEVER modified.
///   • The exact previous value of every touched keyword is captured and restorable.
///   • The card is hidden entirely when the adapter exposes none of these controls, so no
///     dead-end "manual" control is shown.
///
/// Effects: changing advanced adapter properties may briefly reset the link. Disabling
/// energy saving can marginally increase idle power draw and is NOT a guaranteed throughput
/// or FPS improvement — it targets latency consistency only.
/// </summary>
public sealed class NetworkPowerSavingOptimization : Optimization
{
    public override string Id => "network-power-saving";
    public override string Name => "Disable network adapter energy saving (EEE / Green Ethernet)";
    public override string Description =>
        "Turns off Energy Efficient Ethernet (EEE), Green Ethernet and the adapter's Power Saving Mode on the active wired network adapter. " +
        "These can put the link into low-power states that add latency variance; keeping them disabled favours consistent response. " +
        "RSS, interrupt moderation, checksum and large-send offloads are deliberately left untouched. Uses the documented Windows NetAdapter interface; " +
        "your previous values are captured and restorable. The link may reset briefly when applied.";
    public override string Category => "Network";
    public override Safety Safety => Safety.Safe;
    public override bool RequiresAdmin => true; // Set-NetAdapterAdvancedProperty requires elevation.

    public override Detection Detect()
    {
        var reading = NetworkAdapterService.Read();
        if (!reading.Present || !reading.SupportsEnergyControl)
            return new Detection(OptState.Unsupported,
                "No wired adapter with a supported energy-saving advanced control was detected.",
                false,
                "This only applies to a connected wired adapter whose driver exposes EEE / Green Ethernet / Power Saving Mode.");

        string state = Describe(reading);
        return new Detection(
            reading.AllEnergyDisabled ? OptState.Optimized : OptState.NeedsAttention,
            state, true, "");
    }

    protected override string CapturePrevious()
    {
        var reading = NetworkAdapterService.Read();
        if (!reading.Present)
            return JsonSerializer.Serialize(new { available = false, adapter = "", settings = new List<object>() });
        return JsonSerializer.Serialize(new
        {
            available = true,
            adapter = reading.Description,
            settings = reading.EnergySettings.Select(s => new { k = s.Keyword, v = s.Value }).ToList()
        });
    }

    protected override bool ApplyChange(out string detail)
    {
        detail = "";
        var reading = NetworkAdapterService.Read();
        if (!reading.Present || !reading.SupportsEnergyControl)
        {
            detail = "No supported wired adapter energy control is present.";
            return false;
        }
        // Only flip keywords that are currently enabled (value != 0); setting 0->0 is harmless
        // but skipping them keeps the elevated change set minimal.
        var targets = reading.EnergySettings.Where(s => s.Value != 0).ToList();
        if (targets.Count == 0) { detail = "All exposed energy-saving controls are already disabled."; return true; }
        return NetworkAdapterService.TryDisableEnergySaving(reading.Description, targets, out detail);
    }

    public override Detection DetectAfterApply() => Detect();

    public override bool Revert(string previousState, out string detail)
    {
        detail = "";
        try
        {
            var doc = JsonDocument.Parse(previousState);
            bool available = doc.RootElement.TryGetProperty("available", out var av) && av.GetBoolean();
            if (!available) { detail = "No previous network adapter state was captured; cannot revert."; return false; }
            string adapter = doc.RootElement.GetProperty("adapter").GetString() ?? "";
            var previous = new List<NetworkEnergySetting>();
            foreach (var el in doc.RootElement.GetProperty("settings").EnumerateArray())
            {
                string kw = el.GetProperty("k").GetString() ?? "";
                int val = el.GetProperty("v").GetInt32();
                previous.Add(new NetworkEnergySetting(kw, LabelFor(kw), val));
            }
            if (previous.Count == 0) { detail = "Nothing to restore."; return true; }

            bool ok = NetworkAdapterService.TryRestoreEnergySaving(adapter, previous, out detail);
            if (!ok) return false;

            // Independent re-read verification.
            var after = NetworkAdapterService.Read();
            if (!after.Present) { detail = "Could not re-read the adapter after restore."; return false; }
            foreach (var p in previous)
            {
                var now = after.EnergySettings.FirstOrDefault(s => s.Keyword == p.Keyword);
                if (now is null || now.Value != p.Value)
                {
                    detail = $"Revert mismatch for {p.Keyword}: expected {p.Value}, got {(now?.Value.ToString() ?? "absent")}.";
                    return false;
                }
            }
            detail = $"Restored {previous.Count} energy setting(s) to their previous values.";
            return true;
        }
        catch (System.Exception ex)
        {
            SafeLog.Write("Network energy-saving revert failed.", ex);
            detail = ex.Message;
            return false;
        }
    }

    private static string LabelFor(string keyword) => keyword switch
    {
        "*EEE" => "Energy Efficient Ethernet (EEE)",
        "AdvancedEEE" => "Advanced EEE",
        "EnableGreenEthernet" => "Green Ethernet",
        "PowerSavingMode" => "Power Saving Mode",
        _ => keyword
    };

    private static string Describe(NetworkAdapterReading reading)
    {
        var parts = reading.EnergySettings
            .Select(s => $"{LabelFor(s.Keyword)}: {(s.Value == 0 ? "off" : "on")}");
        return $"{reading.Description} · {reading.LinkSpeed} — " + string.Join(" · ", parts);
    }
}
