using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace SwiftwaveTweaks;

/// <summary>
/// Custom window caption button (minimize / maximize / restore / close) drawn with vector
/// geometry. Uses WindowChrome hit-test exemption so the caption drag region does not swallow
/// clicks, and animates hover/press with the same timing and easing as the rest of the app.
/// (spec 3: minimal rounded hit areas, near-white iconography, subtle translucent hover,
/// stronger close hover, tooltips and accessibility names.)
/// </summary>
public sealed class CaptionButton : Grid
{
    // Mutable shared brushes: ThemeService rewrites these on every theme switch.
    private static readonly SolidColorBrush IconRest = new(Color.FromRgb(0xC9, 0xD5, 0xE1));
    private static readonly SolidColorBrush IconHover = new(Color.FromRgb(0xF3, 0xF8, 0xFD));
    private static readonly SolidColorBrush HoverFillBrush = new(Color.FromArgb(0x5A, 0x2E, 0x42, 0x59));
    private static readonly SolidColorBrush PressFillBrush = new(Color.FromArgb(0x6E, 0x0D, 0x16, 0x22));
    private static readonly SolidColorBrush CloseHoverFill = new(Color.FromArgb(0x38, 0xE0, 0x6C, 0x6C));
    private static readonly SolidColorBrush CloseIcon = new(Color.FromRgb(0xE8, 0x8A, 0x8A));

    private readonly Border hoverFill;
    private readonly Border pressFill;
    private readonly Path icon;
    private readonly string restGeometry;
    private readonly string? altGeometry;
    private readonly string name;
    private readonly bool danger;
    private bool isAlt;

    public event EventHandler? Clicked;

    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer()
        => new System.Windows.Automation.Peers.FrameworkElementAutomationPeer(this);

    public CaptionButton(string name, string geometry, string? altGeometry = null, bool danger = false)
    {
        this.restGeometry = geometry;
        this.altGeometry = altGeometry;
        this.name = name;
        this.danger = danger;

        Width = 42;
        Height = 30;
        Cursor = Cursors.Hand;
        AutomationProperties.SetName(this, name);
        ToolTip = new ToolTip { Content = name, Background = Ui.Card, Foreground = Ui.TextWhite, BorderBrush = Ui.Line, HasDropShadow = false };

        hoverFill = new Border { CornerRadius = new CornerRadius(7), Background = danger ? CloseHoverFill : HoverFillBrush, Opacity = 0 };
        pressFill = new Border { CornerRadius = new CornerRadius(7), Background = PressFillBrush, Opacity = 0, Margin = new Thickness(1) };
        icon = new Path
        {
            Data = Geometry.Parse(geometry),
            Stroke = (Brush)IconRest.Clone(),
            StrokeThickness = 1.35,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        SetIsInChrome(true);
        Children.Add(hoverFill);
        Children.Add(pressFill);
        Children.Add(icon);

        MouseEnter += (_, _) => { AnimateFill(hoverFill, 1); AnimateIcon(danger ? CloseIcon : IconHover); };
        MouseLeave += (_, _) => { AnimateFill(hoverFill, 0); AnimateFill(pressFill, 0); AnimateIcon(IconRest); };
        MouseLeftButtonDown += (_, _) => AnimateFill(pressFill, 1);
        MouseLeftButtonUp += (_, _) => Clicked?.Invoke(this, EventArgs.Empty);

        // Keyboard activation (previously mouse-only): Space/Enter trigger the button.
        Focusable = true;
        KeyDown += (_, e) =>
        {
            if (e.Key is Key.Space or Key.Enter or Key.Return)
            {
                e.Handled = true;
                AnimateFill(pressFill, 1);
                Clicked?.Invoke(this, EventArgs.Empty);
            }
        };
        KeyUp += (_, e) => { if (e.Key is Key.Space or Key.Enter or Key.Return) AnimateFill(pressFill, 0); };
        GotKeyboardFocus += (_, _) => AnimateFill(hoverFill, 1);
        LostKeyboardFocus += (_, _) => { AnimateFill(hoverFill, 0); AnimateFill(pressFill, 0); AnimateIcon(IconRest); };
    }

    private void SetIsInChrome(bool value)
        => System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(this, value);

    /// <summary>Rewrites the shared caption colours so future and live buttons match the theme.</summary>
    internal static void ApplyPalette(ThemePalette p)
    {
        IconRest.Color = p.CaptionIcon;
        IconHover.Color = p.CaptionIconHover;
        HoverFillBrush.Color = p.CaptionHoverFill;
        PressFillBrush.Color = p.CaptionPressFill;
        CloseHoverFill.Color = p.CaptionCloseHoverFill;
        CloseIcon.Color = p.CaptionCloseIcon;
    }

    /// <summary>Recolours this instance's cloned icon stroke after a theme switch.</summary>
    internal void SyncColors()
    {
        if (icon.Stroke is not SolidColorBrush stroke) return;
        stroke.BeginAnimation(SolidColorBrush.ColorProperty, null);
        stroke.Color = danger ? CloseIcon.Color : IconRest.Color;
    }

    /// <summary>Animates the icon stroke colour between the shared theme brushes.</summary>
    private void AnimateIcon(Brush target)
    {
        var to = ((SolidColorBrush)target).Color;
        if (Motion.ReducedMotion)
        {
            icon.BeginAnimation(OpacityProperty, null);
            ((SolidColorBrush)icon.Stroke).Color = to;
            return;
        }
        var animation = new ColorAnimation(((SolidColorBrush)icon.Stroke).Color, to, Motion.Fast) { EasingFunction = Motion.OutEasing };
        ((SolidColorBrush)icon.Stroke).BeginAnimation(SolidColorBrush.ColorProperty, animation);
    }

    /// <summary>Swaps between the maximize and restore glyph with a short cross-fade.
    /// The accessibility name and tooltip follow the actual window state.</summary>
    public void SetAlt(bool alt)
    {
        if (alt == isAlt || altGeometry is null) return;
        isAlt = alt;
        var label = alt ? "Restore" : name;
        AutomationProperties.SetName(this, label);
        if (ToolTip is ToolTip tip) tip.Content = label;
        if (Motion.ReducedMotion) { icon.Data = Geometry.Parse(alt ? altGeometry : restGeometry); return; }

        var fade = new DoubleAnimation(icon.Opacity, 0, Motion.Fast) { EasingFunction = Motion.OutEasing };
        fade.Completed += (_, _) =>
        {
            icon.Data = Geometry.Parse(alt ? altGeometry : restGeometry);
            var back = new DoubleAnimation(0, 1, Motion.Fast) { EasingFunction = Motion.OutEasing };
            back.Completed += (_, _) => { };
            icon.BeginAnimation(OpacityProperty, back);
        };
        icon.BeginAnimation(OpacityProperty, fade);
    }

    private void AnimateFill(Border target, double opacity)
    {
        if (Motion.ReducedMotion) { target.BeginAnimation(OpacityProperty, null); target.Opacity = opacity; return; }
        var animation = new DoubleAnimation(target.Opacity, opacity, Motion.Fast) { EasingFunction = Motion.OutEasing };
        target.BeginAnimation(OpacityProperty, animation);
    }
}
