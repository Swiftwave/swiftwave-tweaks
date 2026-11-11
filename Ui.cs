using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SwiftwaveTweaks;

/// <summary>
/// Central theme palette and small control factories used across the reconstructed UI.
/// The brushes are intentionally unfrozen singletons: <see cref="ThemeService"/> rewrites their
/// colours in place, and every control built from them follows immediately — including pages
/// that were already rendered when the user switched theme.
/// </summary>
public static class Ui
{
    public static readonly SolidColorBrush Bg = Make(Color.FromRgb(0x0B, 0x10, 0x1A));       // window background
    public static readonly SolidColorBrush Panel = Make(Color.FromRgb(0x10, 0x19, 0x26));     // sidebar
    public static readonly SolidColorBrush Card = Make(Color.FromRgb(0x14, 0x1F, 0x2E));      // card fill
    public static readonly SolidColorBrush CardSoft = Make(Color.FromRgb(0x18, 0x25, 0x36));  // nested fill
    public static readonly SolidColorBrush Line = Make(Color.FromRgb(0x24, 0x33, 0x45));      // hairlines
    public static readonly SolidColorBrush TextWhite = Make(Color.FromRgb(0xF5, 0xF8, 0xFC)); // primary text
    public static readonly SolidColorBrush TextDim = Make(Color.FromRgb(0x8C, 0xA0, 0xB5));   // secondary text
    public static readonly SolidColorBrush TextMuted = Make(Color.FromRgb(0x65, 0x7A, 0x90)); // tertiary text
    public static readonly SolidColorBrush TextFaint = Make(Color.FromRgb(0x4C, 0x5F, 0x75)); // metadata text
    public static readonly SolidColorBrush Accent = Make(Color.FromRgb(0x4C, 0xC2, 0xFF));    // busy dots / focus
    public static readonly SolidColorBrush Good = Make(Color.FromRgb(0x7C, 0xC9, 0x8B));      // optimized
    public static readonly SolidColorBrush Warn = Make(Color.FromRgb(0xE0, 0xB1, 0x6A));      // needs attention
    public static readonly SolidColorBrush Bad = Make(Color.FromRgb(0xE0, 0x8A, 0x8A));       // failed / danger
    public static readonly SolidColorBrush HoverFill = Make(Color.FromRgb(0x2E, 0x42, 0x59));
    public static readonly SolidColorBrush SwitchOff = Make(Color.FromRgb(0x2A, 0x3A, 0x4F)); // toggle track, off
    public static readonly SolidColorBrush SwitchOn = Make(Color.FromRgb(0x59, 0xC2, 0x89));  // toggle track, on

    // v1-style general status boxes (filled tinted pills, top-right of optimization cards).
    public static readonly SolidColorBrush StatusActiveText = Make(Color.FromRgb(0x59, 0xC2, 0x89));
    public static readonly SolidColorBrush StatusActiveBg = Make(Color.FromRgb(0x1E, 0x37, 0x3B));
    public static readonly SolidColorBrush StatusAttentionText = Make(Color.FromRgb(0xE5, 0xB5, 0x62));
    public static readonly SolidColorBrush StatusAttentionBg = Make(Color.FromRgb(0x35, 0x35, 0x35));
    public static readonly SolidColorBrush StatusAvailableText = Make(Color.FromRgb(0x4C, 0xC2, 0xFF));
    public static readonly SolidColorBrush StatusAvailableBg = Make(Color.FromRgb(0x1C, 0x37, 0x4E));

    private static SolidColorBrush Make(Color color) => new(color);

    /// <summary>Rewrites every shared brush in place so all existing pages recolour live.</summary>
    internal static void ApplyPalette(ThemePalette p)
    {
        Bg.Color = p.WindowBg;
        Panel.Color = p.SidebarBg;
        Card.Color = p.CardBg;
        CardSoft.Color = p.CardSoftBg;
        Line.Color = p.Line;
        TextWhite.Color = p.TextPrimary;
        TextDim.Color = p.TextSecondary;
        TextMuted.Color = p.TextTertiary;
        TextFaint.Color = p.TextFaint;
        Accent.Color = p.Accent;
        Good.Color = p.Good;
        Warn.Color = p.Warn;
        Bad.Color = p.Bad;
        HoverFill.Color = p.HoverFill;
        SwitchOff.Color = p.SwitchOff;
        SwitchOn.Color = p.SwitchOn;
        StatusActiveText.Color = p.StatusActiveText;
        StatusActiveBg.Color = p.StatusActiveBg;
        StatusAttentionText.Color = p.StatusAttentionText;
        StatusAttentionBg.Color = p.StatusAttentionBg;
        StatusAvailableText.Color = p.StatusAvailableText;
        StatusAvailableBg.Color = p.StatusAvailableBg;
    }

    public static TextBlock Text(string content, double size = 13, Brush? brush = null, FontWeight? weight = null, bool wrap = true,
        Thickness? margin = null, FontFamily? fontFamily = null, double? lineHeight = null, TextTrimming? trimming = null)
    {
        var block = new TextBlock
        {
            Text = content,
            FontSize = size,
            Foreground = brush ?? TextWhite,
            FontWeight = weight ?? FontWeights.Normal,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap
        };
        if (margin is Thickness m) block.Margin = m;
        if (fontFamily is not null) block.FontFamily = fontFamily;
        if (lineHeight is double lh) block.LineHeight = lh;
        if (trimming is TextTrimming t) block.TextTrimming = t;
        return block;
    }

    public static Border CardPanel(FrameworkElement content, Thickness? padding = null)
        => new()
        {
            Background = Card,
            CornerRadius = new CornerRadius(10),
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            Padding = padding ?? new Thickness(16, 14, 16, 14),
            Margin = new Thickness(0, 0, 0, 12),
            Child = content
        };

    public static TextBlock SectionHeader(string text)
        => new()
        {
            Text = text,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextDim,
            Margin = new Thickness(2, 10, 0, 8)
        };

    public static Border Tag(string text, Brush color)
        => new()
        {
            Background = CardSoft,
            BorderBrush = color,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = text, FontSize = 11, Foreground = color }
        };

    /// <summary>
    /// v1-style general optimization status: a filled tinted pill (Active / Needs attention /
    /// Available), distinct from the outlined v2 metadata tags.
    /// </summary>
    public static Border StatusBox(string text, Brush color, Brush background)
        => new()
        {
            Background = background,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(9, 3, 9, 3),
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Right,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = color
            }
        };

    /// <summary>
    /// Minimalist line icon from a 24×24 path string. Stroke-only, so it inherits the muted
    /// palette and never introduces colourful illustrations.
    /// </summary>
    public static Viewbox Icon(string data, double size = 16, Brush? brush = null)
        => new()
        {
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(data),
                Stroke = brush ?? TextMuted,
                StrokeThickness = 1.5,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Fill = null
            }
        };

    public static ScrollViewer Scrollable(FrameworkElement content)
        => new()
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
}
