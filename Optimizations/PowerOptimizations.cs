using System.Linq;
using System.Text.Json;
using SwiftwaveTweaks.Services;

namespace SwiftwaveTweaks.Core;

/// <summary>Activates the best built-in performance power plan (Ultimate Performance, falling back to High performance).</summary>
public sealed class PowerPlanOptimization : Optimization
{
    public override string Id => "power-plan";
    public override string Name => "Performance power plan";
    public override string Description => "Activates the built-in Ultimate Performance plan, or High performance when Ultimate Performance is unavailable on this edition. Your previous plan is saved for rollback.";
    public override string Category => "Power";
    public override Safety Safety => Safety.Safe;
    public override bool RequiresAdmin => true;

    private const string UltimateGuid = "e9a42b02-d5df-448d-aa00-03f14749eb61";
    private (string Guid, string Name) _target;
    private string _previousJson = "{}";

    public override Detection Detect()
    {
        var active = PowerPlanReader.ReadActive();
        var plans = PowerPlanReader.ListPlans();
        var best = plans.FirstOrDefault(p => string.Equals(p.Guid, UltimateGuid, StringComparison.OrdinalIgnoreCase));
        if (best.Guid is null) best = plans.FirstOrDefault(p => p.Name.Contains("High performance", StringComparison.OrdinalIgnoreCase));
        if (best.Guid is null)
            return new Detection(OptState.Unsupported, $"Active plan: {active.Name}", false, "No built-in performance plan is available on this edition of Windows.");
        _target = best;
        if (string.Equals(active.Guid, best.Guid, StringComparison.OrdinalIgnoreCase))
            return new Detection(OptState.Optimized, $"Active plan: {active.Name}", true, "");
        return new Detection(OptState.NeedsAttention, $"Active plan: {active.Name} — recommended: {best.Name}", true, "");
    }

    protected override string CapturePrevious()
    {
        var active = PowerPlanReader.ReadActive();
        _previousJson = JsonSerializer.Serialize(new { guid = active.Guid, name = active.Name });
        return _previousJson;
    }

    protected override bool ApplyChange(out string detail)
    {
        // If Ultimate Performance does not exist yet, try to duplicate it from its hidden template (supported on Pro editions).
        var plans = PowerPlanReader.ListPlans();
        var ultimate = plans.FirstOrDefault(p => string.Equals(p.Guid, UltimateGuid, StringComparison.OrdinalIgnoreCase));
        if (ultimate.Guid is null)
        {
            ProcessRunner.RunElevatedPowerShell($"powercfg -duplicatescheme {UltimateGuid}");
            plans = PowerPlanReader.ListPlans();
            ultimate = plans.FirstOrDefault(p => string.Equals(p.Guid, UltimateGuid, StringComparison.OrdinalIgnoreCase));
        }
        var target = ultimate.Guid is not null ? ultimate : plans.FirstOrDefault(p => p.Name.Contains("High performance", StringComparison.OrdinalIgnoreCase));
        if (target.Guid is null) { detail = "No suitable plan found."; return false; }
        _target = target;
        var r = ProcessRunner.RunElevatedPowerShellWithOutput($"powercfg /setactive {target.Guid}; \"SETACTIVE:$?\"");
        detail = r.Output.Trim() is { Length: > 0 } o && !o.Contains("SETACTIVE:True") ? o : "";
        return r.ExitCode == 0 && r.Output.Contains("SETACTIVE:True");
    }

    public override Detection DetectAfterApply()
    {
        var active = PowerPlanReader.ReadActive();
        return string.Equals(active.Guid, _target.Guid, StringComparison.OrdinalIgnoreCase)
            ? new Detection(OptState.Optimized, $"Active plan: {active.Name}", true, "")
            : new Detection(OptState.NeedsAttention, $"Active plan: {active.Name} (expected {_target.Name})", true, "");
    }

    public override bool Revert(string previousState, out string detail)
    {
        detail = "";
        try
        {
            var doc = JsonDocument.Parse(previousState);
            var guid = doc.RootElement.GetProperty("guid").GetString() ?? "";
            if (guid.Length != 36) { detail = "No previous plan recorded."; return false; }
            if (!ProcessRunner.RunElevatedPowerShell($"powercfg /setactive {guid}")) { detail = "Elevated restore failed or was cancelled."; return false; }
            var active = PowerPlanReader.ReadActive();
            bool ok = string.Equals(active.Guid, guid, StringComparison.OrdinalIgnoreCase);
            detail = ok ? $"Restored plan: {active.Name}" : $"Active plan after restore: {active.Name}";
            return ok;
        }
        catch (Exception ex) { detail = ex.Message; return false; }
    }
}

