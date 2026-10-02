using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NetPilot.Models;

namespace NetPilot.Services;

/// <summary>Evaluates scheduled limits every 15 seconds and toggles overrides in LimiterService.</summary>
public sealed class ScheduleService
{
    public static readonly ScheduleService Instance = new();
    private ScheduleService() { }

    private readonly object _lock = new();
    private List<ScheduleRule> _items;
    private Timer _timer;
    private readonly object _evalLock = new();

    /// <summary>app path -> id of the schedule currently pushed into LimiterService
    /// ("" = nothing pushed). Lets us apply only on transitions and release orphans.</summary>
    private readonly Dictionary<string, string> _applied = new(StringComparer.OrdinalIgnoreCase);

    public event Action Changed;

    private List<ScheduleRule> Items
    {
        get
        {
            lock (_lock)
                _items ??= Storage.Load("schedules.json", new List<ScheduleRule>());
            return _items;
        }
    }

    public List<ScheduleRule> GetAll()
    {
        lock (_lock) return Items.Select(Clone).ToList();
    }

    public void Start()
    {
        Evaluate();
        _timer ??= new Timer(_ => Evaluate(), null, 15000, 15000);
    }

    public void Stop()
    {
        try { _timer?.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
    }

    public void Save(ScheduleRule rule)
    {
        lock (_lock)
        {
            var list = Items;
            var existing = list.FirstOrDefault(r => r.Id == rule.Id);
            if (existing != null) list.Remove(existing);
            list.Add(rule);
            Storage.Save("schedules.json", list);
        }
        Evaluate();
        Changed?.Invoke();
    }

    public void Delete(string id)
    {
        lock (_lock)
        {
            var list = Items;
            var removed = list.FirstOrDefault(r => r.Id == id);
            if (removed != null) { list.Remove(removed); Storage.Save("schedules.json", list); }
        }
        // Re-evaluate instead of clearing one override by hand: the deleted schedule may
        // have shared its app path with another schedule that must stay enforced, and the
        // orphan-releasing pass below cleans up paths that no longer have any schedule.
        Evaluate();
        Changed?.Invoke();
    }

    /// <summary>Serialized: an evaluation that is already running is skipped rather than
    /// racing over _applied / the limiter's override table.</summary>
    private void Evaluate()
    {
        lock (_evalLock) EvaluateCore();
    }

    private void EvaluateCore()
    {
        try
        {
            var now = DateTime.Now;
            var all = GetAll();
            bool changed = false;

            // Several schedules can target one app, but LimiterService keeps a single
            // override slot per app path - so the winner is computed across ALL of that
            // app's schedules. "Schedule B just ended" can no longer clear "A is still on".
            var inUse = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in all.GroupBy(r => r.AppPath ?? "", StringComparer.OrdinalIgnoreCase))
            {
                string path = group.Key;
                if (string.IsNullOrEmpty(path)) continue;
                inUse.Add(path);

                var active = group.Where(r => r.Enabled && r.Contains(now)).ToList();
                foreach (var r in group)
                    changed |= SetActiveFlag(r.Id, active.Any(a => a.Id == r.Id));

                string winnerId = PickWinner(active)?.Id ?? "";
                string current = null;
                lock (_lock) _applied.TryGetValue(path, out current);
                if (string.Equals(current ?? "", winnerId, StringComparison.Ordinal)) continue;

                if (winnerId.Length == 0) LimiterService.Instance.SetOverride(path, null);
                else LimiterService.Instance.SetOverride(path, ToOverride(PickWinner(active)));
                lock (_lock) _applied[path] = winnerId;
                changed = true;
            }

            // App paths that no longer have any schedule must release their override
            // (covers Delete and an edit that moved a schedule to another app).
            foreach (var stale in _applied.Keys.Where(k => !inUse.Contains(k)).ToList())
            {
                LimiterService.Instance.SetOverride(stale, null);
                lock (_lock) _applied.Remove(stale);
                changed = true;
            }

            if (changed) Changed?.Invoke();
        }
        catch (Exception ex) { App.LogCrash(ex); }
    }

    private static ScheduleRule PickWinner(List<ScheduleRule> active) =>
        active
            .OrderBy(r => r.BlockInstead ? 0 : 1)          // a block beats a throttle
            .ThenBy(r => Math.Min(r.DownLimitBps <= 0 ? long.MaxValue : r.DownLimitBps,
                                  r.UpLimitBps <= 0 ? long.MaxValue : r.UpLimitBps))
            .ThenBy(r => r.Id, StringComparer.Ordinal)      // deterministic tie-break
            .FirstOrDefault();

    private static LimitRule ToOverride(ScheduleRule rule) => new()
    {
        Id = "sched_" + rule.Id,
        AppPath = rule.AppPath,
        AppName = rule.AppName,
        Mode = rule.BlockInstead ? LimitMode.Block : LimitMode.Limit,
        DownLimitBps = rule.DownLimitBps,
        UpLimitBps = rule.UpLimitBps,
        Enabled = true,
    };

    private bool SetActiveFlag(string id, bool active)
    {
        lock (_lock)
        {
            var r = Items.FirstOrDefault(x => x.Id == id);
            if (r == null || r.ActiveNow == active) return false;
            r.ActiveNow = active;
            return true;
        }
    }

    private static ScheduleRule Clone(ScheduleRule r) => new()
    {
        Id = r.Id, AppPath = r.AppPath, AppName = r.AppName,
        Start = r.Start, End = r.End,
        DownLimitBps = r.DownLimitBps, UpLimitBps = r.UpLimitBps,
        BlockInstead = r.BlockInstead, Enabled = r.Enabled, ActiveNow = r.ActiveNow,
    };
}
