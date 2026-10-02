using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media;
using NetPilot.Controls;
using NetPilot.Core;
using NetPilot.Models;
using NetPilot.Services;
using L = NetPilot.Lang.Lang;

namespace NetPilot.ViewModels;

// ============================ Dashboard ============================

public class DashboardViewModel : PageVmBase
{
    private double _score;
    public double Score { get => _score; set => Set(ref _score, value); }
    private string _scoreText = "—";
    public string ScoreText { get => _scoreText; set => Set(ref _scoreText, value); }
    private Brush _scoreBrush = Brushes.Gray;
    public Brush ScoreBrush { get => _scoreBrush; set => Set(ref _scoreBrush, value); }

    private string _dnsRating = "—", _latRating = "—", _lossRating = "—", _connRating = "—";
    public string DnsRating { get => _dnsRating; set => Set(ref _dnsRating, value); }
    public string LatRating { get => _latRating; set => Set(ref _latRating, value); }
    public string LossRating { get => _lossRating; set => Set(ref _lossRating, value); }
    public string ConnRating { get => _connRating; set => Set(ref _connRating, value); }

    private string _dnsDetail = "—", _latDetail = "—", _lossDetail = "—", _connDetail = "—";
    public string DnsDetail { get => _dnsDetail; set => Set(ref _dnsDetail, value); }
    public string LatDetail { get => _latDetail; set => Set(ref _latDetail, value); }
    public string LossDetail { get => _lossDetail; set => Set(ref _lossDetail, value); }
    public string ConnDetail { get => _connDetail; set => Set(ref _connDetail, value); }

    private string _downText = "0 B/s", _upText = "0 B/s", _currentDns = "…", _todayText = "0 B", _topApp = "—", _healthState = "";
    public string DownText { get => _downText; set => Set(ref _downText, value); }
    public string UpText { get => _upText; set => Set(ref _upText, value); }
    public string CurrentDns { get => _currentDns; set => Set(ref _currentDns, value); }
    public string TodayText { get => _todayText; set => Set(ref _todayText, value); }
    public string TopApp { get => _topApp; set => Set(ref _topApp, value); }
    public string HealthState { get => _healthState; set => Set(ref _healthState, value); }

    private double _orbDown, _orbUp;
    public double OrbDown { get => _orbDown; set => Set(ref _orbDown, value); }
    public double OrbUp { get => _orbUp; set => Set(ref _orbUp, value); }
    private int _orbState = 1;
    public int OrbState { get => _orbState; set => Set(ref _orbState, value); }

    private IList<double> _chartDown = new List<double>();
    private IList<double> _chartUp = new List<double>();
    public IList<double> ChartDown { get => _chartDown; set => Set(ref _chartDown, value); }
    public IList<double> ChartUp { get => _chartUp; set => Set(ref _chartUp, value); }

    public RelayCommand GoMonitorCommand { get; }
    public RelayCommand GoBenchmarkCommand { get; }
    public RelayCommand GoToolsCommand { get; }
    public RelayCommand GoHistoryCommand { get; }
    public RelayCommand GoDnsCommand { get; }

    private DateTime _lastUsage = DateTime.MinValue;

    public DashboardViewModel()
    {
        GoMonitorCommand = new RelayCommand(() => Navigate("network_monitor"));
        GoBenchmarkCommand = new RelayCommand(() => Navigate("dns_benchmark"));
        GoToolsCommand = new RelayCommand(() => Navigate("network_tools"));
        GoHistoryCommand = new RelayCommand(() => Navigate("network_history"));
        GoDnsCommand = new RelayCommand(() => Navigate("dns_manager"));

        MonitorService.Instance.Sampled += s => Dispatch(() =>
        {
            if (!IsActive) return;   // hidden page: no chart rebuilds on every sample
            DownText = Core.SpeedFormatConverter.FormatSpeed(s.DownBps);
            UpText = Core.SpeedFormatConverter.FormatSpeed(s.UpBps);
            OrbDown = s.DownBps;
            OrbUp = s.UpBps;
            OrbState = s.Online ? (s.DownBps > 20_000 || s.UpBps > 10_000 ? 2 : 1) : 0;
            var dl = new List<double>(ChartDown) { s.DownBps };
            var ul = new List<double>(ChartUp) { s.UpBps };
            while (dl.Count > 100) dl.RemoveAt(0);
            while (ul.Count > 100) ul.RemoveAt(0);
            ChartDown = dl;
            ChartUp = ul;

            if ((DateTime.Now - _lastUsage).TotalSeconds > 10)
            {
                _lastUsage = DateTime.Now;
                var today = UsageService.Instance.GetToday();
                TodayText = Core.SpeedFormatConverter.FormatBytes(today.Total);
                TopApp = today.TopApp;
            }
        });

        HealthService.Instance.Updated += snap => Dispatch(() => { if (IsActive) ApplyHealth(snap); });
        var latest = HealthService.Instance.Latest;
        if (latest.Score > 0 || latest.DnsRating != "—") ApplyHealth(latest);
    }

