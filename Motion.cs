using System.Windows;
using System.Windows.Media.Animation;

namespace SwiftwaveTweaks;

/// <summary>
/// Shared animation timing/easing for the whole app. Honors the Windows
/// "Show animations" accessibility setting through <see cref="ReducedMotion"/>:
/// when reduced, callers skip storyboards and snap to final states.
/// </summary>
public static class Motion
{
    /// <summary>True when the user has disabled animations in Windows accessibility settings.</summary>
    public static bool ReducedMotion => !SystemParameters.ClientAreaAnimation;

    public static readonly Duration Fast = new(System.TimeSpan.FromMilliseconds(140));
    public static readonly Duration Medium = new(System.TimeSpan.FromMilliseconds(260));
    public static readonly Duration Slow = new(System.TimeSpan.FromMilliseconds(450));

    public static readonly CubicEase OutEasing = new() { EasingMode = EasingMode.EaseOut };
    public static readonly CubicEase InOutEasing = new() { EasingMode = EasingMode.EaseInOut };

    /// <summary>Fades an element to a target opacity with the shared easing.</summary>
    public static void FadeTo(UIElement element, double opacity, Duration? duration = null)
    {
        if (ReducedMotion)
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = opacity;
            return;
        }
        element.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(element.Opacity, opacity, duration ?? Medium) { EasingFunction = OutEasing });
    }
}
