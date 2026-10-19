using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using NetPilot.Core;
using NetPilot.Services;
using NetPilot.ViewModels;
using L = NetPilot.Lang.Lang;

namespace NetPilot.ViewModels;

public class NavItem : ObservableObject
{
    private readonly MainViewModel _owner;
    public string Key { get; }
    public string Glyph { get; }

    /// <summary>
    /// The page ViewModel this item shows. Every page owns a *fixed* ContentControl in
    /// the shell (see MainWindow.xaml); selecting a nav item only flips Visibility.
    /// This matters: swapping Content on a single shared ContentPresenter makes WPF run
    /// ContentPresenter.EnsureTemplate, which sets Template = null and *then* raises an
    /// inheritable DataContext change while the old subtree is still attached. The walk
    /// reaches FrameworkContentElement.OnPropertyChanged, which dereferences
    /// TemplatedParent.TemplateInternal — now null — and throws. That aborts ApplyTemplate
    /// and leaves _templateIsCurrent = true, so the presenter never recovers: the view
    /// freezes on the previous page. Keeping Content immutable avoids the code path.
    /// </summary>
    public object Vm { get; }

    public Lang.Lang Lang => L.Instance;

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (Set(ref _isSelected, value) && value) _owner.Switch(Key);
        }
    }

    public string Label => Lang[Key];

    public NavItem(MainViewModel owner, string key, string glyph, object vm)
    {
        _owner = owner;
        Key = key;
        Glyph = glyph;
        Vm = vm;
        L.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is "Item[]" or null || e.PropertyName.StartsWith("Item[")) Raise(nameof(Label));
        };
    }
}

/// <summary>One sidebar section: a localized label and the pages under it.</summary>
public sealed class NavSection : ObservableObject
{
    private readonly Lang.Lang _lang;

    public NavSection(Lang.Lang lang, string key)
    {
        _lang = lang;
        Key = key;
        lang.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is "Item[]" or null || e.PropertyName.StartsWith("Item[")) Raise(nameof(Label));
        };
    }

    public string Key { get; }

    /// <summary>A live lookup, exactly like NavItem's: a label stored in a field would
    /// freeze whichever language the shell happened to be showing when the window was
    /// built, and stay wrong after every later language switch.</summary>
    public string Label => _lang[Key];

    public ObservableCollection<NavItem> Items { get; } = new();
}

/// <summary>Shell VM: navigation, language, status bar, tray integration, window commands.</summary>
public class MainViewModel : ObservableObject
{
    public Lang.Lang Lang => L.Instance;
    public ObservableCollection<NavItem> Nav { get; } = new();

    /// <summary>
    /// The same items as <see cref="Nav"/>, their flat order preserved, grouped for the
    /// sidebar's section headers. Eighteen buttons in one column stopped being scannable a
    /// while ago; sections make a page findable by what it ist rather than by remembered
    /// position. Selection still runs against the flat <see cref="Nav"/> list, so grouping is
    /// presentation only and cannot desynchronise it.
    /// </summary>
    public ObservableCollection<NavSection> NavSections { get; } = new();

    private object _currentPage;
    public object CurrentPage
    {
        get => _currentPage;
        private set => Set(ref _currentPage, value);
    }

    private string _statusDns = "…";
    public string StatusDns { get => _statusDns; private set => Set(ref _statusDns, value); }

    private string _healthText = "—";
    public string HealthText { get => _healthText; private set => Set(ref _healthText, value); }

    private Brush _healthBrush = Brushes.Gray;
    public Brush HealthBrush { get => _healthBrush; private set => Set(ref _healthBrush, value); }

    private string _langButtonText = "EN";
    public string LangButtonText { get => _langButtonText; private set => Set(ref _langButtonText, value); }