    public override void OnActivated()
    {
        _ = RefreshDnsAsync();
        var snap = HealthService.Instance.Latest;
        if (snap != null) ApplyHealth(snap);
        var today = UsageService.Instance.GetToday();
        TodayText = Core.SpeedFormatConverter.FormatBytes(today.Total);
        TopApp = today.TopApp;
        SeedFromMonitor();
    }

    /// <summary>Rebuilds the live tile and chart straight from the sampler's buffer, so a
    /// page that was skipped while hidden shows real numbers the moment it is shown.</summary>
    private void SeedFromMonitor()
    {
        var hist = MonitorService.Instance.GetHistory();
        if (hist.Count == 0) return;

        var dl = new List<double>();
        var ul = new List<double>();
        int start = Math.Max(0, hist.Count - 100);
        for (int i = start; i < hist.Count; i++) { dl.Add(hist[i].DownBps); ul.Add(hist[i].UpBps); }
        ChartDown = dl;
        ChartUp = ul;

        var last = hist[hist.Count - 1];
        DownText = Core.SpeedFormatConverter.FormatSpeed(last.DownBps);
        UpText = Core.SpeedFormatConverter.FormatSpeed(last.UpBps);
        OrbDown = last.DownBps;
        OrbUp = last.UpBps;
        OrbState = last.Online ? (last.DownBps > 20_000 || last.UpBps > 10_000 ? 2 : 1) : 0;
    }

    private async System.Threading.Tasks.Task RefreshDnsAsync()
    {
        try { CurrentDns = await DnsService.DescribeCurrentAsync(); }
        catch { CurrentDns = "—"; }
    }

    private void ApplyHealth(HealthSnapshot s)
    {
        Score = s.Score;
        ScoreText = $"{s.Score:0}%";
        ScoreBrush = s.Score >= 85 ? new SolidColorBrush(Color.FromRgb(52, 211, 153))
                   : s.Score >= 60 ? new SolidColorBrush(Color.FromRgb(251, 191, 36))
                   : new SolidColorBrush(Color.FromRgb(248, 113, 113));
        DnsRating = RateLabel(s.DnsRating);
        LatRating = RateLabel(s.LatencyRating);
        LossRating = RateLabel(s.LossRating);
        ConnRating = s.ConnRating is "—" ? "—"
            : s.ConnRating is "Stable" or "MostlyStable"
            ? (L.IsFa ? "پایدار" : "Stable")
            : (L.IsFa ? "ناپایدار" : "Unstable");
        DnsDetail = s.DnsMs > 0 ? $"{s.DnsMs:0} ms" : "—";
        LatDetail = s.PingMs > 0 ? $"{s.PingMs:0} ms" : "—";
        LossDetail = s.LossPct >= 0 ? $"{s.LossPct:0.#}%" : "—";
        ConnDetail = s.Online ? (L.IsFa ? "متصل" : "Online") : (L.IsFa ? "قطع" : "Offline");
        HealthState = s.Online
            // The band that "in good shape" refers to is the one the ratings use, so a
            // network labelled "Good" is not simultaneously told it has problems.
            ? (s.Score >= 65 ? (L.IsFa ? "شبکه در وضعیت مطلوب" : "Network is in good shape")
                             : (L.IsFa ? "شبکه مشکل دارد" : "Network has issues"))
            : (L.IsFa ? "آفلاین" : "Offline");
    }

    private string RateLabel(string rating) => rating switch
    {
        "Excellent" => L.IsFa ? "عالی" : "Excellent",
        "Good" => L.IsFa ? "خوب" : "Good",
        "Fair" => L.IsFa ? "متوسط" : "Fair",
        "Poor" => L.IsFa ? "ضعیف" : "Poor",
        _ => rating,
    };

    protected override void OnLanguageChanged()
    {
        var snap = HealthService.Instance.Latest;
        if (snap != null) ApplyHealth(snap);
    }
}

// ============================ Network History (usage) ============================

