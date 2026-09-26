using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SwiftwaveTweaks;

/// <summary>
/// The Swiftwave toggle switch: a rounded track with a sliding thumb, animated with the
/// shared motion system. Reflects real system state — set through <see cref="SetState"/>
/// while detection runs (no Toggled event fires), and toggled by the user through clicks.
/// </summary>
public sealed class ToggleSwitch : ToggleButton
{
    private readonly Border track;
    private readonly Border thumb;
    private readonly TranslateTransform thumbShift;

    private const double TrackWidth = 44;
    private const double TrackHeight = 24;
    private const double ThumbSize = 16;
    private const double ThumbMargin = 4;
    private const double Travel = TrackWidth - ThumbSize - ThumbMargin * 2;

    /// <summary>Fired only for user-initiated toggles (not programmatic state sync).</summary>
    public event EventHandler<bool>? Toggled;

    public ToggleSwitch()
    {
        Width = TrackWidth;
        Height = TrackHeight;
        Cursor = Cursors.Hand;
        VerticalAlignment = VerticalAlignment.Center;
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        // WPF's default ToggleButton template draws checked chrome (white box). Replace it with a
        // bare chromeless surface using a parsed XAML template so hit-testing works reliably.
        string xaml =
            @"<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ToggleButton'>
                <Border Background='Transparent'>
                    <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
                </Border>
            </ControlTemplate>";
        Template = (ControlTemplate)XamlReader.Parse(xaml);

        track = new Border
        {
            Width = TrackWidth,
            Height = TrackHeight,
            CornerRadius = new CornerRadius(TrackHeight / 2),
            Background = Ui.SwitchOff,
            BorderThickness = new Thickness(0)
        };
        thumbShift = new TranslateTransform(0, 0);
        thumb = new Border
        {
            Width = ThumbSize,
            Height = ThumbSize,
            CornerRadius = new CornerRadius(ThumbSize / 2),
            Background = Ui.TextWhite,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(ThumbMargin, 0, 0, 0),
            RenderTransform = thumbShift
        };

        var grid = new Grid();
        grid.Children.Add(track);
        grid.Children.Add(thumb);
        Content = grid;

        Click += (_, _) => Toggled?.Invoke(this, IsChecked == true);
        Loaded += (_, _) => Sync(animate: false);
        IsEnabledChanged += (_, _) => Sync(animate: false);
    }

    /// <summary>Sets the switch position without raising <see cref="Toggled"/> (used by detection refresh).</summary>
    public void SetState(bool on)
    {
        if (IsChecked == on) { Sync(animate: false); return; }
        IsChecked = on;
        Sync(animate: !Motion.ReducedMotion);
    }

    protected override void OnChecked(RoutedEventArgs e) { base.OnChecked(e); Sync(animate: true); }
    protected override void OnUnchecked(RoutedEventArgs e) { base.OnUnchecked(e); Sync(animate: true); }

    private void Sync(bool animate)
    {
        bool on = IsChecked == true;
        double x = on ? Travel : 0;
        Brush trackBrush = on ? Ui.SwitchOn : Ui.SwitchOff;
        Brush thumbBrush = Ui.TextWhite;
        if (!IsEnabled)
        {
            trackBrush = Ui.Card;
            thumbBrush = Ui.TextFaint;
        }

        if (!animate || Motion.ReducedMotion)
        {
            thumbShift.BeginAnimation(TranslateTransform.XProperty, null);
            thumbShift.X = x;
            track.Background = trackBrush;
            thumb.Background = thumbBrush;
            return;
        }

        thumbShift.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(thumbShift.X, x, Motion.Medium) { EasingFunction = Motion.OutEasing });
        AnimateBrush(track, Border.BackgroundProperty, trackBrush);
        AnimateBrush(thumb, Border.BackgroundProperty, thumbBrush);
    }

    private static void AnimateBrush(Border target, DependencyProperty property, Brush to)
    {
        if (target.Background is not SolidColorBrush from || ((SolidColorBrush)to).Color == from.Color)
        {
            target.SetValue(property, to);
            return;
        }
        var brush = new SolidColorBrush(from.Color);
        target.SetValue(property, brush);
        brush.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(from.Color, ((SolidColorBrush)to).Color, Motion.Medium) { EasingFunction = Motion.OutEasing });
    }
}
