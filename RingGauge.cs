using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace SwiftwaveTweaks;

/// <summary>
/// Compact circular gauge used by the dashboard's live load panel: a hairline track, an accent
/// arc drawn clockwise from the top, and the percentage centred inside. A null value renders an
/// explicit "—" rather than a zero reading, so unavailable telemetry is never shown as 0%.
/// </summary>
public sealed class RingGauge : Grid
{
    private const double Diameter = 104;
    private const double Thickness = 7;
    private const double Radius = (Diameter - Thickness) / 2;

    private readonly Path _arc;
    private readonly TextBlock _value;

    public RingGauge(Brush accent)
    {
        Width = Diameter;
        Height = Diameter;

        Children.Add(new Ellipse
        {
            Stroke = Ui.Line,
            StrokeThickness = Thickness,
            Margin = new Thickness(Thickness / 2),
            IsHitTestVisible = false
        });

        _arc = new Path
        {
            Stroke = accent,
            StrokeThickness = Thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Fill = null,
            Stretch = Stretch.None,
            Width = Diameter,
            Height = Diameter,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false
        };
        Children.Add(_arc);

        _value = new TextBlock
        {
            Text = "—",
            FontSize = 21,
            FontWeight = FontWeights.SemiBold,
            Foreground = Ui.TextMuted,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Children.Add(_value);
    }

    /// <summary>Shows a percentage, or the unavailable placeholder when the reading failed.</summary>
    public void SetValue(double? percent)
    {
        if (percent is not double p)
        {
            _arc.Data = null;
            _value.Text = "—";
            _value.Foreground = Ui.TextMuted;
            return;
        }
        p = Math.Clamp(p, 0, 100);
        _arc.Data = Arc(p);
        _value.Text = $"{Math.Round(p):N0}%";
        _value.Foreground = Ui.TextWhite;
    }

    private static Geometry Arc(double percent)
    {
        double sweep = Math.Clamp(percent, 0, 100) * 3.6;
        if (sweep < 0.1) return Geometry.Empty;
        sweep = Math.Min(sweep, 359.9); // a full 360° arc would collapse to its start point
        var figure = new PathFigure { StartPoint = PointAt(-90), IsClosed = false };
        figure.Segments.Add(new ArcSegment(PointAt(-90 + sweep), new Size(Radius, Radius), 0,
            sweep > 180, SweepDirection.Clockwise, true));
        return new PathGeometry([figure]);
    }

    private static Point PointAt(double degrees)
    {
        double rad = degrees * Math.PI / 180.0;
        return new Point(Diameter / 2 + Radius * Math.Cos(rad), Diameter / 2 + Radius * Math.Sin(rad));
    }
}