public class TopAppVm
{
    public string Name { get; init; } = "";
    public string TotalText { get; init; } = "0 B";
    public double Share { get; init; }
    public Brush BarBrush { get; init; } = Brushes.Cyan;
}

public class UsageHistoryViewModel : PageVmBase
{
    private int _rangeDays = 7;
    public int RangeDays { get => _rangeDays; set { if (Set(ref _rangeDays, value)) Refresh(); } }

    private IList<BarPoint> _chartItems = new List<BarPoint>();
    public IList<BarPoint> ChartItems { get => _chartItems; set => Set(ref _chartItems, value); }

    public ObservableCollection<TopAppVm> TopApps { get; } = new();

    private string _totalText = "0 B", _downText = "0 B", _upText = "0 B";
    public string TotalText { get => _totalText; set => Set(ref _totalText, value); }
    public string DownText { get => _downText; set => Set(ref _downText, value); }
    public string UpText { get => _upText; set => Set(ref _upText, value); }

    private string _todayText = "0 B", _avgText = "0 B", _peakText = "—", _topAppText = "—";
    public string TodayText { get => _todayText; set => Set(ref _todayText, value); }
    public string AvgText { get => _avgText; set => Set(ref _avgText, value); }
    public string PeakText { get => _peakText; set => Set(ref _peakText, value); }
    public string TopAppText { get => _topAppText; set => Set(ref _topAppText, value); }

    private string _rangeLabel = "";
    public string RangeLabel { get => _rangeLabel; set => Set(ref _rangeLabel, value); }

    public RelayCommand SetRange7Command { get; }
    public RelayCommand SetRange30Command { get; }
    public RelayCommand SetRange90Command { get; }
    public RelayCommand RefreshCommand { get; }

    private System.Threading.Timer _timer;

    public UsageHistoryViewModel()
    {
        SetRange7Command = new RelayCommand(() => RangeDays = 7);
        SetRange30Command = new RelayCommand(() => RangeDays = 30);
        SetRange90Command = new RelayCommand(() => RangeDays = 90);
        RefreshCommand = new RelayCommand(() => Refresh());
        UsageService.Instance.Updated += () => Dispatch(() => { if (IsActive) Refresh(); });
        // Ticking only while visible: the VM lives for the whole session, so a timer
        // started here would keep rebuilding a hidden page's chart forever.
        _timer = new System.Threading.Timer(_ => Dispatch(Refresh), null,
            System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
    }

    public override void OnActivated()
    {
        Refresh();
        _timer.Change(TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(45));
    }

    public override void OnDeactivated()
    {
        try { _timer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite); } catch { }
    }

    public void Refresh()
    {
        try
        {
            var days = UsageService.Instance.GetRange(_rangeDays);
            long totalDown = days.Sum(d => d.Down);
            long totalUp = days.Sum(d => d.Up);
            DownText = Core.SpeedFormatConverter.FormatBytes(totalDown);
            UpText = Core.SpeedFormatConverter.FormatBytes(totalUp);
            TotalText = Core.SpeedFormatConverter.FormatBytes(totalDown + totalUp);
            TodayText = Core.SpeedFormatConverter.FormatBytes(UsageService.Instance.GetToday().Total);
            AvgText = Core.SpeedFormatConverter.FormatBytes(days.Count > 0 ? (totalDown + totalUp) / days.Count : 0);
            var peak = days.OrderByDescending(d => d.Total).FirstOrDefault();
            PeakText = peak != null && peak.Total > 0
                ? $"{DateTime.Parse(peak.Date):MM/dd} — {Core.SpeedFormatConverter.FormatBytes(peak.Total)}"
                : "—";
            RangeLabel = Lang[_rangeDays == 7 ? "nh_range_7" : _rangeDays == 30 ? "nh_range_30" : "nh_range_90"];

            // chart: last 14 (or all if fewer) columns
            var chartDays = days.TakeLast(14).ToList();
            ChartItems = chartDays.Select(d => new BarPoint
            {
                Label = DateTime.Parse(d.Date).ToString("dd/MM"),
                Value = d.Down,
                Value2 = d.Up,
            }).ToList();

            TopApps.Clear();
            var top = UsageService.Instance.GetTopApps(_rangeDays, 8);
            long grand = top.Sum(kv => kv.Value);
            string[] palette = { "#38BDF8", "#818CF8", "#34D399", "#F472B6", "#FBBF24", "#F87171", "#22D3EE", "#A78BFA" };
            int i = 0;
            foreach (var kv in top)
            {
                TopApps.Add(new TopAppVm
                {
                    Name = kv.Key,
                    TotalText = Core.SpeedFormatConverter.FormatBytes(kv.Value),
                    Share = grand > 0 ? (double)kv.Value / grand : 0,
                    BarBrush = MakeBrush(palette[i++ % palette.Length]),
                });
            }
            TopAppText = TopApps.FirstOrDefault()?.Name ?? "—";
        }
        catch (Exception ex) { App.LogCrash(ex); }
    }

