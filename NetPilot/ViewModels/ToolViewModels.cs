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

// ============================ Network Tools ============================

public class ToolsViewModel : PageVmBase
{
    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set { if (Set(ref _isBusy, value)) Raise(nameof(NotBusy)); } }
    public bool NotBusy => !_isBusy;

    private string _output = "";
    public string Output { get => _output; set => Set(ref _output, value); }

    private string _pingTarget = "snapp.ir";
    public string PingTarget { get => _pingTarget; set => Set(ref _pingTarget, value); }

    private string _traceTarget = "google.com";
    public string TraceTarget { get => _traceTarget; set => Set(ref _traceTarget, value); }

    private string _lookupDomain = "google.com";
    public string LookupDomain { get => _lookupDomain; set => Set(ref _lookupDomain, value); }

    private string _lookupServer = "1.1.1.1";
    public string LookupServer { get => _lookupServer; set => Set(ref _lookupServer, value); }

    private string _recordType = "A";
    public string RecordType { get => _recordType; set => Set(ref _recordType, value); }

    public List<string> RecordTypes { get; } = new() { "A", "AAAA", "CNAME", "MX", "NS", "TXT", "PTR" };
    public List<string> DnsServers { get; } = new();

    public AsyncRelayCommand FlushCommand { get; }
    public AsyncRelayCommand RenewCommand { get; }
    public AsyncRelayCommand ResetCommand { get; }
    public AsyncRelayCommand PingCommand { get; }
    public AsyncRelayCommand TraceCommand { get; }
    public AsyncRelayCommand LookupCommand { get; }
    public RelayCommand ClearCommand { get; }

    public ToolsViewModel()
    {
        foreach (var e in DnsService.Presets)
            foreach (var s in e.ServersV4)
                DnsServers.Add(s);
        _lookupServer = DnsServers.FirstOrDefault() ?? "1.1.1.1";

        FlushCommand = new AsyncRelayCommand(async () => await Run(ToolsService.FlushDnsAsync()));
        RenewCommand = new AsyncRelayCommand(async () => await Run(ToolsService.RenewIpAsync()));
        ResetCommand = new AsyncRelayCommand(async () =>
        {
            var res = System.Windows.MessageBox.Show(
                Lang["nt_confirm_reset"], "NetPilot",
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
            if (res != System.Windows.MessageBoxResult.Yes) return;
            await Run(ToolsService.ResetNetworkAsync());
        });
        PingCommand = new AsyncRelayCommand(async () =>
            await Run(ToolsService.PingAsync(PingTarget?.Trim() ?? "snapp.ir")));
        TraceCommand = new AsyncRelayCommand(async () =>
            await Run(ToolsService.TracerouteAsync(TraceTarget?.Trim() ?? "google.com")));
        LookupCommand = new AsyncRelayCommand(async () =>
            await Run(ToolsService.NsLookupAsync(LookupDomain?.Trim() ?? "google.com", LookupServer, RecordType)));
        ClearCommand = new RelayCommand(() => Output = "");
    }

    private async System.Threading.Tasks.Task Run(System.Threading.Tasks.Task<ToolResult> task)
    {
        IsBusy = true;
        try
        {
            var r = await task;
            string header = $"── {r.Title}  ({r.RanAt:HH:mm:ss}) ──";
            Output = string.IsNullOrEmpty(Output) ? $"{header}\n{r.Output}" : $"{Output}\n\n{header}\n{r.Output}";
        }
        catch (Exception ex)
        {
            Output += $"\nERROR: {ex.Message}";
        }
        finally { IsBusy = false; }
    }
}

// ============================ Adapters ============================

public class AdapterVm : ObservableObject
{
    public AdapterInfo Info { get; init; }
    public string Name => Info.Name;
    public string KindLabel => Info.Kind switch
    {
        AdapterKind.Ethernet => L.Instance["ad_ethernet"],
        AdapterKind.Wifi => L.Instance["ad_wifi"],
        AdapterKind.Virtual => L.Instance["ad_virtual"],
        AdapterKind.Vpn => L.Instance["ad_vpn"],
        _ => Info.Description,
    };
    public string StatusText => Info.IsUp ? L.Instance["ad_up"] : L.Instance["ad_down"];
    public Brush StatusBrush => Info.IsUp
        ? Freeze(Color.FromRgb(52, 211, 153))
        : Freeze(Color.FromRgb(248, 113, 113));
    public string Ipv4 => string.IsNullOrEmpty(Info.Ipv4) ? "—" : Info.Ipv4;
    public string Gateway => string.IsNullOrEmpty(Info.Gateway) ? "—" : Info.Gateway;
    public string Dns => Info.DnsServers.Count == 0 ? "—" : string.Join(", ", Info.DnsServers);
    public string LinkSpeed => Info.LinkSpeedText;
    public string Mac => Info.Mac;
    public string DhcpLabel => Info.IsDhcp ? L.Instance["ad_dhcp"] : L.Instance["ad_static"];
    public string Glyph => Info.KindGlyph;