/// <summary>Advanced: ensures processor min/max frequency policy is at supported Windows defaults for performance (max 100%). Never changes voltage or boost firmware.</summary>
public sealed class ProcessorPolicyOptimization : Optimization
{
    private const string SubProcessor = "54533251-82be-4824-96c1-47b60b740d00";
    private const string ThrottleMax = "bc5038f7-23e0-4960-96da-33abaf5935ec";
    private const string ThrottleMin = "893dee8e-2bef-41e0-89c6-b55d0929964c";

    public override string Id => "processor-policy";
    public override string Name => "Processor frequency policy (max 100%) — Advanced";
    public override string Description => "Ensures the maximum processor state is 100% on AC and battery. This is a supported Windows power policy ceiling — it does not force cores awake, disable idle states, change boost firmware, or touch voltage. Modern CPUs keep managing frequency dynamically.";
    public override string Category => "Power";
    public override Safety Safety => Safety.Advanced;
    public override bool RequiresAdmin => true;

    private (int Ac, int Dc) _previous;
    private string _previousJson = "{}";

    private static (int? Ac, int? Dc) Query(string alias)
    {
        var r = ProcessRunner.RunCapture("powercfg", $"/q SCHEME_CURRENT {SubProcessor} {alias}");
        if (r is null || r.Value.ExitCode != 0) return (null, null);
        var ac = System.Text.RegularExpressions.Regex.Match(r.Value.Output, @"Current AC Power Setting Index:\s*0x([0-9a-fA-F]+)");
        var dc = System.Text.RegularExpressions.Regex.Match(r.Value.Output, @"Current DC Power Setting Index:\s*0x([0-9a-fA-F]+)");
        return (ac.Success ? Convert.ToInt32(ac.Groups[1].Value, 16) : null,
                dc.Success ? Convert.ToInt32(dc.Groups[1].Value, 16) : null);
    }

    public override Detection Detect()
    {
        var max = Query(ThrottleMax);
        if (max.Ac is null) return new Detection(OptState.Unsupported, "Processor power settings are not readable on this system.", false, "powercfg could not report the maximum processor state.");
        string boost = "";
        var boostInfo = Query("be337238-0d82-4146-a960-4f3749d470c7");
        if (boostInfo.Ac is int b) boost = $" · boost mode: 0x{b:X}";
        bool ok = max.Ac == 100 && (max.Dc is null || max.Dc == 100);
        return new Detection(
            ok ? OptState.Optimized : OptState.NeedsAttention,
            $"Max processor state: AC {max.Ac}%{(max.Dc is int dc ? $" · battery {dc}%" : "")}{boost}",
            true, "");
    }

    protected override string CapturePrevious()
    {
        var max = Query(ThrottleMax);
        _previous = (max.Ac ?? -1, max.Dc ?? -1);
        _previousJson = JsonSerializer.Serialize(new { ac = _previous.Ac, dc = _previous.Dc });
        return _previousJson;
    }

    protected override bool ApplyChange(out string detail)
    {
        var script = $@"
powercfg /setacvalueindex SCHEME_CURRENT {SubProcessor} {ThrottleMax} 100
powercfg /setdcvalueindex SCHEME_CURRENT {SubProcessor} {ThrottleMax} 100
powercfg /setactive SCHEME_CURRENT
""WRITE:$?"";
";
        var r = ProcessRunner.RunElevatedPowerShellWithOutput(script);
        detail = r.Output.Trim();
        return r.ExitCode == 0 && r.Output.Contains("WRITE:True");
    }

    public override Detection DetectAfterApply() => Detect();

    public override bool Revert(string previousState, out string detail)
    {
        detail = "";
        try
        {
            var doc = JsonDocument.Parse(previousState);
            int ac = doc.RootElement.GetProperty("ac").GetInt32();
            int dc = doc.RootElement.GetProperty("dc").GetInt32();
            if (ac < 0) { detail = "No previous values recorded."; return false; }
            var script = $@"
powercfg /setacvalueindex SCHEME_CURRENT {SubProcessor} {ThrottleMax} {ac}
powercfg /setdcvalueindex SCHEME_CURRENT {SubProcessor} {ThrottleMax} {dc}
powercfg /setactive SCHEME_CURRENT
""WRITE:$?"";
";
            var r = ProcessRunner.RunElevatedPowerShellWithOutput(script);
            bool ok = r.ExitCode == 0 && r.Output.Contains("WRITE:True");
            detail = ok ? $"Restored max processor state (AC {ac}%{(dc >= 0 ? $" · battery {dc}%" : "")})." : "Elevated restore failed or was cancelled.";
            return ok;
        }
        catch (Exception ex) { detail = ex.Message; return false; }
    }
}