    private static Brush MakeBrush(string hex)
    {
        try { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }
        catch { return Brushes.Cyan; }
    }

    protected override void OnLanguageChanged() => Refresh();
}

// ============================ Connection History (events) ============================

public class EventVm
{
    public DateTime Ts { get; init; }
    public string TimeText => Ts.ToString("yyyy-MM-dd   HH:mm:ss");
    public string Type { get; init; } = "";
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Glyph => Type switch
    {
        "dns_changed" => "\uE968",
        "dns_restored" => "\uE777",
        "app_blocked" => "\uE711",
        "app_unblocked" => "\uE73E",
        "limit_enabled" => "\uE952",
        "limit_disabled" => "\uE953",
        "network_reset" => "\uE777",
        "profile_applied" => "\uE736",
        "schedule_created" => "\uE823",
        "schedule_deleted" => "\uE74D",
        _ => "\uE7B6",
    };
    public Brush AccentBrush { get; init; } = Brushes.Cyan;

    public string LocalTitle => Type switch
    {
        "dns_changed" => L.Instance["ch_dns_changed"],
        "dns_restored" => L.Instance["ch_dns_restored"],
        "limit_enabled" => L.Instance["ch_limit_enabled"],
        "limit_disabled" => L.Instance["ch_limit_disabled"],
        "app_blocked" => L.Instance["ch_app_blocked"],
        "app_unblocked" => L.Instance["ch_app_unblocked"],
        "network_reset" => L.Instance["ch_network_reset"],
        "ip_renewed" => L.Instance["ch_ip_renewed"],
        "flush_dns" => L.Instance["ch_flush_dns"],
        "profile_applied" => L.Instance["ch_profile_applied"],
        "schedule_created" => L.Instance["ch_schedule_created"],
        "schedule_deleted" => L.Instance["ch_schedule_deleted"],
        _ => Title,
    };
}

public class ConnectionHistoryViewModel : PageVmBase
{
    public ObservableCollection<EventVm> Items { get; } = new();

    private string _filter = "all";
    public string Filter
    {
        get => _filter;
        set { if (value != null && Set(ref _filter, value)) Reload(); }
    }

    private static readonly string[] FilterKeys =
        { "all", "dns_changed", "dns_restored", "limit_enabled", "app_blocked", "profile_applied" };

    /// <summary>Localized filter choices (labels refresh when the language changes).</summary>
    public List<ChoiceVm> FilterOptions { get; } = new();

    public RelayCommand ClearCommand { get; }
    public RelayCommand RefreshCommand { get; }

    public ConnectionHistoryViewModel()
    {
        ClearCommand = new RelayCommand(() =>
        {
            HistoryService.Clear();
            Reload();
        });
        RefreshCommand = new RelayCommand(() => Reload());
        HistoryService.Changed += () => Dispatch(() => { if (IsActive) Reload(); });

        foreach (var k in FilterKeys)
            FilterOptions.Add(new ChoiceVm(k, k == "all" ? Lang["all"] : Lang["ch_" + k]));
    }

    public override void OnActivated() => Reload();

    private void Reload()
    {
        Items.Clear();
        foreach (var e in HistoryService.GetAll())
        {
            if (_filter != "all" && e.Type != _filter) continue;
            Items.Add(new EventVm
            {
                Ts = e.Ts,
                Type = e.Type,
                Title = e.Title,
                Detail = e.Detail,
                AccentBrush = e.Type switch
                {
                    "app_blocked" => BrushOf("#F87171"),
                    "dns_changed" => BrushOf("#38BDF8"),
                    "dns_restored" => BrushOf("#34D399"),
                    "limit_enabled" => BrushOf("#FBBF24"),
                    "profile_applied" => BrushOf("#818CF8"),
                    _ => BrushOf("#93A1BC"),
                },
            });
        }
    }

    private static Brush BrushOf(string hex)
    {
        try { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }
        catch { return Brushes.Gray; }
    }

    protected override void OnLanguageChanged()
    {
        foreach (var o in FilterOptions)
            o.Label = o.Key == "all" ? Lang["all"] : Lang["ch_" + o.Key];
        Reload();
    }
}
