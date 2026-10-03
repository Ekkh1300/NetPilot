using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media;
using System.Windows.Threading;
using NetPilot.Core;
using NetPilot.Services;
using L = NetPilot.Lang.Lang;

namespace NetPilot.ViewModels;

/// <summary>Display wrapper around <see cref="MvLink"/> with localized labels.</summary>
public sealed class MobileLinkVm : ObservableObject
{
    private static readonly Brush KindUsb = new SolidColorBrush(Color.FromRgb(56, 189, 248));
    private static readonly Brush KindHotspot = new SolidColorBrush(Color.FromRgb(167, 139, 250));
    private static readonly Brush KindLan = new SolidColorBrush(Color.FromRgb(52, 211, 153));
    private static readonly Brush KindOther = new SolidColorBrush(Color.FromRgb(148, 163, 184));

    private string _kind = "other";
    private string _name = "";
    private string _description = "";
    private string _ipv4 = "";
    private string _gateway = "";
    private bool _isUp;
    private int _metric;

    public int IfIndex { get; }

    public string Name { get => _name; private set => Set(ref _name, value); }
    public string Description { get => _description; private set => Set(ref _description, value); }
    public string Ipv4 { get => _ipv4; private set => Set(ref _ipv4, value); }
    public string Gateway { get => _gateway; private set => Set(ref _gateway, value); }
    public bool IsUp { get => _isUp; private set { if (Set(ref _isUp, value)) { Raise(nameof(StatusText)); Raise(nameof(StatusBrush)); } } }
    public int Metric { get => _metric; private set => Set(ref _metric, value); }

    public string KindKey => _kind is "usb" or "hotspot" or "lan" ? "mv_link_kind_" + _kind : "mv_link_kind_other";

    private string Kind
    {
        get => _kind;
        set
        {
            if (!Set(ref _kind, value)) return;
            Raise(nameof(KindLabel));
            Raise(nameof(KindBrush));
        }
    }

    public string KindLabel => L.Instance[KindKey];
    public Brush KindBrush => _kind switch
    {
        "usb" => KindUsb,
        "hotspot" => KindHotspot,
        "lan" => KindLan,
        _ => KindOther,
    };
    public string StatusText => L.Instance[IsUp ? "mv_up" : "mv_down"];
    public Brush StatusBrush => IsUp
        ? new SolidColorBrush(Color.FromRgb(52, 211, 153))
        : new SolidColorBrush(Color.FromRgb(148, 163, 184));

    public MobileLinkVm(MvLink link)
    {
        IfIndex = link.IfIndex;
        Update(link);
    }

    public void Update(MvLink link)
    {
        Name = link.Name;
        Description = link.Description;
        Ipv4 = link.Ipv4;
        Gateway = link.Gateway;
        IsUp = link.IsUp;
        Metric = link.Metric;
        Kind = link.Kind;
    }

    public void RefreshLang()
    {
        Raise(nameof(KindLabel));
        Raise(nameof(StatusText));
    }

    public MvLink ToLink() => new()
    {
        IfIndex = IfIndex,
        Name = Name,
        Description = Description,
        Kind = KindKey.EndsWith("usb") ? "usb" : KindKey.EndsWith("hotspot") ? "hotspot" : "lan",
        IsUp = IsUp,
        Ipv4 = Ipv4,
        Gateway = Gateway,
        Metric = Metric,
    };
}

/// <summary>Page 16: move a phone VPN connection onto the PC, safely and reversibly.</summary>
public sealed class MobileVpnViewModel : PageVmBase
{
    private readonly DispatcherTimer _timer;
    private bool _polling;
    private bool _busy;

    public ObservableCollection<MobileLinkVm> Links { get; } = new();
    public ObservableCollection<ChoiceVm> MethodOptions { get; } = new();

    private MobileLinkVm _selectedLink;
    public MobileLinkVm SelectedLink { get => _selectedLink; set => Set(ref _selectedLink, value); }

    private ChoiceVm _selectedMethod;
    public ChoiceVm SelectedMethod { get => _selectedMethod; set => Set(ref _selectedMethod, value); }

    private bool _mobileConnected;
    public bool MobileConnected { get => _mobileConnected; private set => Set(ref _mobileConnected, value); }

    private string _mobileText = "";
    public string MobileText { get => _mobileText; private set => Set(ref _mobileText, value); }

