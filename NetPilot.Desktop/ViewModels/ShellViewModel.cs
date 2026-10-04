using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using NetPilot.Core.Logging;
using NetPilot.Desktop.Views;
using NetPilot.Models;
using NetPilot.Services;

namespace NetPilot.Desktop;

/// <summary>
/// The nav shell, plus the live state every page reads.
///
/// One poller, one set of properties. The WPF app has a view model per page; here they share
/// a source because there are fewer pages and they all need the same link list and the same
/// capability verdict - and two pollers racing each other would show two different truths.
/// </summary>
public sealed class ShellViewModel : INotifyPropertyChanged
{
    private readonly AppServices _services;
    private readonly System.Threading.CancellationTokenSource _cts = new();
    private Task _poller;

    public ShellViewModel(AppServices services)
    {
        _services = services;
        PlatformName = services.Backend.Platform;
        IsPrivileged = services.Backend.IsPrivileged;
        Support = services.Backend.Capability(new AppHandle { Uid = -1 });
        BuildCapabilities(services.Backend);

        Pages = new ObservableCollection<NavPage>
        {
            new("dashboard",       "Dashboard",            DashboardView),
            new("mobile_vpn",       "Phone Tunnel \u2192 PC",   TunnelView),
            new("dns_manager",      "DNS",                  DnsView),
            new("net_limiter",      "Limiter",              LimiterView),
            new("adapters",         "Adapters",             AdaptersView),
            new("capabilities",     "What this machine can do", CapabilitiesView),
            new("diagnostics",       "Diagnostics",              DiagnosticsView),
        };
        Current = Pages[0];
        _poller = PollAsync(_cts.Token);
    }

    public event PropertyChangedEventHandler PropertyChanged;

    public ObservableCollection<NavPage> Pages { get; }

    public string PlatformName { get; }

    public bool IsPrivileged { get; }

    public RuleSupport Support { get; }

    public object DashboardView { get; } = new DashboardView();
    public object TunnelView { get; } = new TunnelView();
    public object DnsView { get; } = new DnsView();
    public object LimiterView { get; } = new LimiterView();
    public object AdaptersView { get; } = new AdaptersView();
    public object CapabilitiesView { get; } = new CapabilitiesView();
    public object DiagnosticsView { get; } = new DiagnosticsView();

    private DiagnosticsViewModel _diagnosticsVm;
    /// <summary>The same object the page is bound to. Held as a property so the refresh timer
    /// and the page cannot end up looking at two different snapshots.</summary>
    public DiagnosticsViewModel Diagnostics
    {
        get => _diagnosticsVm ??= new DiagnosticsViewModel(_services);
        private set => _diagnosticsVm = value;
    }

    private NavPage _current;
    public NavPage Current
    {
        get => _current;
        set { _current = value; Raise(); Raise(nameof(CurrentTitle)); }
    }

    public string CurrentTitle => _current?.Title ?? "";

    /// <summary>Selects a page by key. Used by the sidebar and by --page; returns false for an
    /// unknown key rather than throwing, so a typo in a launch argument cannot stop the app
    /// from starting.</summary>
    public bool Pick(string key)
    {
        var page = Pages.FirstOrDefault(p => p.Key == key);
        if (page is null) return false;
        Current = page;
        return true;
    }

    // ---------------- live state ----------------

    private IReadOnlyList<MvLink> _links = Array.Empty<MvLink>();
    public IReadOnlyList<MvLink> Links
    {
        get => _links;
        private set { _links = value; Raise(); Raise(nameof(UpCount)); Raise(nameof(LinkNames)); }
    }

    public int UpCount => _links.Count(l => l.IsUp);

    public string LinkNames => _links.Count == 0
        ? "none detected"
        : string.Join("   ", _links.Where(l => l.IsUp).Select(l => $"{l.Name} ({l.Ipv4})").Take(3));

    private long _rateDown;
    public long RateDown
    {
        get => _rateDown;
        private set { _rateDown = value; Raise(); Raise(nameof(RateDownText)); }
    }

    private long _rateUp;
    public long RateUp
    {
        get => _rateUp;
        private set { _rateUp = value; Raise(); Raise(nameof(RateUpText)); }
    }

    public string RateDownText => Human(_rateDown);

    public string RateUpText => Human(_rateUp);

    private MvLink _rateLink;
    public MvLink RateLink
    {
        get => _rateLink;
        private set { _rateLink = value; Raise(); Raise(nameof(RateLinkName)); }
    }

    public string RateLinkName => _rateLink == null ? "no link" : _rateLink.Name;

