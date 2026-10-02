using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media;
using NetPilot.Core;
using NetPilot.Models;
using NetPilot.Services;
using L = NetPilot.Lang.Lang;

namespace NetPilot.ViewModels;

// ============================ Net Limiter ============================

public class RuleVm : ObservableObject
{
    public LimitRule Rule { get; }
    public string Id => Rule.Id;
    public string Name => string.IsNullOrEmpty(Rule.AppName)
        ? System.IO.Path.GetFileNameWithoutExtension(Rule.AppPath ?? "")
        : Rule.AppName;
    public string Path => Rule.AppPath;
    public string ModeText => Rule.Mode switch
    {
        LimitMode.Block => L.Instance["nl_block"],
        LimitMode.Allow => L.Instance["nl_allow"],
        _ => L.Instance["nl_limit"],
    };
    public string DownText => Rule.Mode == LimitMode.Limit ? LimiterService.FormatRate(Rule.DownLimitBps) : "—";
    public string UpText => Rule.Mode == LimitMode.Limit ? LimiterService.FormatRate(Rule.UpLimitBps) : "—";
    public bool IsBlock => Rule.Mode == LimitMode.Block;
    public bool IsLimit => Rule.Mode == LimitMode.Limit;
    public string Glyph => Rule.Mode == LimitMode.Block ? "\uE711" : Rule.Mode == LimitMode.Limit ? "\uE952" : "\uE73E";
    public Brush AccentBrush { get; }

    public void RefreshLang()
    {
        Raise(nameof(ModeText));
        Raise(nameof(DownText));
        Raise(nameof(UpText));
    }

    public RuleVm(LimitRule r)
    {
        Rule = r;
        AccentBrush = r.Mode switch
        {
            LimitMode.Block => new SolidColorBrush(Color.FromRgb(248, 113, 113)),
            LimitMode.Limit => new SolidColorBrush(Color.FromRgb(251, 191, 36)),
            _ => new SolidColorBrush(Color.FromRgb(52, 211, 153)),
        };
        AccentBrush.Freeze();
    }
}

public class NetLimiterViewModel : PageVmBase
{
    public ObservableCollection<RuleVm> Rules { get; } = new();
    public ObservableCollection<AppOption> Apps { get; } = new();
    public static readonly string[] Presets = { "Unlimited", "128 KB/s", "256 KB/s", "500 KB/s", "1 MB/s", "2 MB/s", "5 MB/s", "10 MB/s", "Custom" };

    public IReadOnlyList<string> PresetOptions => Presets;

    private AppOption _selectedApp;
    public AppOption SelectedApp { get => _selectedApp; set => Set(ref _selectedApp, value); }

    private int _modeIndex = 1;
    public int ModeIndex { get => _modeIndex; set { if (Set(ref _modeIndex, value)) Raise(nameof(ModeDescription)); } }

    public string ModeDescription => ModeIndex switch
    {
        0 => Lang["nl_allow"],
        2 => Lang["nl_block"],
        _ => Lang["nl_limit"],
    };

    /// <summary>Allow / Limit / Block choices, localized.</summary>
    public List<ChoiceVm> ModeOptions { get; } = new();

    private string _selectedDownPreset = "1 MB/s";
    public string SelectedDownPreset { get => _selectedDownPreset; set { if (Set(ref _selectedDownPreset, value)) Raise(nameof(DownIsCustom)); } }
    private string _selectedUpPreset = "512 KB/s";
    public string SelectedUpPreset { get => _selectedUpPreset; set { if (Set(ref _selectedUpPreset, value)) Raise(nameof(UpIsCustom)); } }

    public bool DownIsCustom => SelectedDownPreset == "Custom";
    public bool UpIsCustom => SelectedUpPreset == "Custom";

    private string _customDown = "300 KB/s";
    public string CustomDown { get => _customDown; set => Set(ref _customDown, value); }
    private string _customUp = "100 KB/s";
    public string CustomUp { get => _customUp; set => Set(ref _customUp, value); }

    private string _statusMessage = "";
    public string StatusMessage { get => _statusMessage; set => Set(ref _statusMessage, value); }

