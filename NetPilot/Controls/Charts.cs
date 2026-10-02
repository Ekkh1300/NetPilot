using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace NetPilot.Controls;

public class BarPoint
{
    public string Label { get; set; }
    public double Value { get; set; }
    public double Value2 { get; set; }
    public string ValueText { get; set; } = "";
}

/// <summary>Smooth live dual-series area chart (download / upload) drawn with WPF vectors.</summary>
public class SpeedChart : FrameworkElement
{
    public static readonly DependencyProperty DownValuesProperty =
        DependencyProperty.Register(nameof(DownValues), typeof(IList<double>), typeof(SpeedChart),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty UpValuesProperty =
        DependencyProperty.Register(nameof(UpValues), typeof(IList<double>), typeof(SpeedChart),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IList<double> DownValues { get => (IList<double>)GetValue(DownValuesProperty); set => SetValue(DownValuesProperty, value); }
    public IList<double> UpValues { get => (IList<double>)GetValue(UpValuesProperty); set => SetValue(UpValuesProperty, value); }

    private static readonly Pen GridPen = new(new SolidColorBrush(Color.FromRgb(31, 43, 65)), 1);
    private static readonly Brush DownFill = MakeGradient(Color.FromArgb(90, 56, 189, 248), Colors.Transparent);
    private static readonly Brush UpFill = MakeGradient(Color.FromArgb(70, 129, 140, 248), Colors.Transparent);
    private static readonly Pen DownPen = new(new SolidColorBrush(Color.FromRgb(56, 189, 248)), 2);
    private static readonly Pen UpPen = new(new SolidColorBrush(Color.FromRgb(129, 140, 248)), 2);

    private static Brush MakeGradient(Color from, Color to)
    {
        var g = new LinearGradientBrush(from, to, 90);
        g.Freeze();
        return g;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 10 || h <= 10) return;

        // grid
        for (int i = 1; i < 4; i++)
        {
            double y = h * i / 4.0;
            dc.DrawLine(GridPen, new Point(0, y), new Point(w, y));
        }

        var down = DownValues;
        var up = UpValues;
        double max = 1;
        if (down != null) foreach (var v in down) max = Math.Max(max, v);
        if (up != null) foreach (var v in up) max = Math.Max(max, v);
        max = NiceCeiling(max);

        DrawSeries(dc, down, w, h, max, DownPen, DownFill);
        DrawSeries(dc, up, w, h, max, UpPen, UpFill);

        // scale labels
        DrawLabel(dc, FormatSpeed(max), new Point(6, 4), Brushes.Gray);
        DrawLabel(dc, FormatSpeed(max / 2), new Point(6, h / 2 - 14), Brushes.Gray);
        DrawLabel(dc, "0", new Point(6, h - 16), Brushes.Gray);
    }

    private static string FormatSpeed(double bps) => Core.SpeedFormatConverter.FormatSpeed(bps);

    private static void DrawLabel(DrawingContext dc, string text, Point p, Brush b)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 10, b, 96);
        dc.DrawText(ft, p);
    }

    private static double NiceCeiling(double v)
    {
        if (v < 1024) return 1024;
        double mag = Math.Pow(10, Math.Floor(Math.Log10(v)));
        double norm = v / mag;
        double nice = norm <= 1 ? 1 : norm <= 2 ? 2 : norm <= 5 ? 5 : 10;
        return nice * mag;
    }

    private static void DrawSeries(DrawingContext dc, IList<double> values, double w, double h,
        double max, Pen pen, Brush fill)
    {
        if (values == null || values.Count < 2) return;
        int n = values.Count;
        var pts = new Point[n];
        for (int i = 0; i < n; i++)
        {
            double x = w * i / (n - 1);
            double y = h - Math.Clamp(values[i] / max, 0, 1) * (h - 6) - 3;
            pts[i] = new Point(x, y);
        }

        // smooth curve through midpoints (quadratic)
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(0, h), true, true);
            ctx.LineTo(pts[0], false, false);
            for (int i = 1; i < n; i++)
            {
                var prev = pts[i - 1];
                var cur = pts[i];
                var mid = new Point((prev.X + cur.X) / 2, (prev.Y + cur.Y) / 2);
                ctx.QuadraticBezierTo(prev, mid, false, false);
            }
            ctx.LineTo(pts[n - 1], false, false);
            ctx.LineTo(new Point(w, h), false, false);
        }
        geo.Freeze();
        dc.DrawGeometry(fill, null, geo);

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(pts[0], false, false);
            for (int i = 1; i < n; i++)
            {
                var prev = pts[i - 1];
                var cur = pts[i];
                var mid = new Point((prev.X + cur.X) / 2, (prev.Y + cur.Y) / 2);
                ctx.QuadraticBezierTo(prev, mid, false, false);
            }
            ctx.LineTo(pts[n - 1], false, false);
        }
        line.Freeze();
        dc.DrawGeometry(null, pen, line);
    }
}

