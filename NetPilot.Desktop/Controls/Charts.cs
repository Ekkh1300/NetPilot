using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace NetPilot.Desktop.Controls;

/// <summary>
/// The live throughput sparkline.
///
/// The WPF version derives from FrameworkElement and paints itself into OnRender. Avalonia's
/// equivalent is a Render override on a Control, using its own DrawingContext - close enough
/// that the shape of the drawing code carries over, different enough that none of it is
/// reusable verbatim.
///
/// Drawn from the values it is given rather than from a live network, so it can be rendered
/// and screenshotted on any machine, including one with no phone attached.
/// </summary>
public sealed class SpeedChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> PointsProperty =
        AvaloniaProperty.Register<SpeedChart, IReadOnlyList<double>?>(nameof(Points));

    public static readonly StyledProperty<IBrush?> LineBrushProperty =
        AvaloniaProperty.Register<SpeedChart, IBrush?>(nameof(LineBrush), Brushes.SkyBlue);

    public static readonly StyledProperty<IBrush?> FillBrushProperty =
        AvaloniaProperty.Register<SpeedChart, IBrush?>(nameof(FillBrush), Brushes.SkyBlue);

    /// <summary>When set, a horizontal line is drawn at this value - the limit a rule imposes -
    /// so "over the line" is visible rather than something the user has to work out.</summary>
    public static readonly StyledProperty<double?> LimitProperty =
        AvaloniaProperty.Register<SpeedChart, double?>(nameof(Limit));

    public IReadOnlyList<double>? Points
    {
        get => GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public IBrush? LineBrush
    {
        get => GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    public IBrush? FillBrush
    {
        get => GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    public double? Limit
    {
        get => GetValue(LimitProperty);
        set => SetValue(LimitProperty, value);
    }

    /// <summary>
    /// A custom-drawn Avalonia control is not re-rendered when a bound property changes: the
    /// binding updates the value and nothing tells the control its drawing is stale. The first
    /// version of this chart therefore showed its empty baseline forever, even with a live
    /// rate updating every two seconds - the number on the tile moved and the line did not.
    ///
    /// So each control invalidates itself when the property it draws from changes.
    /// </summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PointsProperty || change.Property == LimitProperty)
            InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 4 || h < 4) return;

        var pts = Points;
        if (pts is null || pts.Count < 2)
        {
            // An empty chart draws its baseline only. Leaving it blank would be
            // indistinguishable from a broken one.
            ctx.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#1F2B41")), 1),
                new Point(0, h - 1), new Point(w, h - 1));
            return;
        }

        double max = 0;
        foreach (var p in pts) if (p > max) max = p;
        if (Limit is double lim && lim > max) max = lim;
        if (max <= 0) max = 1;

        double X(int i) => i * (w / (pts.Count - 1));
        double Y(double v) => h - 2 - (v / max) * (h - 6);

        if (Limit is double limit && limit > 0)
        {
            double ly = Y(limit);
            // The dash pattern belongs to the pen, not the brush - getting that wrong is a
            // compile error, but worth noting because it is the only way to show the ceiling
            // this chart is measured against.
            var dash = new Pen(new SolidColorBrush(Color.Parse("#FBBF24")), 1,
                               new DashStyle(new double[] { 4, 4 }, 0));
            ctx.DrawLine(dash, new Point(0, ly), new Point(w, ly));
        }

        var geometry = new StreamGeometry();
        using (var sink = geometry.Open())
        {
            sink.BeginFigure(new Point(0, h), false);
            for (int i = 0; i < pts.Count; i++)
                sink.LineTo(new Point(X(i), Y(pts[i])));
            sink.EndFigure(false);
        }

        if (FillBrush != null)
        {
            var fill = geometry;
            var fillGeom = new StreamGeometry();
            using (var sink = fillGeom.Open())
            {
                sink.BeginFigure(new Point(0, h), true);
                for (int i = 0; i < pts.Count; i++)
                    sink.LineTo(new Point(X(i), Y(pts[i])));
                sink.LineTo(new Point(w, h));
                sink.EndFigure(true);
            }
            ctx.DrawGeometry(FillBrush, null, fillGeom);
        }

        ctx.DrawGeometry(null, new Pen(LineBrush ?? Brushes.SkyBlue, 1.8), geometry);
    }
}

/// <summary>
/// A horizontal usage bar, used by the limiter and per-app pages.
///
/// Rendered rather than composed from a ProgressBar so the label and the value live inside the
/// same shape - which is how the WPF version draws it, and why a plain ProgressBar would look
/// like a different product.
/// </summary>
public sealed class UsageBar : Control
{
    public static readonly StyledProperty<double> FractionProperty =
        AvaloniaProperty.Register<UsageBar, double>(nameof(Fraction));

    public static readonly StyledProperty<IBrush?> BarBrushProperty =
        AvaloniaProperty.Register<UsageBar, IBrush?>(nameof(BarBrush), Brushes.SkyBlue);

    public static readonly StyledProperty<string> CaptionProperty =
        AvaloniaProperty.Register<UsageBar, string>(nameof(Caption), "");

    public double Fraction
    {
        get => GetValue(FractionProperty);
        set => SetValue(FractionProperty, value);
    }

    public IBrush? BarBrush
    {
        get => GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    public string Caption
    {
        get => GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FractionProperty || change.Property == CaptionProperty ||
            change.Property == BarBrushProperty)
            InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 4 || h < 4) return;

        const double labelW = 148;
        double barX = labelW + 8;
        double barW = Math.Max(20, w - barX - 78);
        const double barH = 8;
        double barY = (h - barH) / 2;

