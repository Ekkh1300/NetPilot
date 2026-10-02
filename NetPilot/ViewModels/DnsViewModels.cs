using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using NetPilot.Controls;
using NetPilot.Core;
using NetPilot.Models;
using NetPilot.Services;
using L = NetPilot.Lang.Lang;

namespace NetPilot.ViewModels;

// ============================ DNS Manager ============================

public class DnsEntryVm : ObservableObject
{
    public DnsEntry Entry { get; }
    public string Name => Entry.Name;
    public string Servers => Entry.ServersSummary;
    public string Description => Entry.Description;
    public string Glyph => Entry.Glyph;
    public Brush Color { get; }
    public bool IsCustom => Entry.IsCustom;

    private bool _isFavorite;
    public bool IsFavorite { get => _isFavorite; set => Set(ref _isFavorite, value); }

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    private string _currentTag = "";
    public string CurrentTag { get => _currentTag; set => Set(ref _currentTag, value); }
    public bool IsCurrent => !string.IsNullOrEmpty(CurrentTag);

    public DnsEntryVm(DnsEntry entry)
    {
        Entry = entry;
        IsFavorite = entry.IsFavorite;
        try { Color = new SolidColorBrush((Color)ColorConverter.ConvertFromString(entry.Color)); }
        catch { Color = Brushes.Cyan; }
        Color.Freeze();
    }
}

public class AdapterDnsVm
{
    public int IfIndex { get; init; }
    public string Label { get; init; } = "";
    public string Summary { get; init; } = "";
}

public class DnsManagerViewModel : PageVmBase
{
    public ObservableCollection<DnsEntryVm> Entries { get; } = new();
    public ObservableCollection<DnsEntryVm> Filtered { get; } = new();
    public ObservableCollection<AdapterDnsVm> Adapters { get; } = new();

    private string _search = "";
    public string Search { get => _search; set { if (Set(ref _search, value)) ApplyFilter(); } }

    private bool _favOnly;
    public bool FavOnly { get => _favOnly; set { if (Set(ref _favOnly, value)) ApplyFilter(); } }

    private AdapterDnsVm _selectedAdapter;
    public AdapterDnsVm SelectedAdapter { get => _selectedAdapter; set => Set(ref _selectedAdapter, value); }

    private DnsEntryVm _selectedEntry;
    /// <summary>The DNS row highlighted in the list (drives Apply).</summary>
    public DnsEntryVm SelectedEntry { get => _selectedEntry; set => Set(ref _selectedEntry, value); }

