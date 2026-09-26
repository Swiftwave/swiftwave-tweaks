using System.Linq;
using System.Text.RegularExpressions;
using SwiftwaveTweaks.Services;

namespace SwiftwaveTweaks.Core;

/// <summary>
/// Runs the supported Windows Optimize Drives operation for one volume (TRIM/retrim on SSD/NVMe,
/// regular defragmentation on HDD). No custom scheduler, no aggressive behaviour. (spec 18)
/// </summary>
public sealed class StorageOptimization : Optimization
{
    private readonly DriveInfo _drive;

    public StorageOptimization(DriveInfo drive) => _drive = drive;

    public override string Id => $"storage-{_drive.Letter.TrimEnd(':').ToLowerInvariant()}";
    public override string Name => $"Optimize drive {_drive.Letter} ({_drive.BusKind})";
    public override string Description => _drive.BusKind.Contains("SSD") || _drive.BusKind.Contains("NVMe")
        ? $"Runs Windows' supported Optimize Drives (TRIM/retrim) on {_drive.Letter} {_drive.Model}. No manual defragmentation is performed on SSD/NVMe media."
        : $"Runs Windows' supported Optimize Drives defragmentation on {_drive.Letter} {_drive.Model}. Windows decides the safe method for this media type.";
    public override string Category => "Storage";
    public override Safety Safety => Safety.Safe;
    public override bool RequiresAdmin => true;

    private static bool IsSolidState(DriveInfo d) => d.BusKind.Contains("SSD") || d.BusKind.Contains("NVMe");

    private bool _appliedSuccessfully;

    public override Detection Detect()
    {
        string letter = _drive.Letter.TrimEnd(':');
        if (_drive.SizeGb <= 0) return new Detection(OptState.Unsupported, "Drive information unavailable", false, "The volume could not be queried.");
        // There is no persistent "optimized" flag for TRIM; the operation is available on demand.
        _appliedSuccessfully = false; // reset on fresh detection
        return new Detection(
            OptState.Unknown,
            $"{_drive.Model} · {_drive.FreeGb:N0} GB free of {_drive.SizeGb:N0} GB · " + (IsSolidState(_drive) ? "TRIM/retrim available" : "defragmentation available"),
            true, "");
    }

    protected override string CapturePrevious() => "n/a"; // Storage optimization has no settings to roll back.

    protected override bool ApplyChange(out string detail)
    {
        string letter = _drive.Letter.TrimEnd(':');
        var r = ProcessRunner.RunElevatedPowerShellWithOutput($"Optimize-Volume -DriveLetter '{letter}' -Verbose; \"OPT:$?\"");
        string output = r.Output.Trim();
        detail = output.Length > 400 ? output[^400..] : output;
        bool ran = r.ExitCode == 0 && output.Contains("OPT:True");
        bool verified = (ran && output.Contains("successfully", StringComparison.OrdinalIgnoreCase))
                        || (ran && output.Contains("did not need", StringComparison.OrdinalIgnoreCase));
        if (ran && !verified)
        {
            // Windows sometimes reports completion without the word "successfully"; accept explicit success phrases only.
            verified = output.Contains("completed", StringComparison.OrdinalIgnoreCase) && !output.Contains("error", StringComparison.OrdinalIgnoreCase);
        }
        _appliedSuccessfully = ran && verified;
        return _appliedSuccessfully;
    }

    public override Detection DetectAfterApply()
    {
        // TRIM has no persistent "optimized" flag, but a successful run IS verifiable from the
        // Optimize-Volume output. Report Optimized only when the apply was confirmed successful;
        // otherwise re-read the live state.
        if (_appliedSuccessfully)
            return new Detection(OptState.Optimized, $"{_drive.Model} — recently optimized", true, "");
        return Detect();
    }

    public override bool Revert(string previousState, out string detail)
    {
        // Nothing to revert: the operation maintains media, it does not change user settings.
        detail = "Storage optimization does not alter settings; nothing to restore.";
        return true;
    }
}

