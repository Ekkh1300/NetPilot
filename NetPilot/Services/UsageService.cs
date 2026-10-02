using System;
using System.Collections.Generic;
using System.Linq;
using NetPilot.Models;

namespace NetPilot.Services;

/// <summary>Persists daily download/upload totals and per-app usage for history charts.</summary>
public sealed class UsageService
{
    public static readonly UsageService Instance = new();
    private UsageService() { }

    private readonly object _lock = new();
    private Dictionary<string, DayUsage> _days;
    private DateTime _lastSave = DateTime.MinValue;
    private long _lastReportedTotal;

    public event Action Updated;

    private Dictionary<string, DayUsage> Days
    {
        get
        {
            lock (_lock)
            {
                if (_days == null)
                {
                    var list = Storage.Load("usage.json", new List<DayUsage>());
                    _days = list.ToDictionary(d => d.Date, d => d);
                }
                return _days;
            }
        }
    }

    private static string Today => DateTime.Now.ToString("yyyy-MM-dd");

    public void AddSample(long downDelta, long upDelta)
    {
        if (downDelta < 0 || downDelta > 500_000_000 || upDelta > 500_000_000) return;
        try
        {
            lock (_lock)
            {
                var days = Days;
                if (!days.TryGetValue(Today, out var day))
                    days[Today] = day = new DayUsage { Date = Today };
                day.Down += downDelta;
                day.Up += upDelta;
            }
            // Persist at most once a minute to keep disk writes tiny.
            if ((DateTime.Now - _lastSave).TotalMinutes >= 1)
            {
                Save();
                _lastSave = DateTime.Now;
            }
            var total = MonitorService.Instance.SessionDown + MonitorService.Instance.SessionUp;
            if (Math.Abs(total - _lastReportedTotal) > 5_000_000)
            {
                _lastReportedTotal = total;
                Updated?.Invoke();
            }
        }
        catch (Exception ex) { App.LogCrash(ex); }
    }

    public void AddAppBytes(string appName, long downDelta, long upDelta)
    {
        if (string.IsNullOrWhiteSpace(appName) || (downDelta == 0 && upDelta == 0)) return;
        try
        {
            lock (_lock)
            {
                var days = Days;
                if (!days.TryGetValue(Today, out var day))
                    days[Today] = day = new DayUsage { Date = Today };
                day.AppDown.TryGetValue(appName, out long d);
                day.AppUp.TryGetValue(appName, out long u);
                day.AppDown[appName] = d + Math.Max(0, downDelta);
                day.AppUp[appName] = u + Math.Max(0, upDelta);
            }
        }
        catch (Exception ex) { App.LogCrash(ex); }
    }

    public void Save()
    {
        lock (_lock)
        {
            if (_days == null) return;
            Storage.Save("usage.json", _days.Values.OrderByDescending(d => d.Date).ToList());
        }
    }

    public DayUsage GetToday()
    {
        lock (_lock)
        {
            var days = Days;
            return days.TryGetValue(Today, out var d) ? d : new DayUsage { Date = Today };
        }
    }

    /// <summary>Last N days, oldest first (gaps filled with empty days).</summary>
    public List<DayUsage> GetRange(int n)
    {
        lock (_lock)
        {
            var days = Days;
            var list = new List<DayUsage>();
            for (int i = n - 1; i >= 0; i--)
            {
                string key = DateTime.Now.AddDays(-i).ToString("yyyy-MM-dd");
                list.Add(days.TryGetValue(key, out var d) ? d : new DayUsage { Date = key });
            }
            return list;
        }
    }

    public List<DayUsage> GetWeek() => GetRange(7);
    public List<DayUsage> GetMonth() => GetRange(30);

    /// <summary>Aggregated per-app totals over the given days, sorted by usage.</summary>
    public List<KeyValuePair<string, long>> GetTopApps(int days, int take = 8)
    {
        lock (_lock)
        {
            var agg = new Dictionary<string, long>();
            var range = GetRange(days);
            foreach (var d in range)
            {
                foreach (var kv in d.AppDown)
                    agg[kv.Key] = agg.TryGetValue(kv.Key, out var v) ? v + kv.Value : kv.Value;
                foreach (var kv in d.AppUp)
                    agg[kv.Key] = agg.TryGetValue(kv.Key, out var v) ? v + kv.Value : kv.Value;
            }
            return agg.OrderByDescending(kv => kv.Value).Take(take).ToList();
        }
    }
}