    public AsyncRelayCommand AddCommand { get; }
    public AsyncRelayCommand RemoveCommand { get; }
    public RelayCommand BrowseCommand { get; }
    public RelayCommand RefreshAppsCommand { get; }

    public NetLimiterViewModel()
    {
        AddCommand = new AsyncRelayCommand(async () => await AddAsync());
        RemoveCommand = new AsyncRelayCommand(async p =>
        {
            if (p is not RuleVm vm) return;
            await LimiterService.Instance.RemoveAsync(vm.Id);
            HistoryService.Add(vm.Rule.Mode == LimitMode.Block ? "app_unblocked" : "limit_disabled",
                vm.Rule.Mode == LimitMode.Block ? "Application Unblocked" : "Limit Disabled", vm.Name);
            StatusMessage = Lang["nl_rule_removed"];
            ReloadRules();
        });
        BrowseCommand = new RelayCommand(() =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Applications (*.exe)|*.exe|All files (*.*)|*.*" };
            if (dlg.ShowDialog() == true)
                SelectedApp = new AppOption { Path = dlg.FileName, Name = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName) };
        });
        RefreshAppsCommand = new RelayCommand(() => ReloadApps());

        ModeOptions.Add(new ChoiceVm("0", Lang["nl_allow"]));
        ModeOptions.Add(new ChoiceVm("1", Lang["nl_limit"]));
        ModeOptions.Add(new ChoiceVm("2", Lang["nl_block"]));

        LimiterService.Instance.Changed += () => Dispatch(() => { if (IsActive) ReloadRules(); });
        ReloadRules();
        ReloadApps();
    }

    public override void OnActivated()
    {
        // Changes while hidden were skipped, so reload both lists on entry.
        ReloadRules();
        ReloadApps();
    }

    protected override void OnLanguageChanged()
    {
        if (ModeOptions.Count >= 3)
        {
            ModeOptions[0].Label = Lang["nl_allow"];
            ModeOptions[1].Label = Lang["nl_limit"];
            ModeOptions[2].Label = Lang["nl_block"];
        }
        Raise(nameof(ModeDescription));
        // Rows live in an ItemsSource, so their own objects must be told - a raise on
        // this page never reaches them (RuleVm reads the language in ModeText).
        foreach (var r in Rules) r.RefreshLang();
    }

    private void ReloadRules()
    {
        Rules.Clear();
        foreach (var r in LimiterService.Instance.GetRules())
            Rules.Add(new RuleVm(r));
    }

    private void ReloadApps()
    {
        string selectedPath = SelectedApp?.Path;
        Apps.Clear();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in ProcessNetworkService.Instance.GetSnapshot())
        {
            if (string.IsNullOrEmpty(a.Path)) continue;
            if (!seen.Add(a.Path)) continue;
            Apps.Add(new AppOption { Path = a.Path, Name = string.IsNullOrEmpty(a.DisplayName) ? a.ProcessName : a.DisplayName });
        }
        foreach (var r in LimiterService.Instance.GetRules())
        {
            if (string.IsNullOrEmpty(r.AppPath) || !seen.Add(r.AppPath)) continue;
            Apps.Add(new AppOption { Path = r.AppPath, Name = string.IsNullOrEmpty(r.AppName) ? System.IO.Path.GetFileNameWithoutExtension(r.AppPath) : r.AppName });
        }
        SelectedApp = Apps.FirstOrDefault(a => a.Path == selectedPath) ?? SelectedApp;
    }

    private async System.Threading.Tasks.Task AddAsync()
    {
        if (SelectedApp == null || string.IsNullOrWhiteSpace(SelectedApp.Path))
        {
            StatusMessage = Lang["nl_select_app"];
            return;
        }

        var rule = new LimitRule
        {
            AppPath = SelectedApp.Path,
            AppName = SelectedApp.Name,
            Mode = ModeIndex switch { 0 => LimitMode.Allow, 2 => LimitMode.Block, _ => LimitMode.Limit },
            Enabled = true,
        };

        if (rule.Mode == LimitMode.Limit)
        {
            rule.DownLimitBps = RateFrom(SelectedDownPreset, CustomDown);
            rule.UpLimitBps = RateFrom(SelectedUpPreset, CustomUp);
            if (rule.DownLimitBps < 0 || rule.UpLimitBps < 0)
            {
                StatusMessage = Lang["nl_invalid_rate"];
                return;
            }
        }

        await LimiterService.Instance.ApplyAsync(rule);

        string histType = rule.Mode == LimitMode.Block ? "app_blocked" : "limit_enabled";
        string histTitle = rule.Mode == LimitMode.Block ? "Application Blocked" : "Limit Enabled";
        string detail = rule.Mode == LimitMode.Limit
            ? $"{rule.AppName} ↓ {LimiterService.FormatRate(rule.DownLimitBps)} ↑ {LimiterService.FormatRate(rule.UpLimitBps)}"
            : rule.AppName;
        HistoryService.Add(histType, histTitle, detail);

        StatusMessage = rule.Mode == LimitMode.Block ? Lang["nl_app_blocked"]
            : rule.Mode == LimitMode.Limit ? Lang["nl_limit_enabled"]
            : Lang["nl_app_unblocked"];
        ReloadRules();
    }

    private static long RateFrom(string preset, string customText)
    {
        if (preset == "Unlimited") return 0;
        string text = preset == "Custom" ? customText : preset;
        return LimiterService.ParseRate(text);
    }
}