    public DashboardViewModel Dashboard { get; }
    public DnsManagerViewModel DnsManager { get; }
    public BenchmarkViewModel Benchmark { get; }
    public SmartDnsViewModel SmartDns { get; }
    public MonitorViewModel Monitor { get; }
    public PerAppViewModel PerApp { get; }
    public NetLimiterViewModel NetLimiter { get; }
    public SchedulesViewModel Schedules { get; }
    public UsageHistoryViewModel UsageHistory { get; }
    public ConnectionHistoryViewModel ConnectionHistory { get; }
    public ToolsViewModel Tools { get; }
    public AdaptersViewModel Adapters { get; }
    public ProfilesViewModel Profiles { get; }
    public MobileVpnViewModel MobileVpn { get; }
    public SettingsViewModel Settings { get; }
    public DiagnosticsViewModel Diagnostics { get; }
    public BondViewModel Bond { get; }

    public RelayCommand MinimizeCommand { get; }
    public RelayCommand CloseCommand { get; }
    public RelayCommand ExitCommand { get; }
    public RelayCommand LangCommand { get; }

    public string VersionText => "NetPilot " +
        (typeof(MainViewModel).Assembly.GetName().Version is { } v ? v.ToString(3) : "?");

    public static event Action StatusRefreshRequested;

    public static void RequestStatusRefresh() => StatusRefreshRequested?.Invoke();

