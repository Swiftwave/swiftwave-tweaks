using System.Text.Json;
using Microsoft.Win32;
using SwiftwaveTweaks.Services;

namespace SwiftwaveTweaks.Core;

/// <summary>
/// Windows visual effect presets built only from documented, user-scope Windows settings.
/// All previous values are saved and restorable. (spec 19)
/// </summary>
public sealed class VisualEffectsOptimization : Optimization
{
    private const string VisFxKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects";
    private const string AdvKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string DesktopKey = @"Control Panel\Desktop";
    private const string WmKey = @"Control Panel\Desktop\WindowMetrics";

    private sealed record Preset(int VisualFxSetting, int MinAnimate, int TaskbarAnimations, int ListviewAlphaSelect, int IconsOnly, string MenuShowDelay);

    private readonly Preset _preset;
    private readonly string _label;
    private readonly string _id;
    private readonly string _desc;

    private VisualEffectsOptimization(string id, string label, Preset preset, string desc)
    { _id = id; _label = label; _preset = preset; _desc = desc; }

    public static VisualEffectsOptimization Create(string kind) => kind switch
    {
        "balanced" => new VisualEffectsOptimization("visual-balanced", "Balanced visual effects",
            new Preset(0, 0, 1, 1, 0, "200"),
            "Keeps most Windows visuals but removes window min/max animation and shortens the menu delay. Balances appearance and responsiveness."),
        "performance" => new VisualEffectsOptimization("visual-performance", "Performance visual effects",
            new Preset(3, 0, 0, 0, 1, "0"),
            "Disables window animations, taskbar animations, thumbnail previews and menu animation for maximum UI responsiveness. Windows will look plainer."),
        _ => new VisualEffectsOptimization("visual-default", "Windows default visual effects",
            new Preset(2, 1, 1, 1, 0, "400"),
            "Restores the Windows default choice (\"Let Windows decide what's best for my computer\") with standard animations and menu delay.")
    };

    public override string Id => _id;
    public override string Name => _label;
    public override string Description => _desc;
    public override string Category => "Visual";
    public override Safety Safety => _id == "visual-performance" ? Safety.Moderate : Safety.Safe;
    public override bool RequiresAdmin => false;

    private static int? Rd(string path, string name) => RegHelper.ReadDword(RegistryHive.CurrentUser, RegistryView.Default, path, name);
    private static string? Rs(string path, string name) => RegHelper.ReadString(RegistryHive.CurrentUser, RegistryView.Default, path, name);

    private static bool IntMatches(int? actual, int target) => actual == target;
    private static bool StrMatches(string? actual, string target) =>
        actual is not null && int.TryParse(actual, out var v) && v.ToString() == target;

    public override Detection Detect()
    {
        int? fx = Rd(VisFxKey, "VisualFXSetting");
        int? minAn = Rd(WmKey, "MinAnimate");
        if (minAn is null && int.TryParse(Rs(WmKey, "MinAnimate"), out var ma)) minAn = ma;
        int task = Rd(VisFxKey, "TaskbarAnimations") ?? 1;
        int list = Rd(AdvKey, "ListviewAlphaSelect") ?? 1;
        int icons = Rd(AdvKey, "IconsOnly") ?? 0;
        string menu = Rs(DesktopKey, "MenuShowDelay") ?? "400";

        bool match = IntMatches(fx, _preset.VisualFxSetting)
            && IntMatches(minAn, _preset.MinAnimate)
            && IntMatches(task, _preset.TaskbarAnimations)
            && IntMatches(list, _preset.ListviewAlphaSelect)
            && IntMatches(icons, _preset.IconsOnly)
            && StrMatches(menu, _preset.MenuShowDelay);

        string current = $"Current: mode={DescribeFx(fx)}, window animation={(minAn == 0 ? "off" : minAn == 1 ? "on" : "default")}, taskbar animations={(task == 0 ? "off" : "on")}, thumbnails={(icons == 1 ? "off" : "on")}, menu delay={menu} ms";
        return new Detection(match ? OptState.Optimized : OptState.NeedsAttention, current, true, "");
    }