public class AppOption
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public string Display => $"{Name}  —  {Path}";
}

// ============================ Scheduled Limits ============================

public class SchedVm : ObservableObject
{
    public ScheduleRule Rule { get; }
    public string Id => Rule.Id;
    public string AppName => string.IsNullOrEmpty(Rule.AppName) ? System.IO.Path.GetFileNameWithoutExtension(Rule.AppPath) : Rule.AppName;
    public string AppPath => Rule.AppPath;
    public string RangeText => $"{Rule.Start:hh\\:mm} → {Rule.End:hh\\:mm}";
    public string LimitText => Rule.BlockInstead
        ? (L.IsFa ? "مسدود" : "Blocked")
        : $"↓ {LimiterService.FormatRate(Rule.DownLimitBps)}  ↑ {LimiterService.FormatRate(Rule.UpLimitBps)}";
    public bool CrossesMidnight => Rule.Start > Rule.End;
    public bool ActiveNow { get; set; }

    private bool _enabled;
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    public Brush DotBrush { get; }

    public SchedVm(ScheduleRule rule)
    {
        Rule = rule;
        _enabled = rule.Enabled;
        ActiveNow = rule.ActiveNow;
        DotBrush = new SolidColorBrush(rule.Enabled
            ? (rule.ActiveNow ? Color.FromRgb(52, 211, 153) : Color.FromRgb(56, 189, 248))
            : Color.FromRgb(100, 110, 130));
        DotBrush.Freeze();
    }
}

public class SchedulesViewModel : PageVmBase
{
    public ObservableCollection<SchedVm> Items { get; } = new();
    public ObservableCollection<AppOption> Apps { get; } = new();
    public IReadOnlyList<string> PresetOptions => NetLimiterViewModel.Presets;

    private AppOption _selectedApp;
    public AppOption SelectedApp { get => _selectedApp; set => Set(ref _selectedApp, value); }

    private string _startText = "00:00";
    public string StartText { get => _startText; set => Set(ref _startText, value); }
    private string _endText = "08:00";
    public string EndText { get => _endText; set => Set(ref _endText, value); }
    private string _limitPreset = "1 MB/s";
    public string LimitPreset { get => _limitPreset; set => Set(ref _limitPreset, value); }
    private bool _blockInstead;
    public bool BlockInstead { get => _blockInstead; set => Set(ref _blockInstead, value); }

    private string _statusMessage = "";
    public string StatusMessage { get => _statusMessage; set => Set(ref _statusMessage, value); }

    public RelayCommand AddCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand ToggleCommand { get; }
    public RelayCommand RefreshCommand { get; }

