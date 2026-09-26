using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SwiftwaveTweaks;

/// <summary>
/// Central dark theme palette and small control factories used across the reconstructed UI.
/// Matches the surviving XAML design language: near-black background, near-white text,
/// muted gray hierarchy, rounded cards, subtle accent. No neon, no RGB, no gradients.
/// </summary>
public static class Ui
{
    public static readonly SolidColorBrush Bg = Freeze(Color.FromRgb(0x0B, 0x10, 0x1A));       // window background
    public static readonly SolidColorBrush Panel = Freeze(Color.FromRgb(0x10, 0x19, 0x26));     // sidebar
    public static readonly SolidColorBrush Card = Freeze(Color.FromRgb(0x14, 0x1F, 0x2E));      // card fill
    public static readonly SolidColorBrush CardSoft = Freeze(Color.FromRgb(0x18, 0x25, 0x36));  // nested fill
    public static readonly SolidColorBrush Line = Freeze(Color.FromRgb(0x24, 0x33, 0x45));      // hairlines
    public static readonly SolidColorBrush TextWhite = Freeze(Color.FromRgb(0xF5, 0xF8, 0xFC)); // primary text
    public static readonly SolidColorBrush TextDim = Freeze(Color.FromRgb(0x8C, 0xA0, 0xB5));   // secondary text
    public static readonly SolidColorBrush TextMuted = Freeze(Color.FromRgb(0x65, 0x7A, 0x90)); // tertiary text
    public static readonly SolidColorBrush TextFaint = Freeze(Color.FromRgb(0x4C, 0x5F, 0x75)); // metadata text
    public static readonly SolidColorBrush Accent = Freeze(Color.FromRgb(0x4C, 0xC2, 0xFF));    // busy dots / focus
    public static readonly SolidColorBrush Good = Freeze(Color.FromRgb(0x7C, 0xC9, 0x8B));      // optimized
    public static readonly SolidColorBrush Warn = Freeze(Color.FromRgb(0xE0, 0xB1, 0x6A));      // needs attention
    public static readonly SolidColorBrush Bad = Freeze(Color.FromRgb(0xE0, 0x8A, 0x8A));       // failed / danger
    public static readonly SolidColorBrush HoverFill = Freeze(Color.FromRgb(0x2E, 0x42, 0x59));
    public static readonly SolidColorBrush SwitchOff = Freeze(Color.FromRgb(0x2A, 0x3A, 0x4F)); // toggle track, off
    public static readonly SolidColorBrush SwitchOn = Freeze(Color.FromRgb(0x59, 0xC2, 0x89));  // toggle track, on

    // v1-style general status boxes (filled tinted pills, top-right of optimization cards).
    public static readonly SolidColorBrush StatusActiveText = Freeze(Color.FromRgb(0x59, 0xC2, 0x89));
    public static readonly SolidColorBrush StatusActiveBg = Freeze(Color.FromRgb(0x1E, 0x37, 0x3B));
    public static readonly SolidColorBrush StatusAttentionText = Freeze(Color.FromRgb(0xE5, 0xB5, 0x62));
    public static readonly SolidColorBrush StatusAttentionBg = Freeze(Color.FromRgb(0x35, 0x35, 0x35));
    public static readonly SolidColorBrush StatusAvailableText = Freeze(Color.FromRgb(0x4C, 0xC2, 0xFF));
    public static readonly SolidColorBrush StatusAvailableBg = Freeze(Color.FromRgb(0x1C, 0x37, 0x4E));

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
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

    public static ScrollViewer Scrollable(FrameworkElement content)
        => new()
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
}