        var label = new FormattedText(
            Caption ?? "", System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, Typeface.Default, 12, Brushes.White);
        ctx.DrawText(label, new Point(0, (h - 16) / 2));

        ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#1F2B41")), null,
            new RoundedRect(new Rect(barX, barY, barW, barH), 4));

        // Clamped: a value above the limit must fill the bar, not overflow the card.
        double f = Math.Clamp(Fraction, 0, 1);
        if (f > 0)
            ctx.DrawRectangle(BarBrush ?? Brushes.SkyBlue, null,
                new RoundedRect(new Rect(barX, barY, barW * f, barH), 4));

        var pct = new FormattedText(
            $"{Fraction * 100:0.#}%", System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, Typeface.Default, 12, Brushes.White);
        ctx.DrawText(pct, new Point(barX + barW + 10, (h - 16) / 2));
    }
}

/// <summary>The circular gauge on the dashboard.</summary>
public sealed class DonutGauge : Control
{
    public static readonly StyledProperty<double> FractionProperty =
        AvaloniaProperty.Register<DonutGauge, double>(nameof(Fraction));

    public static readonly StyledProperty<string> CenterTextProperty =
        AvaloniaProperty.Register<DonutGauge, string>(nameof(CenterText), "");

    public static readonly StyledProperty<string> CaptionProperty =
        AvaloniaProperty.Register<DonutGauge, string>(nameof(Caption), "");

    public double Fraction
    {
        get => GetValue(FractionProperty);
        set => SetValue(FractionProperty, value);
    }

    public string CenterText
    {
        get => GetValue(CenterTextProperty);
        set => SetValue(CenterTextProperty, value);
    }

    public string Caption
    {
        get => GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FractionProperty || change.Property == CenterTextProperty ||
            change.Property == CaptionProperty)
            InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 20 || h < 20) return;

        double size = Math.Min(w, h);
        var cx = w / 2;
        var cy = h / 2;
        double r = size / 2 - 12;
        if (r <= 2) return;
        double thickness = Math.Max(6, size / 12);

        // Avalonia's DrawingContext has no stroke-arc, so the ring is built as a filled annulus
        // from two arc segments rather than as a stroked circle. An earlier attempt used two
        // concentric circles, which renders as a filled disc, because the inner circle does
        // not punch a hole.
        var outer = new Rect(cx - r, cy - r, r * 2, r * 2);
        ctx.DrawEllipse(new SolidColorBrush(Color.Parse("#1F2B41")), null, outer);

        double f = Math.Clamp(Fraction, 0, 1);
        if (f > 0.001)
        {
            double outerR = r + thickness / 2;
            double innerR = Math.Max(0.5, r - thickness / 2);
            double sweep = 360 * f;
            // A full circle cannot be an arc segment (start and end coincide), so a complete
            // dial is drawn as two halves. Without this the ring is empty at exactly 100%.
            bool full = sweep >= 359.99;
            if (full) sweep = 359.99;

            var ring = new StreamGeometry();
            using (var sink = ring.Open())
            {
                sink.BeginFigure(OnRing(cx, cy, outerR, 0), false);
                sink.ArcTo(new Point(cx + outerR, cy), new Size(outerR, outerR), 0, isLargeArc: sweep > 180, sweep > 180 ? SweepDirection.Clockwise : SweepDirection.Clockwise);
                sink.ArcTo(OnRing(cx, cy, outerR, sweep), new Size(outerR, outerR), 0, isLargeArc: sweep > 180, SweepDirection.Clockwise);
                sink.LineTo(OnRing(cx, cy, innerR, sweep));
                sink.ArcTo(OnRing(cx, cy, innerR, 0), new Size(innerR, innerR), 0, isLargeArc: sweep > 180, SweepDirection.CounterClockwise);
                sink.ArcTo(OnRing(cx, cy, innerR, 0), new Size(innerR, innerR), 0, isLargeArc: false, SweepDirection.CounterClockwise);
                sink.EndFigure(true);
            }
            ctx.DrawGeometry(new SolidColorBrush(Color.Parse("#38BDF8")), null, ring);
            if (full)
            {
                // Close the last degree with a second geometry rather than pretending 359.99
                // is a full ring; the seam is invisible and the dial is honest.
                var cap = new StreamGeometry();
                using (var sink = cap.Open())
                {
                    sink.BeginFigure(OnRing(cx, cy, outerR, 359.99), false);
                    sink.ArcTo(OnRing(cx, cy, outerR, 360), new Size(outerR, outerR), 0, false, SweepDirection.Clockwise);
                    sink.LineTo(OnRing(cx, cy, innerR, 360));
                    sink.ArcTo(OnRing(cx, cy, innerR, 359.99), new Size(innerR, innerR), 0, false, SweepDirection.CounterClockwise);
                    sink.EndFigure(true);
                }
                ctx.DrawGeometry(new SolidColorBrush(Color.Parse("#38BDF8")), null, cap);
            }
        }

        if (!string.IsNullOrEmpty(CenterText))
        {
            var t = new FormattedText(CenterText, System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold),
                20, Brushes.White);
            ctx.DrawText(t, new Point(cx - t.Width / 2, cy - t.Height / 2));
        }
        if (!string.IsNullOrEmpty(Caption))
        {
            var c = new FormattedText(Caption, System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, Typeface.Default, 11, new SolidColorBrush(Color.Parse("#93A1BC")));
            ctx.DrawText(c, new Point(cx - c.Width / 2, cy + r - 2));
        }
    }

    /// <summary>Point on a circle, measured clockwise from twelve o'clock.</summary>
    private static Point OnRing(double cx, double cy, double radius, double degrees)
    {
        double rad = (degrees - 90) * Math.PI / 180.0;
        return new Point(cx + radius * Math.Cos(rad), cy + radius * Math.Sin(rad));
    }
}