/// <summary>Grouped column chart used by Network History (download + upload per day).</summary>
public class ColumnChart : FrameworkElement
{
    public static readonly DependencyProperty ItemsProperty =
        DependencyProperty.Register(nameof(Items), typeof(IList<BarPoint>), typeof(ColumnChart),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public IList<BarPoint> Items { get => (IList<BarPoint>)GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 20 || h <= 30) return;

        var items = Items;
        if (items == null || items.Count == 0) return;

        double max = 1;
        foreach (var it in items) max = Math.Max(max, it.Value + it.Value2);

        var gridPen = new Pen(new SolidColorBrush(Color.FromRgb(31, 43, 65)), 1);
        for (int i = 1; i < 4; i++)
        {
            double y = h * i / 4.0;
            dc.DrawLine(gridPen, new Point(0, y), new Point(w, y));
        }

        double slot = w / items.Count;
        double barW = Math.Min(34, slot * 0.55);
        double bottom = h - 20;

        var downBrush = new LinearGradientBrush(Color.FromRgb(56, 189, 248), Color.FromRgb(129, 140, 248), 90);
        var upBrush = new SolidColorBrush(Color.FromRgb(99, 102, 241));

        for (int i = 0; i < items.Count; i++)
        {
            double cx = slot * i + slot / 2;
            double hDown = (items[i].Value / max) * (bottom - 8);
            double hUp = (items[i].Value2 / max) * (bottom - 8);

            dc.DrawRoundedRectangle(downBrush, null,
                new Rect(cx - barW / 2, bottom - hDown, barW, hDown), 5, 5);
            dc.DrawRectangle(upBrush, null,
                new Rect(cx - barW / 2, bottom - hDown - hUp, barW, Math.Max(hUp, 1)));

            if (!string.IsNullOrEmpty(items[i].Label))
            {
                var ft = new FormattedText(items[i].Label, CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, Brushes.Gray, 96);
                dc.DrawText(ft, new Point(cx - ft.Width / 2, h - 15));
            }
        }
    }
}

/// <summary>Horizontal bars used by DNS benchmark results.</summary>
public class HBarChart : FrameworkElement
{
    public static readonly DependencyProperty ItemsProperty =
        DependencyProperty.Register(nameof(Items), typeof(IList<BarPoint>), typeof(HBarChart),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public IList<BarPoint> Items { get => (IList<BarPoint>)GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 40 || h <= 20) return;
        var items = Items;
        if (items == null || items.Count == 0) return;

        double max = 1;
        foreach (var it in items) max = Math.Max(max, it.Value);

        double rowH = h / items.Count;
        double labelW = Math.Min(150, w * 0.3);
        double barArea = w - labelW - 70;

        for (int i = 0; i < items.Count; i++)
        {
            double y = i * rowH + rowH * 0.2;
            double bh = rowH * 0.5;

            var name = new FormattedText(items[i].Label, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                Math.Min(13, bh), Brushes.LightGray, 96);
            dc.DrawText(name, new Point(0, y + (bh - name.Height) / 2));

            double bw = Math.Max(4, items[i].Value / max * barArea);
            var brush = new LinearGradientBrush(Color.FromRgb(56, 189, 248), Color.FromRgb(129, 140, 248), 0);
            dc.DrawRoundedRectangle(brush, null, new Rect(labelW, y, bw, bh), 6, 6);

            var val = new FormattedText(items[i].ValueText, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, Brushes.White, 96);
            dc.DrawText(val, new Point(labelW + bw + 8, y + (bh - val.Height) / 2));
        }
    }
}

/// <summary>Ring gauge (health score, usage share).</summary>
public class DonutGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(double), typeof(DonutGauge),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MaxValueProperty =
        DependencyProperty.Register(nameof(MaxValue), typeof(double), typeof(DonutGauge),
            new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FillProperty =
        DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(DonutGauge),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double MaxValue { get => (double)GetValue(MaxValueProperty); set => SetValue(MaxValueProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 8 || h <= 8) return;
        double size = Math.Min(w, h) - 6;
        var rect = new Rect((w - size) / 2, (h - size) / 2, size, size);
        double thickness = Math.Max(7, size * 0.09);

        var bg = new Pen(new SolidColorBrush(Color.FromRgb(31, 43, 65)), thickness);
        bg.StartLineCap = PenLineCap.Round;
        bg.EndLineCap = PenLineCap.Round;
        dc.DrawEllipse(null, bg, new Point(w / 2, h / 2), size / 2, size / 2);

        double frac = MaxValue <= 0 ? 0 : Math.Clamp(Value / MaxValue, 0, 1);
        if (frac > 0.001)
        {
            var fillPen = new Pen(Fill ?? new SolidColorBrush(Color.FromRgb(56, 189, 248)), thickness);
            fillPen.StartLineCap = PenLineCap.Round;
            fillPen.EndLineCap = PenLineCap.Round;
            double start = -90;
            double sweep = 360 * frac;
            dc.DrawArc(null, fillPen, rect, start, sweep);
        }
    }
}
