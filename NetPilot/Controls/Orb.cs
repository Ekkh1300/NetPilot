using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace NetPilot.Controls;

/// <summary>
/// Live network orb: visualizes download/upload activity with smooth, cheap animations.
/// States: 0 = offline, 1 = idle/connected, 2 = active transfer.
/// </summary>
public class OrbControl : FrameworkElement
{
    public static readonly DependencyProperty DownBpsProperty =
        DependencyProperty.Register(nameof(DownBps), typeof(double), typeof(OrbControl),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty UpBpsProperty =
        DependencyProperty.Register(nameof(UpBps), typeof(double), typeof(OrbControl),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StateProperty =
        DependencyProperty.Register(nameof(State), typeof(int), typeof(OrbControl),
            new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsRender));

    public double DownBps { get => (double)GetValue(DownBpsProperty); set => SetValue(DownBpsProperty, value); }
    public double UpBps { get => (double)GetValue(UpBpsProperty); set => SetValue(UpBpsProperty, value); }
    public int State { get => (int)GetValue(StateProperty); set => SetValue(StateProperty, value); }

    private readonly DispatcherTimer _timer;
    private double _phase;
    private double _activity;   // smoothed 0..1
    private double _pulse;

    public OrbControl()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(66),
        };
        _timer.Tick += (_, _) =>
        {
            _phase += 0.045 + _activity * 0.14;
            _pulse = 0.5 + 0.5 * Math.Sin(_phase * 1.7);
            InvalidateVisual();
        };
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) _timer.Start(); else _timer.Stop(); };
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
    }

    private Color _colorCore => State == 0 ? Color.FromRgb(120, 60, 60)
        : State == 1 ? Color.FromRgb(56, 120, 189)
        : Color.FromRgb(56, 189, 248);

    private Color _colorAccent => State == 0 ? Color.FromRgb(150, 80, 80)
        : Color.FromRgb(129, 140, 248);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 30 || h < 30) return;

        double cx = w / 2, cy = h / 2;
        double r = Math.Min(w, h) / 2 - 8;

        // smooth activity (target from live speeds; log-scaled so small traffic still shows)
        double target = 0;
        if (State != 0)
        {
            double dl = Math.Log10(1 + Math.Max(0, DownBps) / 2048.0);
            double ul = Math.Log10(1 + Math.Max(0, UpBps) / 2048.0);
            target = Math.Clamp((dl + ul) / 5.0, 0, 1);
            if (DownBps > 50_000 || UpBps > 20_000) target = Math.Max(target, 0.35);
        }
        _activity += (target - _activity) * 0.18;
        double act = _activity;
        double pulse = State == 0 ? 0.3 : 0.75 + 0.25 * _pulse;

        // 1) outer glow
        double glowR = r * (1.18 + act * 0.22 + (State == 0 ? 0 : _pulse * 0.05));
        var glow = new RadialGradientBrush(
            Color.FromArgb((byte)(State == 0 ? 40 : 60 + act * 110), _colorAccent.R, _colorAccent.G, _colorAccent.B),
            Color.FromArgb(0, 0, 0, 0));
        glow.GradientOrigin = new Point(0.5, 0.5);
        dc.DrawEllipse(glow, null, new Point(cx, cy), glowR, glowR);

        // 2) background disc
        var disc = new RadialGradientBrush(Color.FromRgb(18, 26, 44), Color.FromRgb(11, 15, 26));
        disc.GradientOrigin = new Point(0.42, 0.38);
        dc.DrawEllipse(disc, null, new Point(cx, cy), r, r);

        // 3) activity arcs (download clockwise, upload counter-clockwise)
        double arcR = r * 0.94;
        var rect = new Rect(cx - arcR, cy - arcR, arcR * 2, arcR * 2);
        double dlFrac = Math.Clamp(Math.Log10(1 + Math.Max(0, DownBps) / 8192.0) / 4.0, 0.04, 1);
        double ulFrac = Math.Clamp(Math.Log10(1 + Math.Max(0, UpBps) / 8192.0) / 4.0, 0.04, 1);
        if (State == 0) { dlFrac = 0.16; ulFrac = 0.16; }

        var penDl = new Pen(new SolidColorBrush(_colorCore), 4.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var penUl = new Pen(new SolidColorBrush(_colorAccent), 3.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        penDl.Brush.Opacity = State == 0 ? 0.35 : 0.5 + act * 0.5;
        penUl.Brush.Opacity = State == 0 ? 0.25 : 0.4 + act * 0.6;
        dc.DrawArc(null, penDl, rect, -90 + _phase * (State == 0 ? 0 : 1), 360 * dlFrac);
        dc.DrawArc(null, penUl, rect, 90 - _phase * (State == 0 ? 0 : 1.4), -360 * ulFrac);

        // 4) core sphere with pulse
        double coreR = r * (0.62 + act * 0.06 + (State == 0 ? 0 : _pulse * 0.035));
        var core = new RadialGradientBrush(
            Color.FromArgb((byte)(160 + act * 95), _colorCore.R, _colorCore.G, _colorCore.B),
            Color.FromArgb(28, _colorCore.R, _colorCore.G, _colorCore.B));
        core.GradientOrigin = new Point(0.38, 0.35);
        dc.DrawEllipse(core, null, new Point(cx, cy), coreR, coreR);

        // specular highlight
        var spec = new RadialGradientBrush(Color.FromArgb((byte)(70 * pulse), 255, 255, 255), Color.FromArgb(0, 255, 255, 255));
        spec.GradientOrigin = new Point(0.5, 0.5);
        dc.DrawEllipse(spec, null, new Point(cx - coreR * 0.28, cy - coreR * 0.34), coreR * 0.5, coreR * 0.42);

        // 5) orbiting particles (speed scales with activity)
        if (State != 0)
        {
            for (int i = 0; i < 3; i++)
            {
                double speed = i == 2 ? -1.6 : 1.0 + i * 0.5;
                double a = _phase * speed * (0.5 + act * 1.6) + i * 2.094;
                double pr = arcR * (1.02 + 0.04 * Math.Sin(_phase + i));
                double px = cx + Math.Cos(a) * pr;
                double py = cy + Math.Sin(a) * pr;
                double size = 2.4 + act * 2.2;
                var particle = new RadialGradientBrush(
                    Color.FromArgb((byte)(150 + act * 105), 255, 255, 255),
                    Color.FromArgb(0, _colorCore.R, _colorCore.G, _colorCore.B));
                dc.DrawEllipse(particle, null, new Point(px, py), size, size);
            }
        }
        else
        {
            // offline: dashed inner ring
            var dash = new Pen(new SolidColorBrush(Color.FromRgb(150, 90, 90)), 1.6)
            {
                DashStyle = new DashStyle(new double[] { 2, 3 }, 0),
                StartLineCap = PenLineCap.Round,
            };
            double rr = r * 0.72;
            dc.DrawEllipse(null, dash, new Point(cx, cy), rr, rr);
        }
    }
}