    public SchedulesViewModel()
    {
        AddCommand = new RelayCommand(Add);
        DeleteCommand = new RelayCommand(p =>
        {
            if (p is not SchedVm vm) return;
            ScheduleService.Instance.Delete(vm.Id);
            HistoryService.Add("schedule_deleted", "Schedule Deleted", $"{vm.AppName} {vm.RangeText}");
            StatusMessage = Lang["sc_schedule_deleted"];
            Reload();
        });
        ToggleCommand = new RelayCommand(p =>
        {
            if (p is not SchedVm vm) return;
            vm.Rule.Enabled = vm.Enabled;
            ScheduleService.Instance.Save(vm.Rule);
            HistoryService.Add(vm.Enabled ? "limit_enabled" : "limit_disabled",
                vm.Enabled ? "Limit Enabled" : "Limit Disabled", $"{vm.AppName} {vm.RangeText}");
            StatusMessage = Lang["sc_schedule_toggled"];
            Reload();
        });
        RefreshCommand = new RelayCommand(() => { Reload(); ReloadApps(); });

        ScheduleService.Instance.Changed += () => Dispatch(() => { if (IsActive) Reload(); });
        Reload();
        ReloadApps();
    }

    public override void OnActivated()
    {
        // Changes while hidden were skipped, so reload both lists on entry.
        Reload();
        ReloadApps();
    }

    private void Reload()
    {
        Items.Clear();
        foreach (var r in ScheduleService.Instance.GetAll())
            Items.Add(new SchedVm(r));
    }

    private void ReloadApps()
    {
        string sel = SelectedApp?.Path;
        Apps.Clear();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in ProcessNetworkService.Instance.GetSnapshot())
        {
            if (string.IsNullOrEmpty(a.Path) || !seen.Add(a.Path)) continue;
            Apps.Add(new AppOption { Path = a.Path, Name = a.DisplayName ?? a.ProcessName });
        }
        foreach (var r in ScheduleService.Instance.GetAll())
        {
            if (string.IsNullOrEmpty(r.AppPath) || !seen.Add(r.AppPath)) continue;
            Apps.Add(new AppOption { Path = r.AppPath, Name = r.AppName });
        }
        SelectedApp = Apps.FirstOrDefault(a => a.Path == sel) ?? SelectedApp;
    }

    private void Add()
    {
        if (SelectedApp == null || string.IsNullOrWhiteSpace(SelectedApp.Path))
        {
            StatusMessage = Lang["nl_select_app"];
            return;
        }
        if (!TimeSpan.TryParseExact(StartText?.Trim(), @"hh\:mm", null, out var start) ||
            !TimeSpan.TryParseExact(EndText?.Trim(), @"hh\:mm", null, out var end))
        {
            StatusMessage = Lang["sc_example_hint"];
            return;
        }

        long limit = 0;
        if (!BlockInstead)
        {
            // "Unlimited" and "Custom" come straight from the shared preset list. Fed to
            // ParseRate they are not rates at all, so the schedule silently refused to save
            // with "invalid rate" - a dead option on a page that offers it. Reuse the mapping
            // the Net Limiter page already applies; Custom has no box here, so it is "unlimited".
            limit = LimitPreset == "Unlimited" || LimitPreset == "Custom"
                ? 0
                : LimiterService.ParseRate(LimitPreset);
            if (limit < 0) { StatusMessage = Lang["nl_invalid_rate"]; return; }
        }

        var rule = new ScheduleRule
        {
            AppPath = SelectedApp.Path,
            AppName = SelectedApp.Name,
            Start = start,
            End = end,
            DownLimitBps = limit,
            UpLimitBps = limit,
            BlockInstead = BlockInstead,
            Enabled = true,
        };
        ScheduleService.Instance.Save(rule);
        HistoryService.Add("schedule_created", "Schedule Created",
            $"{rule.AppName}: {start:hh\\:mm} → {end:hh\\:mm}, {LimiterService.FormatRate(limit)}");
        StatusMessage = Lang["sc_schedule_added"];
        Reload();
    }
}