    /// <summary>Re-reads the localized labels (kind / up-down / dhcp) after a language switch.</summary>
    public void RefreshLang()
    {
        Raise(nameof(KindLabel));
        Raise(nameof(StatusText));
        Raise(nameof(DhcpLabel));
    }

    private static Brush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

public class AdaptersViewModel : PageVmBase
{
    public ObservableCollection<AdapterVm> Items { get; } = new();

    private AdapterVm _selected;
    public AdapterVm Selected { get => _selected; set => Set(ref _selected, value); }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set => Set(ref _isBusy, value); }

    private string _statusMessage = "";
    public string StatusMessage { get => _statusMessage; set => Set(ref _statusMessage, value); }

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand EnableCommand { get; }
    public AsyncRelayCommand DisableCommand { get; }

    public AdaptersViewModel()
    {
        RefreshCommand = new AsyncRelayCommand(async () => await LoadAsync());
        EnableCommand = new AsyncRelayCommand(async () => await SetEnabledAsync(true));
        DisableCommand = new AsyncRelayCommand(async () => await SetEnabledAsync(false));
        _ = LoadAsync();
    }

    public override void OnActivated() => _ = LoadAsync();

    protected override void OnLanguageChanged()
    {
        // Adapter rows are plain item objects with computed labels, so they are refreshed
        // individually - the raise on this page VM never reaches an ItemsSource element.
        foreach (var i in Items) i.RefreshLang();
    }

    private async System.Threading.Tasks.Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var adapters = await AdapterService.GetAdaptersAsync();
            Dispatch(() =>
            {
                string sel = Selected?.Name;
                Items.Clear();
                foreach (var a in adapters)
                    Items.Add(new AdapterVm { Info = a });
                Selected = Items.FirstOrDefault(i => i.Name == sel) ?? Items.FirstOrDefault();
            });
        }
        finally { IsBusy = false; }
    }

    private async System.Threading.Tasks.Task SetEnabledAsync(bool enable)
    {
        if (Selected == null) { StatusMessage = Lang["ad_no_adapters"]; return; }
        IsBusy = true;
        try
        {
            var (ok, msg) = await AdapterService.SetEnabledAsync(Selected.Name, enable);
            StatusMessage = ok
                ? (enable ? Lang["ad_enabled_msg"] : Lang["ad_disabled_msg"])
                : msg;
            await LoadAsync();
            MainViewModel.RequestStatusRefresh();
        }
        finally { IsBusy = false; }
    }
}

// ============================ Profiles ============================

public class ProfileVm : ObservableObject
{
    public Profile Profile { get; }
    public string Name => Profile.Name;
    public string Description => Profile.Description;
    public string Glyph => Profile.Glyph;
    public Brush ColorBrush { get; }
    public string DnsName
    {
        get
        {
            if (string.IsNullOrEmpty(Profile.DnsEntryId)) return L.Instance["pr_keep_current_dns"];
            var d = DnsService.GetAll().FirstOrDefault(x => x.Id == Profile.DnsEntryId);
            return d?.Name ?? "—";
        }
    }
    public string LimitsSummary
    {
        get
        {
            if (Profile.Rules.Count == 0) return L.Instance["nl_no_rules"];
            int blocked = Profile.Rules.Count(r => r.Mode == LimitMode.Block);
            int limited = Profile.Rules.Count(r => r.Mode == LimitMode.Limit);
            return $"{limited} {L.Instance["nl_limit"]} • {blocked} {L.Instance["nl_block"]}";
        }
    }
    public bool IsActive => SettingsService.Current.ActiveProfileId == Profile.Id;

    public ProfileVm(Profile p)
    {
        Profile = p;
        try { ColorBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(p.Color)); }
        catch { ColorBrush = Brushes.Purple; }
        ColorBrush.Freeze();
    }

    private static NetPilot.Lang.Lang X => NetPilot.Lang.Lang.Instance;
}

public class ProfilesViewModel : PageVmBase
{
    public ObservableCollection<ProfileVm> Items { get; } = new();

    public List<DnsEntry> DnsOptions { get; } = new();

    private ProfileVm _selected;
    public ProfileVm Selected { get => _selected; set => Set(ref _selected, value); }

    private string _newName = "";
    public string NewName { get => _newName; set => Set(ref _newName, value); }