    private string _currentDnsSummary = "…";
    public string CurrentDnsSummary { get => _currentDnsSummary; set => Set(ref _currentDnsSummary, value); }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set => Set(ref _isBusy, value); }

    private string _statusMessage = "";
    public string StatusMessage { get => _statusMessage; set => Set(ref _statusMessage, value); }

    // custom form
    private bool _showCustomForm;
    public bool ShowCustomForm { get => _showCustomForm; set => Set(ref _showCustomForm, value); }
    private string _customName = "";
    public string CustomName { get => _customName; set => Set(ref _customName, value); }
    private string _customPrimary = "";
    public string CustomPrimary { get => _customPrimary; set => Set(ref _customPrimary, value); }
    private string _customSecondary = "";
    public string CustomSecondary { get => _customSecondary; set => Set(ref _customSecondary, value); }

    public RelayCommand ToggleFavoriteCommand { get; }
    public RelayCommand SelectCommand { get; }
    public RelayCommand AddCustomCommand { get; }
    public RelayCommand ShowCustomCommand { get; }
    public AsyncRelayCommand ApplyCommand { get; }
    public AsyncRelayCommand RestoreCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public AsyncRelayCommand FlushCommand { get; }

    public DnsManagerViewModel()
    {
        ToggleFavoriteCommand = new RelayCommand(p =>
        {
            if (p is DnsEntryVm vm)
            {
                DnsService.ToggleFavorite(vm.Entry);
                vm.IsFavorite = vm.Entry.IsFavorite;
            }
        });
        SelectCommand = new RelayCommand(p =>
        {
            if (p is not DnsEntryVm vm) return;
            foreach (var e in Filtered) e.IsSelected = ReferenceEquals(e, vm);
        });
        AddCustomCommand = new RelayCommand(async () => await AddCustomAsync());
        ShowCustomCommand = new RelayCommand(() => { ShowCustomForm = !ShowCustomForm; StatusMessage = ""; });
        ApplyCommand = new AsyncRelayCommand(async () =>
        {
            var sel = SelectedEntry ?? Filtered.FirstOrDefault(f => f.IsSelected);
            if (sel == null) { StatusMessage = Lang["dns_select_hint"]; return; }
            await ApplyAsync(sel.Entry, false);
        });
        RestoreCommand = new AsyncRelayCommand(async () => await RestoreAsync());
        RefreshCommand = new RelayCommand(() => _ = LoadAsync());
        FlushCommand = new AsyncRelayCommand(async () =>
        {
            await Sys.FlushDnsCacheAsync();
            StatusMessage = Lang["dns_flushed"];
        });

        ReloadEntries();
        _ = LoadAsync();
    }

    private void ReloadEntries()
    {
        Entries.Clear();
        foreach (var e in DnsService.GetAll())
            Entries.Add(new DnsEntryVm(e));
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var q = _search.Trim();
        Filtered.Clear();
        foreach (var e in Entries)
        {
            if (_favOnly && !e.IsFavorite) continue;
            if (q.Length > 0 &&
                !e.Name.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !e.Servers.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
            Filtered.Add(e);
        }
        Raise(nameof(NoEntries));
    }

    /// <summary>True when the (filtered) list has nothing to show.</summary>
    public bool NoEntries => Filtered.Count == 0;

    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var states = await DnsService.GetCurrentAsync();
            Dispatch(() =>
            {
                Adapters.Clear();
                foreach (var s in states)
                    Adapters.Add(new AdapterDnsVm
                    {
                        IfIndex = s.IfIndex,
                        Label = $"{s.AdapterName} (#{s.IfIndex})",
                        Summary = s.Summary,
                    });
                SelectedAdapter ??= Adapters.FirstOrDefault();
                CurrentDnsSummary = states.Count == 0 ? "—"
                    : string.Join("   |   ", states.Select(s => $"{s.AdapterName}: {s.Summary}"));

                foreach (var vm in Filtered) vm.CurrentTag = "";
                foreach (var vm in Entries)
                {
                    var match = states.FirstOrDefault(s => s.V4.Count > 0 && vm.Entry.MatchesServers(s.V4));
                    vm.CurrentTag = match != null ? (L.IsFa ? "فعلی" : "current") : "";
                }
            });
        }
        finally { IsBusy = false; }
    }

    private async Task AddCustomAsync()
    {
        if (string.IsNullOrWhiteSpace(CustomName)) { StatusMessage = Lang["dns_name_required"]; return; }
        if (Uri.CheckHostName(CustomPrimary?.Trim() ?? "") != UriHostNameType.IPv4)
        { StatusMessage = Lang["dns_invalid_ip"]; return; }

        var entry = new DnsEntry
        {
            Name = CustomName.Trim(),
            Description = "Custom",
            IsCustom = true,
            Color = "#34D399",
            Glyph = "\uE968",
            ServersV4 = new List<string> { CustomPrimary.Trim() },
        };
        if (Uri.CheckHostName(CustomSecondary?.Trim() ?? "") == UriHostNameType.IPv4)
            entry.ServersV4.Add(CustomSecondary.Trim());

        var custom = DnsService.GetCustom();
        custom.Add(entry);
        DnsService.SaveCustom(custom);

        CustomName = CustomPrimary = CustomSecondary = "";
        ShowCustomForm = false;
        ReloadEntries();
        StatusMessage = Lang["done"];
    }

    public async Task ApplyAsync(DnsEntry entry, bool fromTray)
    {
        IsBusy = true;
        try
        {
            int ifIndex = SelectedAdapter?.IfIndex
                ?? (await DnsService.GetCurrentAsync()).FirstOrDefault()?.IfIndex ?? 0;
            var (ok, msg) = await DnsService.ApplyAsync(entry, ifIndex);
            StatusMessage = ok ? $"{Lang["dns_applied"]}: {msg}" : msg;
            await LoadAsync();
            RequestStatusRefresh();
            TrayService.Instance.RefreshStatusAsync().ConfigureAwait(false);
        }
        finally { IsBusy = false; }
    }

    public async Task RestoreAsync()
    {
        IsBusy = true;
        try
        {
            var (ok, msg) = await DnsService.RestoreAsync();
            StatusMessage = ok ? Lang["dns_restore_done"] : Lang["dns_no_backup"];
            await LoadAsync();
            RequestStatusRefresh();
        }
        finally { IsBusy = false; }
    }

    private static void RequestStatusRefresh() => MainViewModel.RequestStatusRefresh();
}

