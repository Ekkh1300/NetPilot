using System;
using System.Windows;
using System.Windows.Media;

namespace NetPilot.Controls;

/// <summary>
/// WPF's DrawingContext has no arc primitive, so the orb and gauges draw their own arcs.
/// All angles are degrees, measured clockwise from the positive X axis (screen space, Y down).
/// </summary>
public static class DrawingExtensions
{
    /// <summary>Draws (or fills) an elliptical arc segment. A null brush strokes only; a brush builds a wedge.</summary>
    public static void DrawArc(this DrawingContext dc, Brush brush, Pen pen, Rect bounds,
        double startAngle, double sweepAngle)
    {
        if (dc == null) return;
        if (Math.Abs(sweepAngle) < 0.01 || bounds.Width <= 1 || bounds.Height <= 1) return;

        double rx = bounds.Width / 2;
        double ry = bounds.Height / 2;
        double cx = bounds.X + rx;
        double cy = bounds.Y + ry;
        bool filled = brush != null;

        Point At(double deg)
        {
            double a = deg * Math.PI / 180.0;
            return new Point(cx + rx * Math.Cos(a), cy + ry * Math.Sin(a));
        }

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(At(startAngle), filled, false);

            // Split into <=179 degree hops: ArcTo silently draws nothing when the
            // endpoints coincide (a full 360 sweep would hit exactly that case).
            double remaining = sweepAngle;
            double angle = startAngle;
            while (Math.Abs(remaining) > 0.01)
            {
                double step = remaining > 0 ? Math.Min(remaining, 179.0) : Math.Max(remaining, -179.0);
                ctx.ArcTo(At(angle + step), new Size(rx, ry), 0, false,
                    step > 0 ? SweepDirection.Clockwise : SweepDirection.Counterclockwise,
                    true, true);
                angle += step;
                remaining -= step;
            }

            if (filled) ctx.Close();   // closes the wedge back through the centre
        }
        geo.Freeze();
        dc.DrawGeometry(brush, pen, geo);
    }
}
