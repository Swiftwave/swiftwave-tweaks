using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace SwiftwaveTweaks;

/// <summary>User-selectable colour themes. Stored by value in preferences.json.</summary>
public enum AppTheme
{
    /// <summary>The existing Swiftwave navy identity.</summary>
    Default = 0,
    Light = 1,
    Dark = 2,
    /// <summary>Follows the Windows "app" light/dark setting.</summary>
    System = 3
}

/// <summary>
/// One resolved set of colours for every surface, text role and semantic state. The same
/// palette feeds <see cref="Ui"/> (code-built pages) and the App.xaml resource brushes
/// (templates, window chrome), so a switch can never leave half the UI on the old theme.
/// </summary>
internal sealed record ThemePalette(
    Color WindowBg, Color SidebarBg, Color CardBg, Color CardSoftBg, Color Line,
    Color TextPrimary, Color TextSecondary, Color TextTertiary, Color TextFaint,
    Color Accent, Color Good, Color Warn, Color Bad,
    Color HoverFill, Color SwitchOff, Color SwitchOn,
    Color StatusActiveText, Color StatusActiveBg,
    Color StatusAttentionText, Color StatusAttentionBg,
    Color StatusAvailableText, Color StatusAvailableBg,
    Color HeaderSub, Color NavText, Color NavHoverFill, Color NavTextHover, Color NavTextSelected, Color NavHighlight,
    Color ButtonFill, Color ButtonBorder, Color ButtonHover, Color ButtonPress, Color ButtonDisabledText,
    Color ScrollThumb, Color ScrollThumbHover, Color ScrollThumbDrag,
    Color WelcomePrompt, Color WelcomeName,
    Color CaptionIcon, Color CaptionIconHover, Color CaptionCloseIcon,
    Color CaptionHoverFill, Color CaptionPressFill, Color CaptionCloseHoverFill);

/// <summary>
/// Applies a user-selected theme to the whole application: the shared <see cref="Ui"/> brushes,
/// the App.xaml resource brushes used by templates and the window chrome, and the caption
/// buttons. Applying is idempotent and safe to call before the window exists (startup) or at
/// runtime (Settings page), where it raises <see cref="Changed"/> so open pages can rebuild.
/// </summary>
public static class ThemeService
{
    private static AppTheme _requested = AppTheme.Default;
    private static bool _watching;

    /// <summary>The mode the user chose (System stays System; it is not flattened).</summary>
    public static AppTheme Requested => _requested;

    /// <summary>Raised on the UI thread after a palette has been applied.</summary>
    public static event Action? Changed;

    /// <summary>Applies a mode immediately. Non-UI callers are marshalled to the dispatcher.</summary>
    public static void Apply(AppTheme theme)
    {
        var app = Application.Current;
        if (app is not null && !app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.BeginInvoke(() => Apply(theme));
            return;
        }

        _requested = theme;
        var palette = theme switch
        {
            AppTheme.Light => Themes.Light,
            AppTheme.Dark => Themes.Dark,
            AppTheme.System => SystemPrefersLight() ? Themes.Light : Themes.Dark,
            _ => Themes.Default
        };

        Ui.ApplyPalette(palette);
        Publish(palette);
        CaptionButton.ApplyPalette(palette);
        Changed?.Invoke();
    }

    /// <summary>Re-applies the current mode (used when Windows reports a theme change).</summary>
    public static void Refresh()
        => Apply(_requested);