    /// <summary>
    /// What this machine can actually do, built from the platform rather than from a static
    /// feature list. Every row is a fact about the running system, so the same page is correct
    /// on Linux with root, on Linux without, and on macOS - where the honest answer for the
    /// two headline features is "not possible", with the reason attached.
    ///
    /// The reason is not decoration. "pf has no process matcher" tells someone whether to
    /// expect this in a future release or to stop looking; a bare "unsupported" is a bug report
    /// waiting to happen.
    /// </summary>
    public ObservableCollection<CapabilityRow> Capabilities { get; } = new();

    private void BuildCapabilities(INetworkBackend backend)
    {
        string os = backend.Platform;
        bool mac = os == "macos";
        bool linux = os == "linux";
        bool priv = backend.IsPrivileged;

        void Row(string feature, string state, string detail, string brush) =>
            Capabilities.Add(new CapabilityRow(feature, state, detail, brush));

        Row("Link detection and traffic counters", "yes",
            "read from the operating system", CapabilityRow.Yes);
        Row("DNS servers per interface", "read", "always available", CapabilityRow.Yes);

        if (mac)
        {
            Row("System proxy for the phone tunnel", "yes", "via networksetup", CapabilityRow.Yes);
            Row("Block one application", "not possible",
                "macOS's pf has no process matcher, and the Application Firewall is inbound-only. " +
                "This needs a Network Extension: a signed native app, a provisioning profile, and " +
                "Apple's approval.",
                CapabilityRow.No);
            Row("Bandwidth limit per application", "not possible",
                "macOS has no per-flow shaper. This needs a Network Extension too.", CapabilityRow.No);
            Row("PC VPN state", "yes", "scutil, with the three-state rule", CapabilityRow.Yes);
        }
        else if (linux)
        {
            Row("System proxy for the phone tunnel", "yes",
                "via GNOME gsettings; other desktops need the environment variable", CapabilityRow.Maybe);
            Row("Block one application", priv ? "yes" : "needs root",
                "nftables, matched by uid. That is what the kernel can match on, so a rule applies " +
                "to everything running as that user rather than to one executable.",
                priv ? CapabilityRow.Yes : CapabilityRow.Maybe);
            Row("Bandwidth limit per application", priv ? "yes" : "needs root",
                "tc with the flower skuid filter. Upload only; there is no per-uid ingress policer " +
                "this cheap.",
                priv ? CapabilityRow.Yes : CapabilityRow.Maybe);
            Row("PC VPN state", "yes", "tunnel interfaces plus nmcli, three states", CapabilityRow.Yes);
        }
        else
        {
            Row("System proxy for the phone tunnel", "no",
                "this build reads the network only; the desktop app does the rest", CapabilityRow.No);
            Row("Block one application", "no",
                "the Windows desktop app owns the firewall and the QoS policy store", CapabilityRow.No);
            Row("Bandwidth limit per application", "no",
                "the Windows desktop app owns the QoS policy store", CapabilityRow.No);
            Row("PC VPN state", "yes", "adapters, three states", CapabilityRow.Yes);
        }

        Row("Per-application traffic attribution", "no",
            "no base system on any of these exposes per-process byte counters. The Windows app " +
            "gets it from ETW, which has no equivalent here.",
            CapabilityRow.No);
    }

    private IReadOnlyList<AdapterDnsState> _dns = Array.Empty<AdapterDnsState>();
    public IReadOnlyList<AdapterDnsState> Dns
    {
        get => _dns;
        private set { _dns = value; Raise(); }
    }

    private string _pcVpn = "off";
    public string PcVpn
    {
        get => _pcVpn;
        private set { _pcVpn = value; Raise(); Raise(nameof(PcVpnBrush)); Raise(nameof(PcVpnText)); }
    }

    /// <summary>Three states, not two: an adapter that is up with no address is a VPN client
    /// that is not tunnelling, and calling that "active" would block the phone tunnel for no
    /// reason. That mistake shipped once already; the third state is the fix.</summary>
    public string PcVpnText => _pcVpn switch
    {
        "active" => "connected",
        "unknown" => "unknown \u2014 a VPN adapter is up but carries no address",
        _ => "not connected",
    };

    public string PcVpnBrush => _pcVpn switch
    {
        "active" => "#34D399",
        "unknown" => "#FBBF24",
        _ => "#93A1BC",
    };

    private string _status = "reading the network\u2026";
    public string Status
    {
        get => _status;
        private set { _status = value; Raise(); }
    }