    private string _statusMessage = "";
    public string StatusMessage { get => _statusMessage; set => Set(ref _statusMessage, value); }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set => Set(ref _isBusy, value); }

    public AsyncRelayCommand ApplyCommand { get; }
    public AsyncRelayCommand CreateCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand DuplicateCommand { get; }

    public ProfilesViewModel()
    {
        ApplyCommand = new AsyncRelayCommand(async p =>
        {
            var vm = p as ProfileVm ?? Selected;
            if (vm == null) return;
            IsBusy = true;
            try
            {
                await ProfileService.ApplyAsync(vm.Profile);
                StatusMessage = $"{Lang["pr_applied"]}: {vm.Name}";
                Reload();
                MainViewModel.RequestStatusRefresh();
            }
            finally { IsBusy = false; }
        });
        CreateCommand = new AsyncRelayCommand(async () =>
        {
            string name = string.IsNullOrWhiteSpace(NewName) ? $"Profile {Items.Count + 1}" : NewName.Trim();
            IsBusy = true;
            try
            {
                var p = await ProfileService.CreateFromCurrentAsync(name);
                ProfileService.Save(p);
                NewName = "";
                StatusMessage = Lang["done"];
                Reload();
            }
            finally { IsBusy = false; }
        });
        DeleteCommand = new RelayCommand(p =>
        {
            var vm = p as ProfileVm ?? Selected;
            if (vm == null || vm.Profile.IsDefault) return;
            ProfileService.Delete(vm.Profile.Id);
            StatusMessage = Lang["pr_deleted"];
            Reload();
        });
        DuplicateCommand = new RelayCommand(p =>
        {
            var vm = p as ProfileVm ?? Selected;
            if (vm == null) return;
            var copy = new Profile
            {
                Name = vm.Name + " (copy)",
                Description = vm.Description,
                DnsEntryId = vm.Profile.DnsEntryId,
                Rules = vm.Profile.Rules.Select(r => new LimitRule
                {
                    AppPath = r.AppPath, AppName = r.AppName, Mode = r.Mode,
                    DownLimitBps = r.DownLimitBps, UpLimitBps = r.UpLimitBps, Enabled = r.Enabled,
                }).ToList(),
                Glyph = vm.Glyph,
                Color = vm.Profile.Color,
            };
            ProfileService.Save(copy);
            StatusMessage = Lang["pr_duplicated"];
            Reload();
        });

        Reload();
    }

    public override void OnActivated() => Reload();

    private void Reload()
    {
        Items.Clear();
        foreach (var p in ProfileService.GetAll())
            Items.Add(new ProfileVm(p));
        if (Selected == null) Selected = Items.FirstOrDefault();
    }

    protected override void OnLanguageChanged() => Reload();
}

// ============================ Settings ============================

public class SettingsViewModel : PageVmBase
{
    public int LanguageIndex
    {
        get => L.IsFa ? 0 : 1;
        set
        {
            var lang = value == 0 ? "fa" : "en";
            L.Instance.SetLanguage(lang);
            Raise(nameof(LanguageIndex));
            StatusMessage = Lang["st_saved"];
        }
    }

    private bool _minimizeToTray;
    public bool MinimizeToTray
    {
        get => _minimizeToTray;
        set { if (Set(ref _minimizeToTray, value)) { SettingsService.Current.MinimizeToTray = value; SettingsService.Save(); } }
    }

    private string _monitorTarget = "1.1.1.1";
    public string MonitorTarget
    {
        get => _monitorTarget;
        set { if (Set(ref _monitorTarget, value)) { SettingsService.Current.MonitorTarget = value; SettingsService.Save(); } }
    }

    private string _statusMessage = "";
    public string StatusMessage { get => _statusMessage; set => Set(ref _statusMessage, value); }

    public string VersionText => "NetPilot 1.2.1";

    /// <summary>Language choices for the settings combo box.</summary>
    public List<ChoiceVm> LanguageOptions { get; } = new();

    public SettingsViewModel()
    {
        _minimizeToTray = SettingsService.Current.MinimizeToTray;
        _monitorTarget = SettingsService.Current.MonitorTarget;

        LanguageOptions.Add(new ChoiceVm("fa", Lang["st_persian"]));
        LanguageOptions.Add(new ChoiceVm("en", Lang["st_english"]));
    }

    protected override void OnLanguageChanged()
    {
        if (LanguageOptions.Count >= 2)
        {
            LanguageOptions[0].Label = Lang["st_persian"];
            LanguageOptions[1].Label = Lang["st_english"];
        }
        Raise(nameof(VersionText));
    }
}