    public MainViewModel()
    {
        Dashboard = new DashboardViewModel();
        DnsManager = new DnsManagerViewModel();
        Benchmark = new BenchmarkViewModel();
        SmartDns = new SmartDnsViewModel(DnsManager);
        Monitor = new MonitorViewModel();
        PerApp = new PerAppViewModel();
        NetLimiter = new NetLimiterViewModel();
        Schedules = new SchedulesViewModel();
        UsageHistory = new UsageHistoryViewModel();
        ConnectionHistory = new ConnectionHistoryViewModel();
        Tools = new ToolsViewModel();
        Adapters = new AdaptersViewModel();
        Profiles = new ProfilesViewModel();
        MobileVpn = new MobileVpnViewModel();
        Settings = new SettingsViewModel();
        Diagnostics = new DiagnosticsViewModel();
        Bond = new BondViewModel();

        foreach (var vm in new PageVmBase[]
        {
            Dashboard, DnsManager, Benchmark, SmartDns, Monitor, PerApp, NetLimiter, Schedules,
            UsageHistory, ConnectionHistory, Bond, Tools, Adapters, Profiles, MobileVpn, Settings, Diagnostics,
        })
            vm.NavigateRequested += Switch;

        AddNav("dashboard", "\uE80F", Dashboard);
        AddNav("dns_manager", "\uE968", DnsManager);
        AddNav("dns_benchmark", "\uE9D9", Benchmark);
        AddNav("smart_dns", "\uE945", SmartDns);
        AddNav("network_monitor", "\uE83F", Monitor);
        AddNav("per_app_usage", "\uE7BA", PerApp);
        AddNav("net_limiter", "\uE738", NetLimiter);
        AddNav("scheduled_limits", "\uE823", Schedules);
        AddNav("network_history", "\uE81C", UsageHistory);
        AddNav("connection_history", "\uE7B6", ConnectionHistory);
        // \uE896 is the download glyph in Segoe MDL2.
        AddNav("bond_download", "\uE896", Bond);
        AddNav("network_tools", "\uE90F", Tools);
        AddNav("adapters", "\uE968", Adapters);
        AddNav("profiles", "\uE736", Profiles);
        AddNav("mobile_vpn", "\uE774", MobileVpn);
        AddNav("settings", "\uE713", Settings);
        // Diagnostics last: it is the page people reach for when something is already wrong,
        // not somewhere to start a session from.
        AddNav("diagnostics", "\uE9D5", Diagnostics);

        // Section layout. Every page appears in exactly one section, the flat Nav order is
        // untouched, and a missing or mistyped key is a loud failure rather than a page that
        // silently disappears from the sidebar.
        var sectionOf = new Dictionary<string, (NavSection section, int index)>
        {
            ["nav_group_start"] = (null, 0),
            ["nav_group_dns"] = (null, 0),
            ["nav_group_network"] = (null, 0),
            ["nav_group_history"] = (null, 0),
            ["nav_group_links"] = (null, 0),
            ["nav_group_tools"] = (null, 0),
            ["nav_group_system"] = (null, 0),
        };
        foreach (var key in new[]
                 {
                     "nav_group_start", "nav_group_dns", "nav_group_network", "nav_group_history",
                     "nav_group_links", "nav_group_tools", "nav_group_system",
                 })
        {
            var section = new NavSection(L.Instance, key);
            NavSections.Add(section);
            sectionOf[key] = (section, NavSections.Count - 1);
        }

        var byKey = new Dictionary<string, NavItem>();
        foreach (var n in Nav) byKey[n.Key] = n;
        var pageSections = new Dictionary<string, string>
        {
            ["dashboard"] = "nav_group_start",
            ["dns_manager"] = "nav_group_dns",
            ["dns_benchmark"] = "nav_group_dns",
            ["smart_dns"] = "nav_group_dns",
            ["network_monitor"] = "nav_group_network",
            ["per_app_usage"] = "nav_group_network",
            ["net_limiter"] = "nav_group_network",
            ["scheduled_limits"] = "nav_group_network",
            ["adapters"] = "nav_group_network",
            ["network_history"] = "nav_group_history",
            ["connection_history"] = "nav_group_history",
            ["bond_download"] = "nav_group_links",
            ["mobile_vpn"] = "nav_group_links",
            ["network_tools"] = "nav_group_tools",
            ["profiles"] = "nav_group_tools",
            ["settings"] = "nav_group_system",
            ["diagnostics"] = "nav_group_system",
        };
        foreach (var n in Nav)
        {
            // A page without a section is a bug visible at first launch, not a blank row: the
            // group it names must exist and the page must have been written into the table.
            if (!pageSections.TryGetValue(n.Key, out var g) || !sectionOf.TryGetValue(g, out var pair))
                throw new InvalidOperationException($"page '{n.Key}' names no existing nav section");
            pair.section.Items.Add(n);
        }

        CurrentPage = Dashboard;
        Nav[0].IsSelected = true;

        MinimizeCommand = new RelayCommand(() =>
        {
            var win = Application.Current.MainWindow;
            if (win != null) win.WindowState = WindowState.Minimized;
        });
        CloseCommand = new RelayCommand(() =>
        {
            if (Settings.MinimizeToTray) Application.Current.MainWindow?.Hide();
            else App.ExitApp();
        });
        ExitCommand = new RelayCommand(() => App.ExitApp());
        LangCommand = new RelayCommand(() =>
        {
            L.Instance.SetLanguage(L.IsFa ? "en" : "fa");
            LangButtonText = L.IsFa ? "EN" : "FA";
        });
        LangButtonText = L.IsFa ? "EN" : "FA";

        // Status bar + health chip
        StatusRefreshRequested += () => _ = RefreshStatusAsync();
        HealthService.Instance.Updated += snap => Dispatch(() =>
        {
            HealthText = $"{snap.Score:0}%";
            HealthBrush = snap.Score >= 85 ? new SolidColorBrush(Color.FromRgb(52, 211, 153))
                        : snap.Score >= 60 ? new SolidColorBrush(Color.FromRgb(251, 191, 36))
                        : new SolidColorBrush(Color.FromRgb(248, 113, 113));
        });
        L.Instance.PropertyChanged += (_, _) => { LangButtonText = L.IsFa ? "EN" : "FA"; _ = RefreshStatusAsync(); };

        // Tray integration
        TrayService.Instance.OpenRequested += () => Dispatch(() =>
        {
            var win = Application.Current.MainWindow;
            if (win == null) return;
            win.Show();
            win.WindowState = WindowState.Normal;
            win.Activate();
        });
        TrayService.Instance.ExitRequested += () => Dispatch(() => App.ExitApp());
        TrayService.Instance.RestoreRequested += () => Dispatch(() => _ = DnsManager.RestoreAsync());
        TrayService.Instance.QuickApplyRequested += entry => Dispatch(() => _ = DnsManager.ApplyAsync(entry, true));

        _ = RefreshStatusAsync();
    }