// ============================ DNS Benchmark ============================

public class BenchResultVm : ObservableObject
{
    public DnsBenchmarkResult Result { get; }
    public string Name => Result.Entry.Name;
    public string Glyph => Result.Entry.Glyph;
    public Brush Color { get; }
    public double AvgMs => Result.AvgMs;
    public double MinMs => Result.MinMs;
    public double MaxMs => Result.MaxMs;
    public double Loss => Result.LossPercent;
    public double Score => Result.Score;
    public int Rank => Result.Rank;
    public bool IsBest => Result.Rank == 1 && !Result.Failed;
    public string AvgText => Result.Failed ? L.Instance["bm_dns_failed"] : $"{Result.AvgMs:0} ms";
    public string LossText => $"{Result.LossPercent:0}%";
    public string PingText => Result.PingOk ? $"{Result.PingAvgMs:0} ms" : "—";
    public string RankText => Result.Failed ? "—" : Result.Rank.ToString();

    public void RefreshLang() => Raise(nameof(AvgText));

    public BenchResultVm(DnsBenchmarkResult r)
    {
        Result = r;
        try { Color = new SolidColorBrush((Color)ColorConverter.ConvertFromString(r.Entry.Color)); }
        catch { Color = Brushes.Cyan; }
        Color.Freeze();
    }
}

public class BenchmarkViewModel : PageVmBase
{
    public ObservableCollection<BenchResultVm> Results { get; } = new();
    public ObservableCollection<DnsEntryVm> Targets { get; } = new();

    private bool _isRunning;
    public bool IsRunning { get => _isRunning; set { if (Set(ref _isRunning, value)) Raise(nameof(NotRunning)); } }
    public bool NotRunning => !_isRunning;

    private double _progress;
    public double Progress { get => _progress; set => Set(ref _progress, value); }

    private int _rounds = 4;
    public int Rounds { get => _rounds; set => Set(ref _rounds, value); }

    private BenchResultVm _selectedResult;
    public BenchResultVm SelectedResult { get => _selectedResult; set => Set(ref _selectedResult, value); }

    private string _statusMessage = "";
    public string StatusMessage { get => _statusMessage; set => Set(ref _statusMessage, value); }

    private string _bestText = "—";
    public string BestText { get => _bestText; set => Set(ref _bestText, value); }

    private IList<BarPoint> _chartItems = new List<BarPoint>();
    public IList<BarPoint> ChartItems { get => _chartItems; set => Set(ref _chartItems, value); }

    public bool HasResults => Results.Count > 0;
    public bool NoResults => Results.Count == 0;

    private void RaiseResultFlags()
    {
        Raise(nameof(HasResults));
        Raise(nameof(NoResults));
    }

    private CancellationTokenSource _cts;

    /// <summary>Rebuilds the comparison bars from the finished results.</summary>
    private void RebuildChart()
    {
        ChartItems = Results
            .Where(r => !r.Result.Failed)
            .OrderByDescending(r => r.Result.Score)
            .Select(r => new BarPoint
            {
                Label = r.Name,
                Value = r.Result.Score,
                ValueText = $"{r.Result.AvgMs:0} ms",
            }).ToList();
    }

