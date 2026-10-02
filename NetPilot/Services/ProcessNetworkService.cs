using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NetPilot.Models;

namespace NetPilot.Services;

/// <summary>
/// Real per-process network usage via the Windows NT Kernel ETW logger
/// (NETWORK_TCPIP events). Runs entirely on a background thread.
/// </summary>
public sealed class ProcessNetworkService
{
    public static readonly ProcessNetworkService Instance = new();
    private ProcessNetworkService() { }

    private sealed class ProcAgg
    {
        public int Pid;
        public string Name = "";
        public string Path = "";
        public long Down;        // cumulative bytes
        public long Up;          // cumulative bytes
        public long WindowDown;  // bytes since last flush (rate calc)
        public long WindowUp;
        public double RateDown;
        public double RateUp;
        public int Connections;
        public DateTime LastSeen = DateTime.Now;
    }

    private readonly object _lock = new();
    private readonly Dictionary<int, ProcAgg> _procs = new();
    private EtwCollector _etw;
    private bool _etwHealthy;
    private System.Threading.Timer _rateTimer;

    public bool EtwAvailable => _etwHealthy;
    public event Action Updated;

    public void Start()
    {
        if (_rateTimer != null) return;   // guards restarts when ETW init failed (_etw stays null)
        try
        {
            _etw = new EtwCollector(OnBytes);
            if (_etw.Start())
            {
                _etwHealthy = true;
            }
            else
            {
                _etwHealthy = false;
                _etw = null;
            }
        }
        catch (Exception ex)
        {
            App.LogCrash(ex);
            _etwHealthy = false;
            _etw = null;
        }

        _rateTimer = new System.Threading.Timer(_ => FlushRates(), null, 2000, 2000);
    }

    public void Stop()
    {
        _rateTimer?.Dispose();
        _rateTimer = null;
        try { _etw?.Dispose(); } catch { }
        _etw = null;
    }

    private void OnBytes(int pid, long down, long up)
    {
        try
        {
            ProcAgg agg;
            lock (_lock)
            {
                if (!_procs.TryGetValue(pid, out agg))
                {
                    agg = new ProcAgg { Pid = pid };
                    var (name, path) = ResolveProcess(pid);
                    agg.Name = name;
                    agg.Path = path;
                    _procs[pid] = agg;
                }
                agg.Down += down;
                agg.Up += up;
                agg.WindowDown += down;
                agg.WindowUp += up;
                agg.LastSeen = DateTime.Now;
            }
        }
        catch { }
    }

    private static (string name, string path) ResolveProcess(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            string path = "";
            try { path = p.MainModule?.FileName ?? ""; } catch { }
            return (p.ProcessName, path);
        }
        catch { return ($"pid-{pid}", ""); }
    }

    private int _flushBusy;   // one flush at a time (they iterate LastAttrib non-atomically)

    private void FlushRates()
    {
        if (System.Threading.Interlocked.CompareExchange(ref _flushBusy, 1, 0) != 0) return;
        try
        {
            var now = DateTime.Now;
            lock (_lock)
            {
                // connection counts
                var connByPid = AdapterService.GetConnectionPidCounts();
                // drop stale entries (process exited long ago)
                var stale = _procs.Values.Where(v => (now - v.LastSeen).TotalMinutes > 5 && !connByPid.ContainsKey(v.Pid)).ToList();
                foreach (var s in stale) _procs.Remove(s.Pid);

                foreach (var a in _procs.Values)
                {
                    a.RateDown = a.WindowDown / 2.0;
                    a.RateUp = a.WindowUp / 2.0;
                    a.WindowDown = 0;
                    a.WindowUp = 0;
                    a.Connections = connByPid.TryGetValue(a.Pid, out var c) ? c : 0;
                }
            }

            // attribute daily per-app usage from cumulative deltas
            var live = SnapshotUnlocked();
            foreach (var info in live)
            {
                long d = info.TotalDown; // cumulative from process tracking start
                long u = info.TotalUp;
                long dd = d - (LastAttrib.TryGetValue(info.Pid, out var prev) ? prev.d : 0);
                long du = u - (LastAttrib.TryGetValue(info.Pid, out var prevU) ? prevU.u : 0);
                LastAttrib[info.Pid] = (d, u);
                if (dd > 0 || du > 0)
                    UsageService.Instance.AddAppBytes(string.IsNullOrEmpty(info.DisplayName) ? info.ProcessName : info.DisplayName, dd, du);
            }

            // Processes that exited (or aged out of _procs) would otherwise leave an entry
            // here forever, and a returning PID would report a huge bogus delta.
            if (LastAttrib.Count > 0)
            {
                var livePids = live.Select(i => i.Pid).ToHashSet();
                foreach (var pid in LastAttrib.Keys.Where(k => !livePids.Contains(k)).ToList())
                    LastAttrib.Remove(pid);
            }

            Updated?.Invoke();
        }
        catch (Exception ex) { App.LogCrash(ex); }
        finally { System.Threading.Interlocked.Exchange(ref _flushBusy, 0); }
    }

    private readonly Dictionary<int, (long d, long u)> LastAttrib = new();

    public List<AppNetInfo> GetSnapshot()
    {
        lock (_lock) return SnapshotUnlocked();
    }

    private List<AppNetInfo> SnapshotUnlocked()
    {
        return _procs.Values
            .Where(a => a.Down > 0 || a.Up > 0 || a.Connections > 0)
            .Select(a => new AppNetInfo
            {
                Pid = a.Pid,
                ProcessName = a.Name,
                DisplayName = string.IsNullOrEmpty(a.Name) ? a.Path : a.Name,
                Path = a.Path,
                DownBps = a.RateDown,
                UpBps = a.RateUp,
                TotalDown = a.Down,
                TotalUp = a.Up,
                Connections = a.Connections,
                LastSeen = a.LastSeen,
            })
            .OrderByDescending(a => a.DownBps + a.UpBps)
            .ThenByDescending(a => a.TotalDown + a.TotalUp)
            .ToList();
    }
}
