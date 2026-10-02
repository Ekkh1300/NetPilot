using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NetPilot.Models;

namespace NetPilot.Services;

/// <summary>
/// Real per-application internet limiter:
///  - Block  -> Windows Firewall block rules (both directions)
///  - Upload -> native QoS bandwidth throttle (ThrottleRateActionBitsPerSecond)
///  - Download -> token-bucket shaping by toggling the firewall drop rule
///                based on real per-process traffic measured via ETW.
/// </summary>
public sealed class LimiterService
{
    public static readonly LimiterService Instance = new();
    private LimiterService() { }

    private readonly object _lock = new();
    private readonly object _tokenLock = new();   // _tokens is written by the worker AND the shaper timer
    private List<LimitRule> _base;
    private readonly Dictionary<string, LimitRule> _overrides = new(StringComparer.OrdinalIgnoreCase);
    private Timer _shaper;
    private int _shaperBusy;                       // one shaper tick at a time
    private readonly Dictionary<string, double> _tokens = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastTickAt = DateTime.Now;

    // Enforcement launches netsh/PowerShell and can take seconds. It runs on a single
    // serial background worker so it never blocks the caller (usually the UI thread) and
    // two enforcements for the same app can never overlap.
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _pending = new();
    private readonly SemaphoreSlim _pendingSignal = new(0);
    private CancellationTokenSource _workerCts;

    public event Action Changed;

    private List<LimitRule> Base
    {
        get
        {
            lock (_lock)
                _base ??= Storage.Load("limits.json", new List<LimitRule>());
            return _base;
        }
    }

    public List<LimitRule> GetRules()
    {
        lock (_lock) return Base.ToList();
    }

    public void Start()
    {
        if (_workerCts == null)
        {
            _workerCts = new CancellationTokenSource();
            var ct = _workerCts.Token;
            _ = Task.Run(() => WorkerAsync(ct));
        }
        // Queued, not executed here: ServiceHub.Init() runs on the UI thread and this used
        // to stall startup by one process spawn per rule.
        foreach (var r in GetRules()) Enqueue(r.AppPath);
        _shaper ??= new Timer(_ => ShaperTick(), null, 1000, 500);
    }

    public void Stop()
    {
        try { _shaper?.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
        try { _shaper?.Dispose(); } catch { }
        _shaper = null;
        try { _workerCts?.Cancel(); } catch { }
        try { _pendingSignal.Release(); } catch { }   // wake the worker so it can exit
        // Null it out so a later Start() can spin up a fresh worker. It used to stay
        // cancelled-but-not-null, so the guard in Start() ("if (_workerCts == null)") skipped
        // the new worker and every queued enforcement was dropped on the floor afterwards -
        // limits silently stopped being applied while the UI still showed them as enabled.
        // Not disposed here on purpose: the worker may still be reading its token.
        _workerCts = null;
    }

    private void Enqueue(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        _pending.Enqueue(path);
        try { _pendingSignal.Release(); } catch (ObjectDisposedException) { }
    }

    private async Task WorkerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await _pendingSignal.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }

