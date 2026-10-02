using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using NetPilot.Models;

namespace NetPilot.Services;

/// <summary>
/// Continuous background sampler: download/upload speed, ping, packet loss, online state.
/// Emits a Sampled event every tick; keeps a rolling history for charts.
/// </summary>
public sealed class MonitorService
{
    public static readonly MonitorService Instance = new();
    private MonitorService() { }

    private readonly object _lock = new();
    private readonly List<NetworkSample> _history = new();
    private readonly Queue<bool> _pingWindow = new();
    private const int PingWindowSize = 20;

    private CancellationTokenSource _cts;
    private long _lastDown, _lastUp;
    private double _emaDown, _emaUp;
    private bool _primed;
    private int _consecutivePingFails;
    private long _sessionDown, _sessionUp;
    private DateTime _onlineSince = DateTime.Now;

    public NetworkSample Latest { get; private set; } = new();
    public double EmaDown { get { lock (_lock) return _emaDown; } }
    public double EmaUp { get { lock (_lock) return _emaUp; } }
    public long SessionDown { get { lock (_lock) return _sessionDown; } }
    public long SessionUp { get { lock (_lock) return _sessionUp; } }
    public TimeSpan Uptime => DateTime.Now - _onlineSince;
    public bool Online { get; private set; } = true;

    public event Action<NetworkSample> Sampled;
    public event Action<bool> OnlineChanged;

    public List<NetworkSample> GetHistory()
    {
        lock (_lock) return _history.ToList();
    }

    private static (long down, long up) ReadCounters()
    {
        long down = 0, up = 0;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (nic.NetworkInterfaceType != NetworkInterfaceType.Ethernet &&
                    nic.NetworkInterfaceType != NetworkInterfaceType.Wireless80211 &&
                    nic.NetworkInterfaceType != NetworkInterfaceType.GigabitEthernet &&
                    nic.NetworkInterfaceType != NetworkInterfaceType.FastEthernetT &&
                    nic.NetworkInterfaceType != NetworkInterfaceType.FastEthernetFx)
                    continue;
                var s = nic.GetIPStatistics();
                down += s.BytesReceived;
                up += s.BytesSent;
            }
        }
        catch { }
        return (down, up);
    }

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            long lastTickDown = 0, lastTickUp = 0;
            int tick = 0;
            // Real elapsed time: a tick that waits for a ping (up to ~1.2s) or gets
            // descheduled must not be reported as 0.5s, or every rate is inflated.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!ct.IsCancellationRequested)
            {
                var (down, up) = ReadCounters();
                // Real elapsed time: a tick that waits for a ping (up to ~1.2s) or gets
                // descheduled must not be reported as 0.5s, or every rate is inflated.
                var elapsed = clock.Elapsed.TotalSeconds;
                clock.Restart();
                if (elapsed > 10)
                {
                    // The machine slept, or the thread was descheduled for seconds. Dividing
                    // the whole gap by the 10s clamp reported a 60 MB burst over 30 s as
                    // 180 MB/s and fed that into the EMA for the following samples. Re-base
                    // on the counters instead of publishing a rate that never happened.
                    _primed = false;
                    lastTickDown = down; lastTickUp = up;
                    Thread.Sleep(50);
                    continue;
                }
                double dt = Math.Clamp(elapsed, 0.05, 10);
                if (!_primed) { _primed = true; lastTickDown = down; lastTickUp = up; }
                long dDelta = Math.Max(0, down - lastTickDown);
                long uDelta = Math.Max(0, up - lastTickUp);
                lastTickDown = down; lastTickUp = up;

                double rawDown = dDelta / dt;
                double rawUp = uDelta / dt;

                // Ignore counter resets (adapter reconnection) gracefully.
                if (rawDown > 500_000_000 || rawUp > 500_000_000) { rawDown = _emaDown; rawUp = _emaUp; }

                const double alpha = 0.45;
                double smDown = _primed ? alpha * rawDown + (1 - alpha) * _emaDown : rawDown;
                double smUp = _primed ? alpha * rawUp + (1 - alpha) * _emaUp : rawUp;

                double pingMs = Latest.PingMs;
                bool pingOk = false;
                if (tick % 2 == 0) // ping every ~1s
                    (pingMs, pingOk) = await DoPingAsync(ct);
                if (!ct.IsCancellationRequested)
                    UpdatePingWindow(pingOk);

                double loss = 0;
                lock (_lock)
                {
                    _emaDown = smDown; _emaUp = smUp;
                    _sessionDown += dDelta; _sessionUp += uDelta;
                    loss = _pingWindow.Count > 0 ? 100.0 * _pingWindow.Count(v => !v) / _pingWindow.Count : 0;
                }

                bool wasOnline = Online;
                if (pingOk) { _consecutivePingFails = 0; if (!Online) Online = true; }
                else
                {
                    _consecutivePingFails++;
                    if (_consecutivePingFails >= 5) Online = false;
                }
                if (Online && !wasOnline) { _onlineSince = DateTime.Now; OnlineChanged?.Invoke(true); }
                else if (!Online && wasOnline) OnlineChanged?.Invoke(false);

                var sample = new NetworkSample
                {
                    Ts = DateTime.Now,
                    DownBps = smDown,
                    UpBps = smUp,
                    PingMs = pingMs,
                    LossPct = loss,
                    Online = Online,
                };
                lock (_lock)
                {
                    Latest = sample;
                    _history.Add(sample);
                    if (_history.Count > 600) _history.RemoveAt(0);
                }

                UsageService.Instance.AddSample(dDelta, uDelta);
                Sampled?.Invoke(sample);

                tick++;
                try { await Task.Delay(500, ct); } catch (TaskCanceledException) { break; }
            }
        }, ct);
    }

    public void Stop()
    {
        // Keep _cts non-null: the sampling loop may still be winding down, and a second
        // Start() before it exits would run two loops against the same counters.
        try { _cts?.Cancel(); } catch { }
    }

    private void UpdatePingWindow(bool ok)
    {
        lock (_lock)
        {
            _pingWindow.Enqueue(ok);
            while (_pingWindow.Count > PingWindowSize) _pingWindow.Dequeue();
        }
    }

    private async Task<(double ms, bool ok)> DoPingAsync(CancellationToken ct)
    {
        string target = SettingsService.Current.MonitorTarget;
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(target, 1200);
            return reply.Status == IPStatus.Success ? (reply.RoundtripTime, true) : (0, false);
        }
        catch { return (0, false); }
    }

    /// <summary>Gateway ping in ms, or -1 — used by health score as secondary latency source.</summary>
    public static async Task<double> PingHostAsync(string host, int timeout = 1500)
    {
        try
        {
            using var ping = new Ping();
            var r = await ping.SendPingAsync(host, timeout);
            return r.Status == IPStatus.Success ? r.RoundtripTime : -1;
        }
        catch { return -1; }
    }
}