/// <summary>Central registry of all optimizations shown in the UI. (spec 30/33)</summary>
public static class OptimizationCatalog
{
    public static List<Optimization> Build(Core.SystemSnapshot? snapshot)
    {
        var list = new List<Optimization>
        {
            new PowerPlanOptimization(),
            new ProcessorPolicyOptimization(),
            new GameDvrOptimization(),
            new GameBarOptimization(),
            new GameModeOptimization(),
            new HagsOptimization(),
            VisualEffectsOptimization.Create("default"),
            VisualEffectsOptimization.Create("balanced"),
            VisualEffectsOptimization.Create("performance"),
        };

        if (snapshot is not null)
            list.AddRange(snapshot.Drives.Select(d => new StorageOptimization(d)));

        // NVIDIA Power Management Mode is the only NVIDIA profile setting automated
        // here — it has a verified NVAPI DRS interface (setting ID 0x1057EB71,
        // confirmed against official NVIDIA documentation). All other NVIDIA profile
        // settings (Low Latency Mode, V-Sync, Max Frame Rate, Shader Cache, Texture
        // Filtering, NVIDIA Reflex) are intentionally NOT exposed in the UI: NVIDIA
        // confirms they have no supported NVAPI DRS interface, so presenting them as
        // manual cards would add dead-end UI without real functionality. The
        // NvidiaProfileManualState class is preserved as reusable infrastructure for
        // any future setting that gains verified NVAPI support.
        list.Add(new NvidiaPowerManagementOptimization());

        // Network: only the wired-adapter energy/power-saving link features (EEE, Green
        // Ethernet, vendor Power Saving Mode) are automated, via the documented NetAdapter
        // cmdlets. RSS, interrupt moderation, checksum/LSO offloads and flow control are
        // intentionally never modified. The optimization self-reports Unsupported on
        // machines without a capable wired adapter, and the Network page hides that card.
        list.Add(new NetworkPowerSavingOptimization());

        // Fortnite specific optimizations: per-user, fully hidden Startup watcher that
        // excludes logical CPU 0 from the Fortnite process affinity. Category="Gaming"
        // so it renders on the Gaming page under the General section.
        list.Add(new FortniteAffinityOptimization());

        // Windows Debloat: only the ContentDeliveryManager suggestion surfaces are
        // automated — they have a safe, documented, HKCU, per-feature, reversible mechanism.
        // Background-activity reduction, preinstalled-app background control, and the broad
        // ContentDeliveryAllowed master toggle are intentionally NOT exposed (no safe
        // single-key mechanism exists on this Windows build). No services, components,
        // or apps are disabled/removed. Category="Gaming" so it renders on the Gaming page.
        list.Add(new WindowsSuggestionsOptimization());

        return list;
    }
}

/// <summary>Named optimization sets. Applying a preset still runs each optimization's own detect/apply/verify cycle. (spec 30)</summary>
public static class Presets
{
    public sealed record PresetDef(string Id, string Name, string Description, string[] OptimizationIds);

    public static readonly PresetDef Recommended = new("recommended", "Recommended safe optimizations",
        "Only broadly safe changes: best performance power plan, Game DVR background recording off, Game Bar overlay off, Game Mode on, Windows default visuals and one-shot storage optimization.",
        ["power-plan", "game-dvr", "game-bar", "game-mode", "visual-default", "storage"]);

    public static readonly PresetDef Gaming = new("gaming", "Gaming performance",
        "Focuses on power policy, Game DVR/Game Bar activity and startup noise. Adds the advanced processor frequency ceiling.",
        ["power-plan", "game-dvr", "game-bar", "game-mode", "processor-policy", "visual-balanced", "storage"]);

    public static readonly PresetDef Balanced = new("balanced", "Balanced",
        "Low-risk optimizations while preserving normal Windows behaviour.",
        ["power-plan", "game-mode", "visual-default"]);

    public static IReadOnlyList<PresetDef> All => [Recommended, Gaming, Balanced];

    public static List<Optimization> Resolve(PresetDef preset, List<Optimization> catalog)
    {
        var result = new List<Optimization>();
        foreach (var id in preset.OptimizationIds)
        {
            if (id == "storage")
            {
                result.AddRange(catalog.Where(o => o.Id.StartsWith("storage-")));
                continue;
            }
            var match = catalog.FirstOrDefault(o => o.Id == id);
            if (match is not null) result.Add(match);
        }
        return result;
    }
}