            while (_pending.TryDequeue(out var path))
            {
                if (ct.IsCancellationRequested) break;
                try { await EnforceAsync(path).ConfigureAwait(false); }
                catch (Exception ex) { App.LogCrash(ex); }
            }
        }
    }

    // ---------------- public actions ----------------

    public async Task ApplyAsync(LimitRule rule)
    {
        lock (_lock)
        {
            var list = Base;
            var existing = list.FirstOrDefault(r => string.Equals(r.AppPath, rule.AppPath, StringComparison.OrdinalIgnoreCase));
            if (existing != null) list.Remove(existing);
            list.Add(rule);
            Storage.Save("limits.json", list);
        }
        Enforce(rule.AppPath);
        await Task.CompletedTask;
        Changed?.Invoke();
    }

    public async Task RemoveAsync(string ruleId)
    {
        string path = null;
        lock (_lock)
        {
            var list = Base;
            var r = list.FirstOrDefault(x => x.Id == ruleId);
            if (r != null) { path = r.AppPath; list.Remove(r); Storage.Save("limits.json", list); }
        }
        if (path != null)
        {
            // The worker applies the remaining override if there is one, and otherwise
            // removes the firewall/QoS rules - both used to happen synchronously here.
            Enforce(path);
        }
        await Task.CompletedTask;
        Changed?.Invoke();
    }

    /// <summary>Schedule override: pass a rule to force it, null to fall back to the base rule.</summary>
    public void SetOverride(string appPath, LimitRule rule)
    {
        if (string.IsNullOrEmpty(appPath)) return;
        lock (_lock)
        {
            if (rule == null) _overrides.Remove(appPath);
            else _overrides[appPath] = rule;
        }
        Enforce(appPath);
        Changed?.Invoke();
    }

    /// <summary>Replaces the entire rule set (used by Profiles). Rules that disappear are cleaned up.</summary>
    public void SetRules(List<LimitRule> rules)
    {
        List<string> paths;
        lock (_lock)
        {
            var old = Base.Select(r => r.AppPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _base = rules?.ToList() ?? new List<LimitRule>();
            Storage.Save("limits.json", _base);
            paths = old.Concat(_base.Select(r => r.AppPath)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        foreach (var p in paths) Enforce(p);
        Changed?.Invoke();
    }

    // ---------------- enforcement ----------------

    /// <summary>Queues enforcement for one app path. Returns immediately; the work runs on
    /// the serial background worker, so callers (UI commands, timers) never wait for netsh
    /// or a PowerShell process.</summary>
    private void Enforce(string path) => Enqueue(path);

    private async Task EnforceAsync(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            LimitRule effective;
            lock (_lock)
            {
                if (_overrides.TryGetValue(path, out var ov)) effective = ov;
                else effective = Base.FirstOrDefault(r => string.Equals(r.AppPath, path, StringComparison.OrdinalIgnoreCase));
            }

            if (effective == null || !effective.Enabled || effective.Mode == LimitMode.Allow)
            {
                FirewallHelper.RemoveRulesFor(path);
                await RemoveQosAsync(path).ConfigureAwait(false);
                lock (_tokenLock) _tokens.Remove(path);
                return;
            }

            if (effective.Mode == LimitMode.Block)
            {
                await RemoveQosAsync(path).ConfigureAwait(false);
                FirewallHelper.AddBlockRules(path, out string outName, out string inName);
                FirewallHelper.SetRuleEnabled(outName, true);
                FirewallHelper.SetRuleEnabled(inName, true);
                return;
            }

            // Limit mode
            FirewallHelper.AddBlockRules(path, out string oName, out string iName);
            FirewallHelper.SetRuleEnabled(oName, false);
            FirewallHelper.SetRuleEnabled(iName, false);
            await SetQosAsync(path, effective.UpLimitBps).ConfigureAwait(false);
            lock (_tokenLock)
            {
                if (!_tokens.ContainsKey(path)) _tokens[path] = 0;
            }
        }
        catch (Exception ex) { App.LogCrash(ex); }
    }

    private static string QosName(string path) =>
        "NP_" + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(path.ToLowerInvariant()))[..8]);

    private static async Task SetQosAsync(string path, long upBps)
    {
        await RemoveQosAsync(path);
        if (upBps <= 0) return;
        long bps = upBps * 8; // bytes/s -> bits/s
        string name = QosName(path);
        await Sys.PsAsync(
            $"New-NetQosPolicy -Name '{name}' -AppPathNameMatchCondition '{path.Replace("'", "''")}' " +
            $"-ThrottleRateActionBitsPerSecond {bps} -ErrorAction Stop | Out-Null", 20000);
    }

    private static async Task RemoveQosAsync(string path)
    {
        string name = QosName(path);
        await Sys.PsAsync($"Remove-NetQosPolicy -Name '{name}' -Confirm:$false -ErrorAction SilentlyContinue | Out-Null", 15000);
    }

    // ---------------- download shaper (token bucket) ----------------

    private void ShaperTick()
    {
        // A slow tick (COM call + process snapshot) must never overlap the next one:
        // _tokens and _lastTickAt are not safe against two concurrent ticks.
        if (Interlocked.CompareExchange(ref _shaperBusy, 1, 0) != 0) return;
        try
        {
            var now = DateTime.Now;
            double dt = Math.Clamp((now - _lastTickAt).TotalSeconds, 0.05, 3);
            _lastTickAt = now;

            List<LimitRule> active;
            lock (_lock)
                active = _overrides.Values.Concat(Base)
                    .Where(r => r.Enabled && r.Mode == LimitMode.Limit && r.DownLimitBps > 0)
                    .GroupBy(r => r.AppPath, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToList();
            if (active.Count == 0) return;

            var snapshot = ProcessNetworkService.Instance.GetSnapshot();
            foreach (var rule in active)
            {
                double limit = rule.DownLimitBps;
                double cap = limit * 2;
                double tokens;
                lock (_tokenLock)
                {
                    _tokens.TryGetValue(rule.AppPath, out tokens);
                    tokens = Math.Min(cap, tokens + limit * dt);
                }

                double bytes = 0;
                foreach (var app in snapshot)
                {
                    if (PathsMatch(app.Path, rule.AppPath) ||
                        string.Equals(app.ProcessName, System.IO.Path.GetFileNameWithoutExtension(rule.AppPath), StringComparison.OrdinalIgnoreCase))
                    {
                        // Sum every process of the app, not just the first: Chrome alone runs
                        // five processes on the same binary, and charging only the busiest one
                        // let the rule pass ~5x its limit.
                        bytes += app.DownBps * dt;
                    }
                }

                bool blocked;
                if (bytes > tokens) { tokens = 0; blocked = true; }
                else { tokens -= bytes; blocked = false; }
                lock (_tokenLock) _tokens[rule.AppPath] = Math.Max(0, tokens);

                // Shape download by dropping inbound packets during over-budget windows.
                FirewallHelper.SetRuleEnabled(FirewallHelper.RuleName(rule.AppPath, "in"), blocked);
            }
        }
        catch (Exception ex) { App.LogCrash(ex); }
        finally { Interlocked.Exchange(ref _shaperBusy, 0); }
    }

    private static bool PathsMatch(string processPath, string rulePath)
    {
        if (string.IsNullOrEmpty(processPath) || string.IsNullOrEmpty(rulePath)) return false;
        return string.Equals(processPath.Trim(), rulePath.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    // ---------------- helpers for UI ----------------

    public static string FormatRate(long bytesPerSec)
    {
        if (bytesPerSec <= 0) return "Unlimited";
        return Core.SpeedFormatConverter.FormatSpeed(bytesPerSec);
    }

    /// <summary>Parses "512 KB/s", "1 Mbps", "2M" ... into bytes/second. Returns 0 for unlimited/invalid.</summary>
    public static long ParseRate(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        text = text.Trim().ToLowerInvariant().Replace("/s", "").Trim();
        // NOTE: stripping "ps" here destroyed the bit unit before matching: "1 mbps" became
        // "1 mb" and was read as 1 MiB/s (8x too fast) - and "bps" could never match at all.
        // Longest first, so "mbps" wins over "mb" and "bps" over "b".
        string[] units = { "gbps", "mbps", "kbps", "bps", "bit", "gb", "g", "mb", "m", "kb", "k", "b" };
        double mult = 1;
        foreach (var u in units)
        {
            if (text.EndsWith(u))
            {
                string num = text[..^u.Length].Trim();
                if (!double.TryParse(num, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double v)) return -1;
                bool bits = u.EndsWith("ps") || u == "bit";
                mult = u.StartsWith('g') ? 1024 * 1024 * 1024
                     : u.StartsWith('m') ? 1024 * 1024
                     : u.StartsWith('k') ? 1024
                     : 1;
                if (bits) mult /= 8;
                return (long)(v * mult);
            }
        }
        if (double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double plain))
            return (long)plain;
        return -1;
    }
}
