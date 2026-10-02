using System;
using System.Collections.Generic;

namespace NetPilot.Models;

public class DnsEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; }
    public List<string> ServersV4 { get; set; } = new();
    public List<string> ServersV6 { get; set; } = new();
    public string Description { get; set; } = "";
    public bool IsFavorite { get; set; }
    public bool IsCustom { get; set; }
    public string Glyph { get; set; } = "\uE968";
    public string Color { get; set; } = "#38BDF8";

    public string ServersSummary => string.Join("  •  ", ServersV4);
    public bool MatchesServers(IEnumerable<string> servers)
    {
        var set = new HashSet<string>(servers, StringComparer.OrdinalIgnoreCase);
        return ServersV4.Count > 0 && set.SetEquals(ServersV4);
    }
}

public class AdapterDnsState
{
    public int IfIndex { get; set; }
    public string AdapterName { get; set; } = "";
    public List<string> V4 { get; set; } = new();
    public List<string> V6 { get; set; } = new();
    public bool IsDynamic { get; set; }
    public string Summary =>
        V4.Count == 0 ? "—" : string.Join(", ", V4);
}

public class DnsBenchmarkResult
{
    public DnsEntry Entry { get; set; }
    public List<double> SamplesMs { get; set; } = new();
    public int Rounds { get; set; }
    public int SuccessCount { get; set; }
    public double AvgMs { get; set; }
    public double MinMs { get; set; }
    public double MaxMs { get; set; }
    public double JitterMs { get; set; }
    public double LossPercent { get; set; }
    public double PingAvgMs { get; set; }
    public bool PingOk { get; set; }
    public double Score { get; set; }
    public int Rank { get; set; }
    public DateTime RanAt { get; set; } = DateTime.Now;
    public bool Failed => SuccessCount == 0;

    public void ComputeStats()
    {
        if (SamplesMs.Count > 0)
        {
            AvgMs = SamplesMs.Average();
            MinMs = SamplesMs.Min();
            MaxMs = SamplesMs.Max();
            double mean = AvgMs;
            JitterMs = SamplesMs.Count > 1
                ? Math.Sqrt(SamplesMs.Average(v => (v - mean) * (v - mean)))
                : 0;
        }
        LossPercent = Rounds > 0 ? 100.0 * (Rounds - SuccessCount) / Rounds : 0;
        // Real composite score: response time dominates, then loss, then jitter.
        double timePart = Math.Clamp(100 - AvgMs * 1.2, 0, 100);
        double lossPart = 100 - LossPercent;
        double jitterPart = Math.Clamp(100 - JitterMs * 2.5, 0, 100);
        Score = Failed ? 0 : timePart * 0.6 + lossPart * 0.3 + jitterPart * 0.1;
    }
}

public enum AdapterKind { Ethernet, Wifi, Virtual, Vpn, Other }

public class AdapterInfo
{
    public int IfIndex { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Mac { get; set; } = "";
    public AdapterKind Kind { get; set; }
    public bool IsUp { get; set; }
    public string Status { get; set; } = "";
    public string Ipv4 { get; set; } = "";
    public string Ipv6 { get; set; } = "";
    public string Gateway { get; set; } = "";
    public List<string> DnsServers { get; set; } = new();
    public long LinkSpeedMbps { get; set; }
    public bool IsDhcp { get; set; }
    public string KindGlyph => Kind switch
    {
        AdapterKind.Wifi => "\uE701",
        AdapterKind.Virtual => "\uE774",
        AdapterKind.Vpn => "\uE705",
        _ => "\uE968",
    };
    public string LinkSpeedText => LinkSpeedMbps > 0 ? $"{LinkSpeedMbps} Mbps" : "—";
}
