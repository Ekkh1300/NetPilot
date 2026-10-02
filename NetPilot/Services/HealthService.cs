using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NetPilot.Models;

namespace NetPilot.Services;

/// <summary>
/// Computes a REAL network health score (0-100) from measured data:
/// DNS response time (25%), latency (30%), packet loss (25%), connection stability (20%).
/// </summary>
public sealed class HealthService
{
    public static readonly HealthService Instance = new();
    private HealthService() { }

    private Timer _timer;
    private bool _started;
    private int _busy;     // one Recompute in flight
    private int _probeBusy; // one DNS probe in flight
    private double _dnsMs = -1;
    /// <summary>Set when a resolver query was actually attempted and did not answer —
    /// distinct from "not probed yet", which is merely unknown.</summary>
    private bool _dnsFailed;
    private DateTime _lastDnsProbe = DateTime.MinValue;
    private string _lastDnsServer = "";

    public event Action<HealthSnapshot> Updated;
    public HealthSnapshot Latest { get; private set; } = new();

    public void Start()
    {
        if (_started) return;   // a second Start would orphan a timer Stop() can never reach
        _started = true;
        _ = ProbeDnsAsync();
        _timer = new Timer(_ =>
        {
            // A recompute (DNS probe + PowerShell) can outlast the 5s period; without this
            // guard two of them race over _dnsMs/_lastDnsProbe and publish twice.
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
            _ = RunRecomputeAsync();
        }, null, 3000, 5000);
    }

    private async Task RunRecomputeAsync()
    {
        try { await RecomputeAsync(); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    public void Stop()
    {
        try
        {
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
            _timer?.Dispose();
        }
        catch { }
        _timer = null;
    }

    /// <summary>Forces an immediate DNS re-probe (used by Smart DNS after a change).</summary>
    public async Task RefreshDnsProbeAsync()
    {
        _lastDnsServer = "";
        await ProbeDnsAsync();
        await RecomputeAsync();
    }

    private async Task ProbeDnsAsync()
    {
        if (Interlocked.CompareExchange(ref _probeBusy, 1, 0) != 0) return; // one probe at a time
        try
        {
            var states = await DnsService.GetCurrentAsync();
            string server = states.FirstOrDefault(s => s.V4.Count > 0)?.V4.FirstOrDefault()
                            ?? SettingsService.Current.MonitorTarget;
            if (server == _lastDnsServer && (DateTime.Now - _lastDnsProbe).TotalSeconds < 60 && _dnsMs > 0)
                return;
            _lastDnsServer = server;
            _lastDnsProbe = DateTime.Now;
            var answer = await DnsQuery.QueryAsync(server, "www.microsoft.com", DnsQuery.TypeA, 3000);
            _dnsMs = answer.Ok ? answer.Ms : -1;
            _dnsFailed = !answer.Ok;
        }
        catch { _dnsMs = -1; _dnsFailed = true; }
        finally { Interlocked.Exchange(ref _probeBusy, 0); }
    }

    private async Task RecomputeAsync()
    {
        try
        {
            if ((DateTime.Now - _lastDnsProbe).TotalSeconds > 45)
                await ProbeDnsAsync();

            var mon = MonitorService.Instance;
            var latest = mon.Latest;
            var history = mon.GetHistory().TakeLast(120).ToList(); // last ~60s

            // A minute of samples is a trend; a couple of them are not — leaving that out
            // is what stops the score claiming 100% in the first seconds of a run.
            double stability = history.Count > 10
                ? 100.0 * history.Count(s => s.Online) / history.Count
                : HealthScoring.Unknown;

            double ping = latest.PingMs;
            double loss = latest.LossPct;
            bool trafficOk = mon.EmaDown > 0 || mon.EmaUp > 0;

            // Every echo probe failing while the link is demonstrably carrying data (or
            // still resolving names) means the target stopped answering ICMP — not that
            // the network died. Reporting that as 100% loss, and then as a permanent 15%
            // once MonitorService flips itself offline, was a large part of a score that
            // always read low. Treat it as unmeasured instead, and take the round trip
            // from the default gateway, which answers on essentially every LAN.
            bool icmpFiltered = loss >= 99.99 && (trafficOk || _dnsMs > 0);
            if (icmpFiltered)
            {
                loss = HealthScoring.Unknown;
                if (ping <= 0)
                {
                    double gw = await MonitorService.PingHostAsync(DefaultGateway(), 900);
                    if (gw > 0) ping = gw;
                }
            }

            bool online = ((mon.Online && latest.Online) || icmpFiltered)
                          && (trafficOk || ping > 0 || stability > 60);

            double dnsForScore = _dnsFailed ? HealthScoring.FailedDns : _dnsMs;
            double score = HealthScoring.Score(dnsForScore, ping, loss, stability);
            if (!online) score = Math.Min(score, 15);

            var snap = new HealthSnapshot
            {
                Score = Math.Round(score),
                DnsMs = _dnsMs,
                PingMs = ping,
                LossPct = loss,
                StabilityPct = Math.Round(stability, 1),
                Online = online,
                DnsRating = HealthScoring.Rating(HealthScoring.RateDns(dnsForScore)),
                LatencyRating = HealthScoring.Rating(HealthScoring.RateLatency(ping)),
                LossRating = HealthScoring.Rating(HealthScoring.RateLoss(loss)),
                ConnRating = HealthScoring.RateStability(stability) == HealthScoring.Unknown ? "—"
                    : stability >= 95 ? "Stable"
                    : stability >= 80 ? "MostlyStable"
                    : "Unstable",
            };
            Latest = snap;
            Updated?.Invoke(snap);
        }
        catch (Exception ex) { App.LogCrash(ex); }
    }

    /// <summary>
    /// IPv4 default gateway — a latency target that answers even when ICMP toward the
    /// internet is filtered away, so a blocked echo still yields an honest round trip.
    /// </summary>
    private static string DefaultGateway()
    {
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                foreach (var g in ni.GetIPProperties().GatewayAddresses)
                    if (g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                        && !System.Net.IPAddress.Any.Equals(g.Address))
                        return g.Address.ToString();
            }
        }
        catch { }
        return "";
    }

    /// <summary>Maps a rating token to a localization key.</summary>
    public static string RatingKey(string rating) => rating switch
    {
        "Excellent" => "db_excellent",
        "Good" => "db_good",
        "Fair" => "db_fair",
        "Poor" => "db_poor",
        "Stable" => "db_connection",
        "Unstable" => "db_packet_loss",
        _ => "db_connection",
    };
}