    private static string DescribeFx(int? fx) => fx switch
    {
        0 => "custom", 1 => "best appearance", 2 => "Windows default", 3 => "best performance",
        _ => "Windows default (unset)"
    };

    protected override string CapturePrevious() => JsonSerializer.Serialize(new
    {
        fx = Rd(VisFxKey, "VisualFXSetting"),
        minAnimate = Rd(WmKey, "MinAnimate"),
        minAnimateStr = Rs(WmKey, "MinAnimate"),
        taskbar = Rd(VisFxKey, "TaskbarAnimations"),
        listview = Rd(AdvKey, "ListviewAlphaSelect"),
        iconsOnly = Rd(AdvKey, "IconsOnly"),
        menuDelay = Rs(DesktopKey, "MenuShowDelay")
    });

    protected override bool ApplyChange(out string detail)
    {
        try
        {
            RegHelper.WriteDword(RegistryHive.CurrentUser, VisFxKey, "VisualFXSetting", _preset.VisualFxSetting);
            RegHelper.WriteString(RegistryHive.CurrentUser, WmKey, "MinAnimate", _preset.MinAnimate.ToString());
            RegHelper.WriteDword(RegistryHive.CurrentUser, VisFxKey, "TaskbarAnimations", _preset.TaskbarAnimations);
            RegHelper.WriteDword(RegistryHive.CurrentUser, AdvKey, "ListviewAlphaSelect", _preset.ListviewAlphaSelect);
            RegHelper.WriteDword(RegistryHive.CurrentUser, AdvKey, "IconsOnly", _preset.IconsOnly);
            RegHelper.WriteString(RegistryHive.CurrentUser, DesktopKey, "MenuShowDelay", _preset.MenuShowDelay);
            // Notify the shell so settings take effect immediately.
            NativeMethods.BroadcastSettingChange();
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
            var e = JsonSerializer.Deserialize<JsonElement>(previousState);
            static int? GetInt(JsonElement el, string n) => el.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : (int?)null;
            static string? GetStr(JsonElement el, string n) => el.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (GetInt(e, "fx") is int fx) RegHelper.WriteDword(RegistryHive.CurrentUser, VisFxKey, "VisualFXSetting", fx);
            int? ma = GetInt(e, "minAnimate");
            string? mas = GetStr(e, "minAnimate");
            if (ma is int) RegHelper.WriteString(RegistryHive.CurrentUser, WmKey, "MinAnimate", ma!.Value.ToString());
            else if (mas is not null) RegHelper.WriteString(RegistryHive.CurrentUser, WmKey, "MinAnimate", mas);
            if (GetInt(e, "taskbar") is int t) RegHelper.WriteDword(RegistryHive.CurrentUser, VisFxKey, "TaskbarAnimations", t);
            if (GetInt(e, "listview") is int l) RegHelper.WriteDword(RegistryHive.CurrentUser, AdvKey, "ListviewAlphaSelect", l);
            if (GetInt(e, "iconsOnly") is int i) RegHelper.WriteDword(RegistryHive.CurrentUser, AdvKey, "IconsOnly", i);
            if (GetStr(e, "menuDelay") is string md) RegHelper.WriteString(RegistryHive.CurrentUser, DesktopKey, "MenuShowDelay", md);
            NativeMethods.BroadcastSettingChange();
            return true;
        }
        catch (Exception ex) { detail = ex.Message; return false; }
    }

    internal static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
        public static void BroadcastSettingChange()
            => SendMessageTimeout((IntPtr)0xFFFF, 0x001A, IntPtr.Zero, IntPtr.Zero, 0x0002, 500, out _); // HWND_BROADCAST, WM_SETTINGCHANGE, SMTO_ABORTIFHUNG
    }
}
