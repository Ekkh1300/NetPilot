using System;
using System.Collections.Generic;

namespace NetPilot.Models;

public class NetworkSample
{
    public DateTime Ts { get; set; }
    public double DownBps { get; set; }
    public double UpBps { get; set; }
    public double PingMs { get; set; }
    public double LossPct { get; set; }
    public bool Online { get; set; } = true;
}

public class AppNetInfo
{
    public int Pid { get; set; }
    public string ProcessName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Path { get; set; } = "";
    public double DownBps { get; set; }
    public double UpBps { get; set; }
    public long TotalDown { get; set; }
    public long TotalUp { get; set; }
    public int Connections { get; set; }
    public DateTime LastSeen { get; set; } = DateTime.Now;
    public string IconGlyph { get; set; } = "\uE74B";
}

public enum LimitMode { Allow = 0, Limit = 1, Block = 2 }

public class LimitRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string AppPath { get; set; } = "";
    public string AppName { get; set; } = "";
    public LimitMode Mode { get; set; } = LimitMode.Allow;
    /// <summary>Bytes/second. 0 = unlimited.</summary>
    public long DownLimitBps { get; set; }
    public long UpLimitBps { get; set; }
    public bool Enabled { get; set; } = true;
}

public class ScheduleRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string AppPath { get; set; } = "";
    public string AppName { get; set; } = "";
    public TimeSpan Start { get; set; }
    public TimeSpan End { get; set; }
    public long DownLimitBps { get; set; }
    public long UpLimitBps { get; set; }
    public bool BlockInstead { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>Runtime flag: "my override is currently applied in LimiterService".
    /// Never persisted - a restart empties the override table, so a stale flag read back
    /// from disk would make Evaluate skip both branches and leave a schedule stuck on/off.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool ActiveNow { get; set; }

    public bool Contains(DateTime t)
    {
        var now = t.TimeOfDay;
        return Start <= End ? now >= Start && now < End : now >= Start || now < End;
    }
}

public class HistoryEvent
{
    public DateTime Ts { get; set; } = DateTime.Now;
    public string Type { get; set; } = "";
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
}

public class DayUsage
{
    public string Date { get; set; } = ""; // yyyy-MM-dd
    public long Down { get; set; }
    public long Up { get; set; }
    public Dictionary<string, long> AppDown { get; set; } = new();
    public Dictionary<string, long> AppUp { get; set; } = new();
    public long Total => Down + Up;
    public string TopApp
    {
        get
        {
            string best = "";
            long bestVal = -1;
            foreach (var kv in AppDown)
            {
                long v = kv.Value + (AppUp.TryGetValue(kv.Key, out var u) ? u : 0);
                if (v > bestVal) { bestVal = v; best = kv.Key; }
            }
            return bestVal > 0 ? best : "—";
        }
    }
}

public class Profile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>DnsEntry id, or "" to keep current DNS untouched.</summary>
    public string DnsEntryId { get; set; } = "";
    public List<LimitRule> Rules { get; set; } = new();
    public bool IsDefault { get; set; }
    public string Glyph { get; set; } = "\uE736";
    public string Color { get; set; } = "#818CF8";
}

public class HealthSnapshot
{
    public double Score { get; set; }
    public double DnsMs { get; set; }
    public double PingMs { get; set; }
    public double LossPct { get; set; }
    public double StabilityPct { get; set; }
    public bool Online { get; set; } = true;
    public string DnsRating { get; set; } = "—";
    public string LatencyRating { get; set; } = "—";
    public string LossRating { get; set; } = "—";
    public string ConnRating { get; set; } = "—";
}

public class ToolResult
{
    public string Title { get; set; } = "";
    public string Output { get; set; } = "";
    public bool Success { get; set; }
    public DateTime RanAt { get; set; } = DateTime.Now;
}