    private void AddNav(string key, string glyph, object vm)
    {
        var item = new NavItem(this, key, glyph, vm);
        Nav.Add(item);
        if (Nav.Count == 1) item.IsSelected = true;
    }

    public void Switch(string key)
    {
        var previous = CurrentPage;
        object page = key switch
        {
            "dashboard" => Dashboard,
            "dns_manager" => DnsManager,
            "dns_benchmark" => Benchmark,
            "smart_dns" => SmartDns,
            "network_monitor" => Monitor,
            "per_app_usage" => PerApp,
            "net_limiter" => NetLimiter,
            "scheduled_limits" => Schedules,
            "network_history" => UsageHistory,
            "connection_history" => ConnectionHistory,
            "network_tools" => Tools,
            "adapters" => Adapters,
            "profiles" => Profiles,
            "mobile_vpn" => MobileVpn,
            "settings" => Settings,
            _ => Dashboard,
        };
        CurrentPage = page;
        // Enforce exclusivity here as well, so navigation stays correct even if the
        // RadioButtons inside the ItemsControl do not share a grouping scope.
        foreach (var n in Nav)
        {
            bool shouldSelect = n.Key == key;
            if (n.IsSelected != shouldSelect) n.IsSelected = shouldSelect;
        }
        // Give the outgoing page a chance to stop its timers before the incoming one starts.
        if (!ReferenceEquals(previous, page)) (previous as PageVmBase)?.Deactivate();
        (page as PageVmBase)?.Activate();
        if (key is "dns_manager" or "smart_dns") _ = RefreshStatusAsync();
    }

    private async System.Threading.Tasks.Task RefreshStatusAsync()
    {
        try { StatusDns = await DnsService.DescribeCurrentAsync(); }
        catch { StatusDns = "—"; }
        TrayService.Instance.RefreshStatusAsync().ConfigureAwait(false);
    }

    internal static void Dispatch(Action action)
    {
        var app = Application.Current;
        if (app?.Dispatcher == null) { action(); return; }
        app.Dispatcher.BeginInvoke(action);
    }
}

/// <summary>Base for all page VMs: localized text refresh + navigation event.</summary>
public abstract class PageVmBase : ObservableObject
{
    public Lang.Lang Lang => L.Instance;
    public event Action<string> NavigateRequested;

    protected PageVmBase()
    {
        L.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is null or "" or "Item[]")
            {
                // Re-read every computed property on this page (status lines, mode
                // descriptions, range labels, health text ...) so a language switch is
                // complete without each page having to list its own property names.
                // Page-specific leftovers (rows living in an ItemsSource) are handled
                // by OnLanguageChanged, which runs right after.
                RefreshAll();
                OnLanguageChanged();
            }
        };
    }

    protected void Navigate(string key) => NavigateRequested?.Invoke(key);
    protected virtual void OnLanguageChanged() { }
    public virtual void OnActivated() { }

    /// <summary>Called when another page replaces this one - stop any polling started in
    /// <see cref="OnActivated"/> so background work does not outlive the page.</summary>
    public virtual void OnDeactivated() { }

    /// <summary>True only for the page on screen. Every page VM is built once at startup
    /// and subscribes to high-frequency services in its constructor, so their handlers
    /// check this flag: otherwise all 15 pages would rebuild charts and lists on every
    /// network sample even though 14 of them are hidden.</summary>
    public bool IsActive { get; private set; }

    /// <summary>Marks the page visible and runs <see cref="OnActivated"/>.</summary>
    internal void Activate()
    {
        IsActive = true;
        OnActivated();
    }

    /// <summary>Marks the page hidden (before <see cref="OnDeactivated"/> runs), so any
    /// handler still queued on the dispatcher drops its work.</summary>
    internal void Deactivate()
    {
        IsActive = false;
        OnDeactivated();
    }

    protected static void Dispatch(Action action) => MainViewModel.Dispatch(action);
}