    /// <summary>Starts listening for Windows light/dark changes while "Follow system" is active.</summary>
    public static void StartWatching()
    {
        if (_watching) return;
        _watching = true;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (_requested != AppTheme.System) return;
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle))
            return;
        Apply(AppTheme.System);
    }

    /// <summary>Reads the Windows app-theme preference; falls back to dark.</summary>
    private static bool SystemPrefersLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is { } value)
                return Convert.ToInt32(value) != 0;
        }
        catch (Exception exception)
        {
            Services.SafeLog.Write("Could not read the Windows app theme", exception);
        }
        return false;
    }

    /// <summary>Writes the palette into the Application resource dictionary (DynamicResource keys).</summary>
    private static void Publish(ThemePalette p)
    {
        if (Application.Current is not { } app) return;
        var resources = app.Resources;

        Set(resources, "AppWindowBg", p.WindowBg);
        Set(resources, "SidebarBg", p.SidebarBg);
        Set(resources, "CardBg", p.CardBg);
        Set(resources, "CardSoftBg", p.CardSoftBg);
        Set(resources, "LineBrush", p.Line);
        Set(resources, "TextPrimary", p.TextPrimary);
        Set(resources, "TextSecondary", p.TextSecondary);
        Set(resources, "TextTertiary", p.TextTertiary);
        Set(resources, "TextFaint", p.TextFaint);
        Set(resources, "AccentBrush", p.Accent);
        Set(resources, "HeaderTitle", p.TextPrimary);
        Set(resources, "HeaderSub", p.HeaderSub);
        Set(resources, "NavText", p.NavText);
        Set(resources, "NavTextHover", p.NavTextHover);
        Set(resources, "NavTextSelected", p.NavTextSelected);
        Set(resources, "NavHoverFill", p.NavHoverFill);
        Set(resources, "NavHighlightBrush", p.NavHighlight);
        Set(resources, "ButtonFill", p.ButtonFill);
        Set(resources, "ButtonBorder", p.ButtonBorder);
        Set(resources, "ButtonHover", p.ButtonHover);
        Set(resources, "ButtonPress", p.ButtonPress);
        Set(resources, "ButtonDisabledText", p.ButtonDisabledText);
        Set(resources, "ScrollThumb", p.ScrollThumb);
        Set(resources, "ScrollThumbHover", p.ScrollThumbHover);
        Set(resources, "ScrollThumbDrag", p.ScrollThumbDrag);
        Set(resources, "TrayMenuBg", p.CardBg);
        Set(resources, "TrayMenuBorder", p.Line);
        Set(resources, "TrayMenuText", p.TextPrimary);
        Set(resources, "TrayHover", p.HoverFill);
        Set(resources, "TrayPress", p.ButtonPress);
        Set(resources, "WelcomePrompt", p.WelcomePrompt);
        Set(resources, "WelcomeName", p.WelcomeName);
    }

    private static void Set(ResourceDictionary resources, string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        resources[key] = brush;
    }

    private static class Themes
    {
        private static Color Rgb(uint hex)
            => Color.FromRgb((byte)(hex >> 16), (byte)(hex >> 8), (byte)hex);

        private static Color Argb(uint argb)
            => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

        // ── existing Swiftwave theme (unchanged values) ─────────────────────────────────────
        internal static readonly ThemePalette Default = new(
            WindowBg: Rgb(0x0B101A), SidebarBg: Rgb(0x101926), CardBg: Rgb(0x141F2E), CardSoftBg: Rgb(0x182536), Line: Rgb(0x243345),
            TextPrimary: Rgb(0xF5F8FC), TextSecondary: Rgb(0x8CA0B5), TextTertiary: Rgb(0x657A90), TextFaint: Rgb(0x4C5F75),
            Accent: Rgb(0x4CC2FF), Good: Rgb(0x7CC98B), Warn: Rgb(0xE0B16A), Bad: Rgb(0xE08A8A),
            HoverFill: Rgb(0x2E4259), SwitchOff: Rgb(0x2A3A4F), SwitchOn: Rgb(0x59C289),
            StatusActiveText: Rgb(0x59C289), StatusActiveBg: Rgb(0x1E373B),
            StatusAttentionText: Rgb(0xE5B562), StatusAttentionBg: Rgb(0x353535),
            StatusAvailableText: Rgb(0x4CC2FF), StatusAvailableBg: Rgb(0x1C374E),
            HeaderSub: Rgb(0x7E93A9), NavText: Rgb(0xB8C5D4), NavHoverFill: Argb(0x22FFFFFF),
            NavTextHover: Rgb(0xE4EDF6), NavTextSelected: Rgb(0xF5F8FC), NavHighlight: Rgb(0x87A0B8),
            ButtonFill: Rgb(0x1C2A3B), ButtonBorder: Rgb(0x31455D), ButtonHover: Rgb(0x2E4259),
            ButtonPress: Rgb(0x0D1622), ButtonDisabledText: Rgb(0x7E90A3),
            ScrollThumb: Rgb(0x31455D), ScrollThumbHover: Rgb(0x4A637F), ScrollThumbDrag: Rgb(0x5B7A9C),
            WelcomePrompt: Rgb(0xAFC0D1), WelcomeName: Rgb(0xF5FAFF),
            CaptionIcon: Rgb(0xC9D5E1), CaptionIconHover: Rgb(0xF3F8FD), CaptionCloseIcon: Rgb(0xE88A8A),
            CaptionHoverFill: Argb(0x5A2E4259), CaptionPressFill: Argb(0x6E0D1622), CaptionCloseHoverFill: Argb(0x38E06C6C));

        // ── light: white surfaces, near-black text, darker semantic accents for contrast ───
        internal static readonly ThemePalette Light = new(
            WindowBg: Rgb(0xF4F6F9), SidebarBg: Rgb(0xEDEFF3), CardBg: Rgb(0xFFFFFF), CardSoftBg: Rgb(0xF2F5F9), Line: Rgb(0xD7DEE8),
            TextPrimary: Rgb(0x11161D), TextSecondary: Rgb(0x3F4A57), TextTertiary: Rgb(0x5C6774), TextFaint: Rgb(0x7E8896),
            Accent: Rgb(0x0B6FBF), Good: Rgb(0x1E7A3C), Warn: Rgb(0x8A5A00), Bad: Rgb(0xB3261E),
            HoverFill: Rgb(0xE4EAF2), SwitchOff: Rgb(0xC6CED9), SwitchOn: Rgb(0x2E9E5B),
            StatusActiveText: Rgb(0x1E7A3C), StatusActiveBg: Rgb(0xDFF3E5),
            StatusAttentionText: Rgb(0x7A5200), StatusAttentionBg: Rgb(0xFBEFD6),
            StatusAvailableText: Rgb(0x0B5FA5), StatusAvailableBg: Rgb(0xDCEBF9),
            HeaderSub: Rgb(0x5F6B78), NavText: Rgb(0x414C58), NavHoverFill: Argb(0x14000000),
            NavTextHover: Rgb(0x11161D), NavTextSelected: Rgb(0x000000), NavHighlight: Rgb(0x1B2B3D),
            ButtonFill: Rgb(0xF7F9FC), ButtonBorder: Rgb(0xC9D3DF), ButtonHover: Rgb(0xE8EEF6),
            ButtonPress: Rgb(0xDCE5F0), ButtonDisabledText: Rgb(0x96A0AC),
            ScrollThumb: Rgb(0xC0C9D4), ScrollThumbHover: Rgb(0xA7B4C3), ScrollThumbDrag: Rgb(0x8F9EAF),
            WelcomePrompt: Rgb(0x5F6B78), WelcomeName: Rgb(0x11161D),
            CaptionIcon: Rgb(0x4A5663), CaptionIconHover: Rgb(0x11161D), CaptionCloseIcon: Rgb(0xC0392B),
            CaptionHoverFill: Argb(0x18000000), CaptionPressFill: Argb(0x26000000), CaptionCloseHoverFill: Argb(0x20C0392B));

        // ── neutral dark: same contrast model as Default, without the navy tint ────────────
        internal static readonly ThemePalette Dark = new(
            WindowBg: Rgb(0x0C0D0F), SidebarBg: Rgb(0x131417), CardBg: Rgb(0x191B1F), CardSoftBg: Rgb(0x20232A), Line: Rgb(0x2C3038),
            TextPrimary: Rgb(0xF2F3F5), TextSecondary: Rgb(0xA4ACB8), TextTertiary: Rgb(0x7C8592), TextFaint: Rgb(0x5B6470),
            Accent: Rgb(0x4CC2FF), Good: Rgb(0x6FCB8B), Warn: Rgb(0xDDB06B), Bad: Rgb(0xE08A8A),
            HoverFill: Rgb(0x2A2E36), SwitchOff: Rgb(0x333842), SwitchOn: Rgb(0x4FB37A),
            StatusActiveText: Rgb(0x6FCB8B), StatusActiveBg: Rgb(0x1B2A20),
            StatusAttentionText: Rgb(0xDDB06B), StatusAttentionBg: Rgb(0x2E2A20),
            StatusAvailableText: Rgb(0x4CC2FF), StatusAvailableBg: Rgb(0x1B2733),
            HeaderSub: Rgb(0x8A929E), NavText: Rgb(0xA9B2BE), NavHoverFill: Argb(0x14FFFFFF),
            NavTextHover: Rgb(0xE9EDF2), NavTextSelected: Rgb(0xFFFFFF), NavHighlight: Rgb(0xC8D4E2),
            ButtonFill: Rgb(0x1F2228), ButtonBorder: Rgb(0x343A44), ButtonHover: Rgb(0x2A2E36),
            ButtonPress: Rgb(0x14161A), ButtonDisabledText: Rgb(0x7C8592),
            ScrollThumb: Rgb(0x3A404A), ScrollThumbHover: Rgb(0x4C5461), ScrollThumbDrag: Rgb(0x5E6876),
            WelcomePrompt: Rgb(0xC0C6CE), WelcomeName: Rgb(0xF8F9FB),
            CaptionIcon: Rgb(0xC9CED6), CaptionIconHover: Rgb(0xF5F7FA), CaptionCloseIcon: Rgb(0xE08A8A),
            CaptionHoverFill: Argb(0x5A2A2E36), CaptionPressFill: Argb(0x6E14161A), CaptionCloseHoverFill: Argb(0x38E06C6C));
    }
}