    /// <summary>Bytes/second to something a person can read. Uses the same units and the same
    /// rounding as the Windows build, so a number means the same thing on both.</summary>
    public static string Human(double bps)
    {
        if (double.IsNaN(bps) || bps <= 0) return "0 B/s";
        string[] units = { "B/s", "KB/s", "MB/s", "GB/s" };
        int i = 0;
        while (bps >= 1024 && i < units.Length - 1) { bps /= 1024; i++; }
        return $"{bps:0.#} {units[i]}";
    }

    private async Task PollAsync(CancellationToken ct)
    {
        // Counter deltas are kept here rather than per page, so two pages showing the same
        // number always agree.
        long prevRx = 0, prevTx = 0;
        int prevIndex = -1;
        DateTime prevAt = DateTime.MinValue;
        var history = new List<double>();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var links = await _services.Backend.GetLinksAsync(ct).ConfigureAwait(false);
                Links = links;

                var dns = await _services.Backend.GetDnsAsync(ct).ConfigureAwait(false);
                Dns = dns;

                var vpn = await _services.Backend.GetPcVpnStateAsync(ct).ConfigureAwait(false);
                PcVpn = vpn.Active ? "active" : vpn.Unknown ? "unknown" : "off";

                // Only ever compute a rate from two samples of the *same* link. Comparing two
                // different adapters' counters produces a large, entirely fictional number.
                var up = links.Where(l => l.IsUp && !l.IsVpn && l.Ipv4.Length > 0)
                              .OrderByDescending(l => l.Rx)
                              .FirstOrDefault();
                RateLink = up;
                if (up != null)
                {
                    var now = DateTime.UtcNow;
                    if (prevIndex == up.IfIndex && prevAt != DateTime.MinValue)
                    {
                        double secs = (now - prevAt).TotalSeconds;
                        if (secs > 0.4)
                        {
                            RateDown = (long)Math.Max(0, (up.Rx - prevRx) / secs);
                            RateUp = (long)Math.Max(0, (up.Tx - prevTx) / secs);
                        }
                    }
                    prevRx = up.Rx;
                    prevTx = up.Tx;
                    prevIndex = up.IfIndex;
                    prevAt = now;
                }
                else
                {
                    RateDown = 0;
                    RateUp = 0;
                    prevAt = DateTime.MinValue;
                }
                history.Add(RateDown);
                if (history.Count > 60) history.RemoveAt(0);
                History = history.ToArray();

                Status = $"{UpCount} of {Links.Count} links up";

                // One line per poll at Debug, not Info: on a machine left open this is 43,000
                // lines a day, which is not a log, it is noise that buries the one entry that
                // explains the fault.
                Log.Debug("probe", $"{UpCount}/{Links.Count} links up; " +
                                    $"down={ShellViewModel.Human(RateDown)} up={ShellViewModel.Human(RateUp)}; " +
                                    $"pc vpn {PcVpn}");

                // The log view re-reads on the same tick, so the counters and the entries
                // beside them are from the same moment. Refreshing them separately is how a
                // page ends up showing "dropped: 0" above a log that is missing entries.
                if (_current?.Key == "diagnostics") Diagnostics.Refresh();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // A failed probe must not leave the page claiming a healthy network, and must
                // not kill the poller.
                Status = "read failed: " + ex.Message;
                // Warn, not Error: a probe that fails once on a flaky machine is not a crash,
                // and it keeps going. The next successful poll is visible in the log anyway.
                Log.Warn("probe", "a poll failed", ex);
            }

            try { await Task.Delay(2000, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private IReadOnlyList<double> _history = Array.Empty<double>();

    /// <summary>The last minute of download samples, for the chart. Public so the chart binds
    /// to one source instead of every page keeping its own copy.</summary>
    public IReadOnlyList<double> History
    {
        get => _history;
        private set { _history = value; Raise(); }
    }

    private void Raise([CallerMemberName] string name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class NavPage
{
    public NavPage(string key, string title, object view)
    {
        Key = key;
        Title = title;
        View = view;
    }

    public string Key { get; }
    public string Title { get; }
    public object View { get; }
}

/// <summary>
/// One row of the capability table: what the feature is called, what it can do here, and -
/// when it cannot - the actual reason.
///
/// The reason is not decoration. macOS refusing per-app blocking with "pf has no process
/// matcher" tells a user whether to expect this in a future release or to stop looking.
/// A bare "unsupported" is the same as a bug report waiting to happen.
/// </summary>
public sealed class CapabilityRow
{
    public CapabilityRow(string feature, string state, string detail, string brush)
    {
        Feature = feature;
        State = state;
        Detail = detail;
        Brush = brush;
    }

    public string Feature { get; }
    public string State { get; }
    public string Detail { get; }
    public string Brush { get; }

    public const string Yes = "#34D399";
    public const string No = "#F87171";
    public const string Maybe = "#FBBF24";
}