    private Brush _mobileBrush = Brushes.Gray;
    public Brush MobileBrush { get => _mobileBrush; private set => Set(ref _mobileBrush, value); }

    private string _mobileVpnText = "";
    public string MobileVpnText { get => _mobileVpnText; private set => Set(ref _mobileVpnText, value); }

    private Brush _mobileVpnBrush = Brushes.Gray;
    public Brush MobileVpnBrush { get => _mobileVpnBrush; private set => Set(ref _mobileVpnBrush, value); }

    private string _pcVpnText = "";
    public string PcVpnText { get => _pcVpnText; private set => Set(ref _pcVpnText, value); }

    private Brush _pcVpnBrush = Brushes.Gray;
    public Brush PcVpnBrush { get => _pcVpnBrush; private set => Set(ref _pcVpnBrush, value); }

    private string _pcText = "";
    public string PcText { get => _pcText; private set => Set(ref _pcText, value); }

    private Brush _pcBrush = Brushes.Gray;
    public Brush PcBrush { get => _pcBrush; private set => Set(ref _pcBrush, value); }

    private string _trafficText = "0 B/s";
    public string TrafficText { get => _trafficText; private set => Set(ref _trafficText, value); }

    private string _peerText = "—";
    public string PeerText { get => _peerText; private set => Set(ref _peerText, value); }

    private bool _sharing;
    public bool Sharing
    {
        get => _sharing;
        private set { if (Set(ref _sharing, value)) { Raise(nameof(SharingText)); Raise(nameof(SharingBrush)); } }
    }

    public string SharingText => L.Instance[Sharing ? "mv_share_on" : "mv_ready"];
    public Brush SharingBrush => Sharing ? Good : Warn;

    private bool _confirmVpnChange;
    /// <summary>
    /// The only switch that lets this feature influence the default route while a PC VPN
    /// is up. Off by default, so a VPN is never bypassed without the user asking for it.
    /// </summary>
    public bool ConfirmVpnChange { get => _confirmVpnChange; set => Set(ref _confirmVpnChange, value); }

    private bool _hasSnapshot;
    public bool HasSnapshot { get => _hasSnapshot; private set => Set(ref _hasSnapshot, value); }

    private string _snapshotText = "";
    public string SnapshotText { get => _snapshotText; private set => Set(ref _snapshotText, value); }

    private string _statusMessage = "";
    public string StatusMessage { get => _statusMessage; private set => Set(ref _statusMessage, value); }

    private bool _apiRunning;
    public bool ApiRunning
    {
        get => _apiRunning;
        private set { if (Set(ref _apiRunning, value)) { Raise(nameof(ApiStateText)); Raise(nameof(ApiToggleText)); Raise(nameof(ApiRunningBrush)); } }
    }

    public string ApiStateText => L.Instance[ApiRunning ? "mv_api_running" : "mv_api_stopped"];
    public string ApiToggleText => L.Instance[ApiRunning ? "mv_api_stop" : "mv_api_start"];
    public Brush ApiRunningBrush => ApiRunning ? Good : Warn;
    public string PairedText => L.Instance[Paired ? "mv_paired" : "mv_not_paired"];
    public Brush PairedBrush => Paired ? Good : Warn;

    private string _pairingCode = "";
    public string PairingCode { get => _pairingCode; private set => Set(ref _pairingCode, value); }

    private string _apiAddress = "";
    public string ApiAddress { get => _apiAddress; private set => Set(ref _apiAddress, value); }

    private bool _paired;
    public bool Paired
    {
        get => _paired;
        private set { if (Set(ref _paired, value)) { Raise(nameof(PairedText)); Raise(nameof(PairedBrush)); } }
    }

    private string _deviceText = "";
    public string DeviceText { get => _deviceText; private set => Set(ref _deviceText, value); }

    private string _phoneReport = "";
    public string PhoneReport { get => _phoneReport; private set => Set(ref _phoneReport, value); }

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand CaptureCommand { get; }
    public AsyncRelayCommand RestoreCommand { get; }
    public AsyncRelayCommand ShareCommand { get; }
    public AsyncRelayCommand StopCommand { get; }
    public RelayCommand ToggleApiCommand { get; }
    public RelayCommand NewCodeCommand { get; }

