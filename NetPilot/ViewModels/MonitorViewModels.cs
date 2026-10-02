using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media;
using System.Windows.Threading;
using NetPilot.Core;
using NetPilot.Models;
using NetPilot.Services;
using L = NetPilot.Lang.Lang;

namespace NetPilot.ViewModels;

// ============================ Network Monitor ============================

public class MonitorViewModel : PageVmBase
{
    private string _downText = "0 B/s", _upText = "0 B/s", _pingText = "—", _lossText = "0%";
    private string _uptimeText = "—", _sessionDownText = "0 B", _sessionUpText = "0 B", _stateText = "";
    private double _orbDown, _orbUp;
    private int _orbState = 1;
    private string _target;

    public string DownText { get => _downText; set => Set(ref _downText, value); }
    public string UpText { get => _upText; set => Set(ref _upText, value); }
    public string PingText { get => _pingText; set => Set(ref _pingText, value); }
    public string LossText { get => _lossText; set => Set(ref _lossText, value); }
    public string UptimeText { get => _uptimeText; set => Set(ref _uptimeText, value); }
    public string SessionDownText { get => _sessionDownText; set => Set(ref _sessionDownText, value); }
    public string SessionUpText { get => _sessionUpText; set => Set(ref _sessionUpText, value); }
    public string StateText { get => _stateText; set => Set(ref _stateText, value); }
    public double OrbDown { get => _orbDown; set => Set(ref _orbDown, value); }
    public double OrbUp { get => _orbUp; set => Set(ref _orbUp, value); }
    public int OrbState { get => _orbState; set => Set(ref _orbState, value); }

    private IList<double> _chartDown = new List<double>();
    private IList<double> _chartUp = new List<double>();
    public IList<double> ChartDown { get => _chartDown; set => Set(ref _chartDown, value); }
    public IList<double> ChartUp { get => _chartUp; set => Set(ref _chartUp, value); }

    public string Target
    {
        get => _target;
        set { if (Set(ref _target, value)) SettingsService.Current.MonitorTarget = value; }
    }

    public MonitorViewModel()
    {
        _target = SettingsService.Current.MonitorTarget;
        var hist = MonitorService.Instance.GetHistory();
        if (hist.Count > 0)
        {
            ChartDown = hist.Select(s => s.DownBps).ToList();
            ChartUp = hist.Select(s => s.UpBps).ToList();
        }

        MonitorService.Instance.Sampled += s => Dispatch(() =>
        {
            if (!IsActive) return;   // hidden page: no chart rebuilds on every sample
            DownText = Core.SpeedFormatConverter.FormatSpeed(s.DownBps);
            UpText = Core.SpeedFormatConverter.FormatSpeed(s.UpBps);
            PingText = s.PingMs > 0 ? $"{s.PingMs:0} ms" : "—";
            LossText = $"{s.LossPct:0.#}%";
            UptimeText = s.Online ? FormatUptime(MonitorService.Instance.Uptime) : "—";
            SessionDownText = Core.SpeedFormatConverter.FormatBytes(MonitorService.Instance.SessionDown);
            SessionUpText = Core.SpeedFormatConverter.FormatBytes(MonitorService.Instance.SessionUp);
            StateText = s.Online ? (L.IsFa ? "متصل" : "Connected") : (L.IsFa ? "قطع" : "Disconnected");
            OrbDown = s.DownBps;
            OrbUp = s.UpBps;
            OrbState = s.Online ? (s.DownBps > 20_000 || s.UpBps > 10_000 ? 2 : 1) : 0;

            var dl = new List<double>(ChartDown) { s.DownBps };
            var ul = new List<double>(ChartUp) { s.UpBps };
            while (dl.Count > 120) dl.RemoveAt(0);
            while (ul.Count > 120) ul.RemoveAt(0);
            ChartDown = dl;
            ChartUp = ul;
        });
    }

    public override void OnActivated()
    {
        // The handler above is skipped while the page is hidden, so seed from the buffer.
        var hist = MonitorService.Instance.GetHistory();
        if (hist.Count == 0) return;
        var last = hist[hist.Count - 1];
        DownText = Core.SpeedFormatConverter.FormatSpeed(last.DownBps);
        UpText = Core.SpeedFormatConverter.FormatSpeed(last.UpBps);
        PingText = last.PingMs > 0 ? $"{last.PingMs:0} ms" : "—";
        LossText = $"{last.LossPct:0.#}%";
        UptimeText = last.Online ? FormatUptime(MonitorService.Instance.Uptime) : "—";
        SessionDownText = Core.SpeedFormatConverter.FormatBytes(MonitorService.Instance.SessionDown);
        SessionUpText = Core.SpeedFormatConverter.FormatBytes(MonitorService.Instance.SessionUp);
        StateText = last.Online ? (L.IsFa ? "متصل" : "Connected") : (L.IsFa ? "قطع" : "Disconnected");
        OrbDown = last.DownBps;
        OrbUp = last.UpBps;
        OrbState = last.Online ? (last.DownBps > 20_000 || last.UpBps > 10_000 ? 2 : 1) : 0;

        var dl = new List<double>();
        var ul = new List<double>();
        int start = Math.Max(0, hist.Count - 120);
        for (int i = start; i < hist.Count; i++) { dl.Add(hist[i].DownBps); ul.Add(hist[i].UpBps); }
        ChartDown = dl;
        ChartUp = ul;
    }

    private static string FormatUptime(TimeSpan t)
    {
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes:00}m";
        if (t.TotalMinutes >= 1) return $"{t.Minutes}m {t.Seconds:00}s";
        return $"{t.Seconds}s";
    }
}