    public List<double> RoundsOptions { get; } = new() { 2, 3, 4, 6, 8 };

    public AsyncRelayCommand RunCommand { get; }
    public RelayCommand StopCommand { get; }

    public BenchmarkViewModel()
    {
        RunCommand = new AsyncRelayCommand(async () => await RunAsync());
        StopCommand = new RelayCommand(() => _cts?.Cancel());

        BenchmarkService.ResultUpdated += r => Dispatch(() =>
        {
            var vm = Results.FirstOrDefault(x => x.Name == r.Entry.Name);
            if (vm == null)
            {
                vm = new BenchResultVm(r);
                Results.Add(vm);
            }
            RebuildChart();
        });
        BenchmarkService.Finished += () => Dispatch(() =>
        {
            var ordered = Results.OrderByDescending(x => x.Score).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                Results.Remove(ordered[i]);
                Results.Add(ordered[i]);
            }
            BestText = Results.FirstOrDefault()?.Name ?? "—";
            RebuildChart();
            RaiseResultFlags();
        });
    }

    public override void OnActivated()
    {
        if (Targets.Count == 0)
            foreach (var e in DnsService.GetAll())
                Targets.Add(new DnsEntryVm(e));
    }

    protected override void OnLanguageChanged()
    {
        // Result rows carry their own computed text (AvgText becomes "DNS failed" in the
        // active language), and rows in an ItemsSource never see the page's own raise.
        foreach (var r in Results) r.RefreshLang();
    }

    private async Task RunAsync()
    {
        _cts = new CancellationTokenSource();
        Results.Clear();
        RaiseResultFlags();
        Progress = 0;
        IsRunning = true;
        StatusMessage = Lang["bm_running"];
        try
        {
            var targets = Targets.Where(t => true).Select(t => t.Entry).ToList();
            var progress = new Progress<double>(p => Dispatch(() => Progress = p * 100));
            await BenchmarkService.RunAsync(targets, Rounds, progress, _cts.Token);
            StatusMessage = $"{Lang["bm_summary"]}: {Results.Count} — {Lang["bm_best"]}: {BestText}";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = Lang["bm_stop"];
        }
        finally { IsRunning = false; }
    }
}

// ============================ Smart DNS ============================

public class SmartDnsViewModel : PageVmBase
{
    private readonly DnsManagerViewModel _dnsVm;

    public ObservableCollection<BenchResultVm> Suggestions { get; } = new();

    private bool _isScanning;
    public bool IsScanning { get => _isScanning; set { if (Set(ref _isScanning, value)) Raise(nameof(NotScanning)); } }
    public bool NotScanning => !_isScanning;

    private double _progress;
    public double Progress { get => _progress; set => Set(ref _progress, value); }

    private string _currentDns = "…";
    public string CurrentDns { get => _currentDns; set => Set(ref _currentDns, value); }

    private string _recommendation = "";
    public string Recommendation { get => _recommendation; set => Set(ref _recommendation, value); }

    private BenchResultVm _selectedSuggestion;
    public BenchResultVm SelectedSuggestion { get => _selectedSuggestion; set => Set(ref _selectedSuggestion, value); }

    private string _statusMessage = "";
    public string StatusMessage { get => _statusMessage; set => Set(ref _statusMessage, value); }

    private int _fasterCount;
    public int FasterCount { get => _fasterCount; set => Set(ref _fasterCount, value); }

    private string _improvementText = "—";
    public string ImprovementText { get => _improvementText; set => Set(ref _improvementText, value); }

    public ObservableCollection<string> Steps { get; } = new();

    public bool HasSuggestions => Suggestions.Count > 0;

    private void RaiseSuggestionsFlag() => Raise(nameof(HasSuggestions));

    public AsyncRelayCommand ScanCommand { get; }
    public AsyncRelayCommand ApplyCommand { get; }