    public MobileVpnViewModel()
    {
        MethodOptions.Add(new ChoiceVm("mv_method_auto", L.Instance["mv_method_auto"]));
        MethodOptions.Add(new ChoiceVm("mv_method_usb", L.Instance["mv_method_usb"]));
        MethodOptions.Add(new ChoiceVm("mv_method_hotspot", L.Instance["mv_method_hotspot"]));
        MethodOptions.Add(new ChoiceVm("mv_method_lan", L.Instance["mv_method_lan"]));
        MethodOptions.Add(new ChoiceVm("mv_method_proxy", L.Instance["mv_method_proxy"]));
        SelectedMethod = MethodOptions[0];

        RefreshCommand = new AsyncRelayCommand(PollAsync);
        CaptureCommand = new AsyncRelayCommand(CaptureAsync);
        RestoreCommand = new AsyncRelayCommand(RestoreAsync);
        ShareCommand = new AsyncRelayCommand(ShareAsync, () => !_busy);
        StopCommand = new AsyncRelayCommand(StopShareAsync, () => !_busy);
        ToggleApiCommand = new RelayCommand(ToggleApi);
        NewCodeCommand = new RelayCommand(() =>
        {
            MobileVpnApi.Instance.NewPairingCode();
            PairingCode = MobileVpnApi.Instance.PairingCode;
        });

        ApiAddress = $"http://pc:{MobileVpnService.ApiPort}";
        PairingCode = MobileVpnApi.Instance.PairingCode;
        ApiRunning = MobileVpnApi.Instance.Running;
        Paired = MobileVpnApi.Instance.Paired;
        DeviceText = MobileVpnApi.Instance.DeviceName;
        SnapshotText = MobileVpnService.Instance.HasSnapshot
            ? MobileVpnService.Instance.SnapshotTime : "";
        HasSnapshot = MobileVpnService.Instance.HasSnapshot;

        // Neutral placeholders so the tiles are never blank while the first probe runs
        // (it takes about a second to start PowerShell and collect the adapters).
        MobileText = L.Instance["mv_mobile_disconnected"];
        MobileVpnText = L.Instance["mv_vpn_unknown"];
        PcVpnText = L.Instance["mv_pc_vpn_off"];
        PcText = L.Instance["mv_pc_disconnected"];
        PhoneReport = L.Instance["mv_no_report"];

        MobileVpnApi.Instance.Changed += () => Dispatch(() =>
        {
            ApiRunning = MobileVpnApi.Instance.Running;
            PairingCode = MobileVpnApi.Instance.PairingCode;
            Paired = MobileVpnApi.Instance.Paired;
            DeviceText = MobileVpnApi.Instance.DeviceName;
        });

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) => { if (!_busy) _ = PollAsync(); };