// ============================ Per-App Usage ============================

public class AppRowVm : ObservableObject
{
    public int Pid { get; init; }
    public string Name { get; init; } = "";
    public string Process { get; init; } = "";
    public string Path { get; init; } = "";
    public string DownText { get; init; } = "0 B/s";
    public string UpText { get; init; } = "0 B/s";
    public string TotalDownText { get; init; } = "0 B";
    public string TotalUpText { get; init; } = "0 B";
    public double DownBps { get; init; }
    public double UpBps { get; init; }
    public long TotalDown { get; init; }
    public long TotalUp { get; init; }
    public int Connections { get; init; }
    public string StatusTag { get; init; } = "";
    public Brush StatusBrush { get; init; } = Brushes.Transparent;
    public string Glyph { get; init; } = "\uE7B4";
}

public class PerAppViewModel : PageVmBase
{
    public ObservableCollection<AppRowVm> Items { get; } = new();

    private string _search = "";
    public string Search { get => _search; set { if (Set(ref _search, value)) Refresh(); } }

    private string _sortKey = "down";
    public string SortKey { get => _sortKey; set { if (Set(ref _sortKey, value)) Refresh(); } }

    private bool _sortDesc = true;
    public bool SortDesc { get => _sortDesc; set { if (Set(ref _sortDesc, value)) Refresh(); } }

    private string _statusMessage = "";
    public string StatusMessage { get => _statusMessage; set => Set(ref _statusMessage, value); }

    private int _totalCount;
    public int TotalCount { get => _totalCount; set => Set(ref _totalCount, value); }

    private readonly DispatcherTimer _timer;

    public RelayCommand SortCommand { get; }

    public PerAppViewModel()
    {
        SortCommand = new RelayCommand(p =>
        {
            string key = p as string ?? "down";
            if (SortKey == key) SortDesc = !SortDesc;
            else { SortKey = key; SortDesc = true; }
        });

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => Refresh();
        // The timer starts in OnActivated: this VM is built once at startup, so ticking it
        // from the constructor kept refreshing a hidden page (and its hidden list) forever.
    }

    public override void OnActivated()
    {
        Refresh();
        _timer.Start();
    }

    public override void OnDeactivated() => _timer.Stop();

    private void Refresh()
    {
        try
        {
            var snapshot = ProcessNetworkService.Instance.GetSnapshot();
            var rules = LimiterService.Instance.GetRules();

            var rows = snapshot.Where(a =>
                string.IsNullOrWhiteSpace(_search) ||
                a.ProcessName.Contains(_search, StringComparison.OrdinalIgnoreCase) ||
                (a.Path?.Contains(_search, StringComparison.OrdinalIgnoreCase) ?? false));

            rows = _sortKey switch
            {
                "name" => rows.OrderBy(r => r.ProcessName, StringComparer.OrdinalIgnoreCase),
                "up" => rows.OrderByDescending(r => r.UpBps),
                "total" => rows.OrderByDescending(r => r.TotalDown + r.TotalUp),
                "conn" => rows.OrderByDescending(r => r.Connections),
                _ => rows.OrderByDescending(r => r.DownBps),
            };

            Items.Clear();
            foreach (var a in rows.Take(150))
            {
                var rule = rules.FirstOrDefault(r =>
                    (!string.IsNullOrEmpty(a.Path) && string.Equals(r.AppPath, a.Path, StringComparison.OrdinalIgnoreCase)) ||
                    string.Equals(System.IO.Path.GetFileNameWithoutExtension(r.AppPath ?? ""), a.ProcessName, StringComparison.OrdinalIgnoreCase));

                string tag = "";
                Brush brush = Brushes.Transparent;
                if (rule != null && rule.Enabled)
                {
                    if (rule.Mode == LimitMode.Block) { tag = L.IsFa ? "مسدود" : "blocked"; brush = new SolidColorBrush(Color.FromRgb(248, 113, 113)); }
                    else if (rule.Mode == LimitMode.Limit) { tag = L.IsFa ? "محدود" : "limited"; brush = new SolidColorBrush(Color.FromRgb(251, 191, 36)); }
                }

                Items.Add(new AppRowVm
                {
                    Pid = a.Pid,
                    Name = string.IsNullOrEmpty(a.DisplayName) ? a.ProcessName : a.DisplayName,
                    Process = a.ProcessName,
                    Path = a.Path ?? "",
                    DownBps = a.DownBps,
                    UpBps = a.UpBps,
                    DownText = Core.SpeedFormatConverter.FormatSpeed(a.DownBps),
                    UpText = Core.SpeedFormatConverter.FormatSpeed(a.UpBps),
                    TotalDown = a.TotalDown,
                    TotalUp = a.TotalUp,
                    TotalDownText = Core.SpeedFormatConverter.FormatBytes(a.TotalDown),
                    TotalUpText = Core.SpeedFormatConverter.FormatBytes(a.TotalUp),
                    Connections = a.Connections,
                    StatusTag = tag,
                    StatusBrush = brush,
                });
            }
            TotalCount = snapshot.Count;
            StatusMessage = ProcessNetworkService.Instance.EtwAvailable
                ? (L.IsFa ? "ترافیک لحظه‌ای هر برنامه از ETW ویندوز" : "Live per-app traffic via Windows ETW")
                : (L.IsFa ? "جمع‌آوری ترافیک در دسترس نیست" : "Traffic collection unavailable");
        }
        catch (Exception ex) { App.LogCrash(ex); }
    }
}