    public SmartDnsViewModel(DnsManagerViewModel dnsVm = null)
    {
        _dnsVm = dnsVm;
        ScanCommand = new AsyncRelayCommand(async () => await ScanAsync());
        ApplyCommand = new AsyncRelayCommand(async () =>
        {
            var sel = SelectedSuggestion ?? Suggestions.FirstOrDefault();
            if (sel == null) { StatusMessage = Lang["sd_no_recommend"]; return; }
            await ApplySuggestionAsync(sel);
        });
    }

    public override void OnActivated()
    {
        _ = RefreshCurrentAsync();
    }

    private async Task RefreshCurrentAsync()
    {
        CurrentDns = await DnsService.DescribeCurrentAsync();
    }

    private async Task ScanAsync()
    {
        IsScanning = true;
        Progress = 0;
        Suggestions.Clear();
        RaiseSuggestionsFlag();
        Steps.Clear();
        StatusMessage = Lang["sd_analyzing"];
        try
        {
            await RefreshCurrentAsync();
            Steps.Add($"{Lang["sd_current_dns"]}: {CurrentDns}");

            var entries = DnsService.GetAll();
            var progress = new Progress<double>(p => Dispatch(() => Progress = p * 100));
            var results = await BenchmarkService.RunAsync(entries, 3, progress, CancellationToken.None);

            Dispatch(() =>
            {
                foreach (var r in results.Where(r => !r.Failed).OrderByDescending(r => r.Score))
                    Suggestions.Add(new BenchResultVm(r));
                SelectedSuggestion = Suggestions.FirstOrDefault();
                RaiseSuggestionsFlag();
            });

            // compare against current DNS
            var currentMatch = DnsService.FindMatching(
                (await DnsService.GetCurrentAsync()).FirstOrDefault()?.V4 ?? new List<string>());
            double currentAvg = results.FirstOrDefault(r => currentMatch != null && r.Entry.Id == currentMatch.Id)?.AvgMs ?? -1;
            var best = results.Where(r => !r.Failed).OrderByDescending(r => r.Score).FirstOrDefault();

            if (best != null)
            {
                FasterCount = currentMatch == null ? Suggestions.Count
                    : results.Count(r => !r.Failed && currentAvg > 0 && r.AvgMs < currentAvg);
                Recommendation = $"{best.Entry.Name} — {best.AvgMs:0} ms";
                if (currentAvg > 0 && best.AvgMs > 0)
                    ImprovementText = $"{(currentAvg - best.AvgMs) / currentAvg * 100:0}%";
                Steps.Add($"{Lang["sd_recommendation"]}: {best.Entry.Name} ({best.AvgMs:0} ms)");
                Steps.Add($"{Lang["sd_faster_count"]}: {FasterCount}");
            }
            StatusMessage = Lang["sd_scan_done"];
        }
        finally { IsScanning = false; }
    }

    private async Task ApplySuggestionAsync(BenchResultVm sel)
    {
        var states = await DnsService.GetCurrentAsync();
        int ifIndex = states.FirstOrDefault()?.IfIndex ?? 0;
        var (ok, msg) = await DnsService.ApplyAsync(sel.Result.Entry, ifIndex);
        StatusMessage = ok ? $"{Lang["dns_applied"]}: {msg}" : msg;
        if (!ok) return;

        await RefreshCurrentAsync();
        Steps.Add($"{Lang["sd_retest"]}…");

        // re-test the newly applied DNS to prove the change
        var retest = await BenchmarkService.RunAsync(new[] { sel.Result.Entry }, 3, null, CancellationToken.None);
        var r = retest.FirstOrDefault();
        if (r != null)
            Steps.Add($"{Lang["bm_avg"]} ({sel.Name}): {r.AvgMs:0} ms  •  {Lang["bm_packet_loss"]}: {r.LossPercent:0}%");

        await HealthService.Instance.RefreshDnsProbeAsync();
        MainViewModel.RequestStatusRefresh();
        _dnsVm?.LoadAsync().ConfigureAwait(false);
    }
}