        // Warm the probe up at construction time so the page already shows real data the
        // first time it is opened instead of waiting a second on the placeholder text.
        _ = PollAsync();
    }

    // -------------------------------------------------------------- lifecycle

    public override void OnActivated()
    {
        _timer.Start();
        _ = PollAsync();
    }

    public override void OnDeactivated() => _timer.Stop();

    protected override void OnLanguageChanged()
    {
        foreach (var o in MethodOptions)
            o.Label = L.Instance[o.Key];
        foreach (var l in Links) l.RefreshLang();
        Raise(nameof(ApiAddress));
        Raise(nameof(ApiStateText));
        Raise(nameof(ApiToggleText));
        Raise(nameof(ApiRunningBrush));
        Raise(nameof(PairedText));
        Raise(nameof(SharingText));
        Raise(nameof(SharingBrush));
    }

    // -------------------------------------------------------------- polling

    private async System.Threading.Tasks.Task PollAsync()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var s = await MobileVpnService.Instance.DetectAsync();
                ApplyState(s);

                // The first probe of a link only primes the byte counters, so its rate is
                // zero by definition. Take one more sample right away - otherwise the tile
                // reads 0 B/s until the next poll, which is exactly what the eye catches
                // when the page is opened. The loop bound keeps this to a single retry.
                if (!s.WasBaseline) break;
                // force: a cached answer would hand back the very baseline we are retrying,
                // and the tile would sit at 0 B/s until the cache expired on its own.
                await System.Threading.Tasks.Task.Delay(1500);
                s = await MobileVpnService.Instance.DetectAsync(force: true);
                ApplyState(s);
            }
        }
        catch (Exception ex) { App.LogCrash(ex); }
        finally { _polling = false; }
    }

    private void ApplyState(MvState s)
    {
        MobileConnected = s.MobileConnected;
        MobileText = L.Instance[s.MobileConnected ? "mv_mobile_connected" : "mv_mobile_disconnected"];
        MobileBrush = s.MobileConnected ? Good : Bad;

        MobileVpnText = L.Instance[s.MobileVpnActive switch
        {
            true => "mv_vpn_active",
            false => "mv_vpn_inactive",
            _ => "mv_vpn_unknown",
        }];
        MobileVpnBrush = s.MobileVpnActive switch
        {
            true => Good,
            false => Bad,
            _ => Warn,
        };

        PcVpnText = s.PcVpnActive ? L.Instance["mv_pc_vpn_on"]
                   : s.PcVpnUnknown ? L.Instance["mv_pc_vpn_unknown"]
                   : L.Instance["mv_pc_vpn_off"];
        PcVpnBrush = s.PcVpnActive ? Warn : s.PcVpnUnknown ? Muted : Good;

        PcText = L.Instance[s.PcConnected ? "mv_pc_connected" : "mv_pc_disconnected"];
        PcBrush = s.PcConnected ? Good : Bad;

        // Two lines: a "down / up" line is wider than this tile can ever be at 1280px,
        // so a single row simply clips the upload rate.
        TrafficText = $"{SpeedFormatConverter.FormatSpeed(s.RateDown)} ↓\n" +
                      $"{SpeedFormatConverter.FormatSpeed(s.RateUp)} ↑";
        PeerText = s.LocalPeers.ToString();
        Sharing = s.Sharing;

        SyncLinks(s.Links);

        // The address the phone should actually type. "pc" is only a placeholder and a
        // phone cannot resolve it — the connection attempt died with UnknownHost, shown
        // to the user as "Wrong address, or the two devices are on different networks".
        // Show the LAN address a phone on the same network can reach us on: the uplink
        // first (it is the one with a gateway), then any live LAN interface, then the
        // USB/hotspot link the phone itself may have created.
        var reachable =
            s.Links.FirstOrDefault(l => l.IsUp && l.Kind == "lan" && !string.IsNullOrEmpty(l.Gateway))
            ?? s.Links.FirstOrDefault(l => l.IsUp && l.Kind == "lan")
            ?? s.Links.FirstOrDefault(l => l.IsUp && !string.IsNullOrEmpty(l.Ipv4));
        var addr = reachable?.Ipv4 ?? "";
        ApiAddress = addr.Length > 0
            ? $"http://{addr}:{MobileVpnService.ApiPort}"
            : $"http://pc:{MobileVpnService.ApiPort}";

        if (SelectedLink == null && Links.Count > 0)
            SelectedLink = Links[0];

        HasSnapshot = MobileVpnService.Instance.HasSnapshot;
        SnapshotText = HasSnapshot ? MobileVpnService.Instance.SnapshotTime : "";

        PhoneReport = s.MobileVpnActive.HasValue || !string.IsNullOrEmpty(MobileVpnApi.Instance.DeviceName)
            ? (string.IsNullOrEmpty(MobileVpnApi.Instance.DeviceDetail)
                ? MobileVpnApi.Instance.DeviceName
                : MobileVpnApi.Instance.DeviceDetail)
            : L.Instance["mv_no_report"];
    }

    private static readonly Brush Good = new SolidColorBrush(Color.FromRgb(52, 211, 153));
    private static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(251, 191, 36));
    // Neutral, for "we cannot tell" - neither a green tick nor a warning.
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(148, 163, 184));
    private static readonly Brush Bad = new SolidColorBrush(Color.FromRgb(248, 113, 113));

    private void SyncLinks(List<MvLink> fresh)
    {
        var ordered = fresh
            .OrderBy(l => l.Kind switch { "usb" => 0, "hotspot" => 1, "lan" => 2, _ => 3 })
            .ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        int selectedIfIndex = SelectedLink?.IfIndex ?? 0;
        bool sameOrder = ordered.Count == Links.Count &&
                         ordered.Select((l, i) => l.IfIndex == Links[i].IfIndex).All(x => x);

        if (!sameOrder)
        {
            Links.Clear();
            foreach (var l in ordered) Links.Add(new MobileLinkVm(l));
        }
        else
        {
            for (int i = 0; i < ordered.Count; i++) Links[i].Update(ordered[i]);
        }

        if (selectedIfIndex != 0)
            SelectedLink = Links.FirstOrDefault(l => l.IfIndex == selectedIfIndex) ?? SelectedLink;
    }

    // -------------------------------------------------------------- actions

    private void SetStatus(MvResult r, string successFallback = null)
    {
        string text = L.Instance[r.Key];
        if (r.Ok && !string.IsNullOrEmpty(successFallback)) text = successFallback;
        if (!r.Ok && !string.IsNullOrEmpty(r.Detail)) text += "  " + r.Detail;
        StatusMessage = text;
    }

    private async System.Threading.Tasks.Task CaptureAsync()
    {
        _busy = true;
        try { SetStatus(await MobileVpnService.Instance.CaptureSnapshotAsync()); }
        catch (Exception ex) { App.LogCrash(ex); StatusMessage = ex.Message; }
        finally
        {
            _busy = false;
            HasSnapshot = MobileVpnService.Instance.HasSnapshot;
            SnapshotText = HasSnapshot ? MobileVpnService.Instance.SnapshotTime : "";
            ShareCommand.RaiseCanExecuteChanged();
            StopCommand.RaiseCanExecuteChanged();
        }
    }

    private async System.Threading.Tasks.Task RestoreAsync()
    {
        _busy = true;
        try { SetStatus(await MobileVpnService.Instance.RestoreSnapshotAsync()); }
        catch (Exception ex) { App.LogCrash(ex); StatusMessage = ex.Message; }
        finally
        {
            _busy = false;
            Sharing = MobileVpnService.Instance.IsSharing;
            ShareCommand.RaiseCanExecuteChanged();
            StopCommand.RaiseCanExecuteChanged();
        }
    }

    private async System.Threading.Tasks.Task ShareAsync()
    {
        _busy = true;
        ShareCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        try
        {
            var state = await MobileVpnService.Instance.DetectAsync();
            ApplyState(state);

            string methodKey = SelectedMethod?.Key ?? "mv_method_auto";
            MvMethod method = methodKey == "mv_method_proxy" ? MvMethod.Proxy : MvMethod.Auto;

            MvLink link = methodKey switch
            {
                "mv_method_usb" => state.UsbLink,
                "mv_method_hotspot" => state.HotspotLink,
                _ => SelectedLink?.ToLink() ?? state.AnyUpLink,
            };

            if (methodKey == "mv_method_auto")
                link = state.AnyUpLink ?? link;
            if (methodKey == "mv_method_lan")
                link = state.Links.FirstOrDefault(l => l.Kind == "lan" && l.IsUp) ?? link;

            var r = await MobileVpnService.Instance.ShareAsync(link, method, ConfirmVpnChange);
            SetStatus(r);
            if (r.Ok) Sharing = true;
        }
        catch (Exception ex) { App.LogCrash(ex); StatusMessage = ex.Message; }
        finally
        {
            _busy = false;
            ShareCommand.RaiseCanExecuteChanged();
            StopCommand.RaiseCanExecuteChanged();
        }
    }

    private async System.Threading.Tasks.Task StopShareAsync()
    {
        _busy = true;
        ShareCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        try { SetStatus(await MobileVpnService.Instance.StopShareAsync()); }
        catch (Exception ex) { App.LogCrash(ex); StatusMessage = ex.Message; }
        finally
        {
            _busy = false;
            Sharing = MobileVpnService.Instance.IsSharing;
            ShareCommand.RaiseCanExecuteChanged();
            StopCommand.RaiseCanExecuteChanged();
        }
    }

    private void ToggleApi()
    {
        if (MobileVpnApi.Instance.Running) MobileVpnApi.Instance.Stop();
        else MobileVpnApi.Instance.Start(MobileVpnService.ApiPort);

        ApiRunning = MobileVpnApi.Instance.Running;
        PairingCode = MobileVpnApi.Instance.PairingCode;
        // Say why a start did not take, instead of leaving a button that looks dead. The
        // common cause is the port still held by http.sys right after a stop.
        StatusMessage = ApiRunning
            ? L.Instance["mv_api_running"]
            : MobileVpnApi.Instance.LastError.Length > 0
                ? L.Instance["mv_api_start_failed"].Replace("%1$s", MobileVpnApi.Instance.LastError)
                : L.Instance["mv_api_stopped"];
    }
}
