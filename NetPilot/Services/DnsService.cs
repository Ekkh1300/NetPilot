using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using NetPilot.Models;

namespace NetPilot.Services;

/// <summary>DNS presets, custom DNS, apply / restore with backup.</summary>
public static class DnsService
{
    public static List<DnsEntry> Presets { get; } = new()
    {
        new() { Name = "Cloudflare", ServersV4 = new() { "1.1.1.1", "1.0.0.1" },
                 ServersV6 = new() { "2606:4700:4700::1111", "2606:4700:4700::1001" },
                 Description = "Fast & privacy focused", Color = "#F6821F", Glyph = "\uE968" },
        new() { Name = "Google DNS", ServersV4 = new() { "8.8.8.8", "8.8.4.4" },
                 ServersV6 = new() { "2001:4860:4860::8888", "2001:4860:4860::8844" },
                 Description = "Reliable global DNS", Color = "#4285F4", Glyph = "\uE80F" },
        new() { Name = "Quad9", ServersV4 = new() { "9.9.9.9", "149.112.112.112" },
                 ServersV6 = new() { "2620:fe::fe", "2620:fe::9" },
                 Description = "Security blocking malware", Color = "#1B365D", Glyph = "\uE72E" },
        new() { Name = "OpenDNS", ServersV4 = new() { "208.67.222.222", "208.67.220.220" },
                 Description = "Cisco OpenDNS", Color = "#00A0DF", Glyph = "\uE968" },
        new() { Name = "AdGuard DNS", ServersV4 = new() { "94.140.14.14", "94.140.15.15" },
                 Description = "Blocks ads & trackers", Color = "#67B279", Glyph = "\uE710" },
        new() { Name = "CleanBrowsing", ServersV4 = new() { "185.228.168.9", "185.228.169.9" },
                 Description = "Family safe filtering", Color = "#8B5CF6", Glyph = "\uE72E" },
        new() { Name = "dns0.eu", ServersV4 = new() { "193.110.81.0", "185.253.5.0" },
                 Description = "European non-profit DNS", Color = "#10B981", Glyph = "\uE968" },
        new() { Name = "Comodo DNS", ServersV4 = new() { "8.26.56.26", "8.20.247.20" },
                 Description = "Secure DNS by Comodo", Color = "#1E40AF", Glyph = "\uE72E" },
        new() { Name = "Level3", ServersV4 = new() { "4.2.2.1", "4.2.2.2" },
                 Description = "CenturyLink backbone", Color = "#64748B", Glyph = "\uE968" },
    };

    private static List<DnsEntry> _custom;
    private static readonly object _lock = new();

    public static List<DnsEntry> GetCustom()
    {
        lock (_lock)
        {
            _custom ??= Storage.Load("dns_custom.json", new List<DnsEntry>());
            return _custom.ToList();
        }
    }

    public static void SaveCustom(List<DnsEntry> list)
    {
        lock (_lock) { _custom = list.ToList(); Storage.Save("dns_custom.json", _custom); }
    }

    public static List<DnsEntry> GetAll()
    {
        var favs = new HashSet<string>(SettingsService.Current.FavoriteDnsIds);
        var all = Presets.Concat(GetCustom()).ToList();
        foreach (var d in all) d.IsFavorite = favs.Contains(d.Id);
        return all;
    }

    public static void ToggleFavorite(DnsEntry entry)
    {
        var s = SettingsService.Current;
        if (s.FavoriteDnsIds.Contains(entry.Id)) s.FavoriteDnsIds.Remove(entry.Id);
        else s.FavoriteDnsIds.Add(entry.Id);
        SettingsService.Save();
    }

    public static DnsEntry FindMatching(IEnumerable<string> servers)
    {
        var set = new HashSet<string>(servers.Where(s => Uri.CheckHostName(s) == UriHostNameType.IPv4),
                                      StringComparer.OrdinalIgnoreCase);
        if (set.Count == 0) return null;
        return GetAll().FirstOrDefault(d => d.ServersV4.Count > 0 && set.SetEquals(d.ServersV4));
    }

    // ---------------- Current DNS ----------------

    public static async Task<List<AdapterDnsState>> GetCurrentAsync()
    {
        const string script = @"
Get-NetAdapter | Where-Object { $_.Status -eq 'Up' } | ForEach-Object {
  $i = $_.ifIndex
  $v4 = @((Get-DnsClientServerAddress -InterfaceIndex $i -AddressFamily IPv4 -ErrorAction SilentlyContinue).ServerAddresses)
  $v6 = @((Get-DnsClientServerAddress -InterfaceIndex $i -AddressFamily IPv6 -ErrorAction SilentlyContinue).ServerAddresses)
  [pscustomobject]@{ IfIndex=[int]$i; AdapterName=$_.Name; V4=($v4 -join '|'); V6=($v6 -join '|'); IsDynamic=($v4.Count -eq 0 -or $v4 -contains '0.0.0.0') }
} | ConvertTo-Json -Compress";
        var (ok, output) = await Sys.PsStdoutAsync(script, 20000);
        var result = new List<AdapterDnsState>();
        if (!ok || string.IsNullOrWhiteSpace(output)) return result;
        if (!Sys.TryExtractJson(output, out var json)) return result;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var items = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray() : new[] { root }.AsEnumerable();
            foreach (var it in items)
            {
                var st = new AdapterDnsState
                {
                    IfIndex = it.GetProperty("IfIndex").GetInt32(),
                    AdapterName = it.TryGetProperty("AdapterName", out var an) ? an.GetString() : "",
                    IsDynamic = it.TryGetProperty("IsDynamic", out var id) && id.ValueKind == JsonValueKind.True,
                };
                if (it.TryGetProperty("V4", out var v4) && v4.ValueKind == JsonValueKind.String)
                {
                    var s = v4.GetString();
                    if (!string.IsNullOrWhiteSpace(s) && s != "0.0.0.0")
                        st.V4.AddRange(s.Split('|', StringSplitOptions.RemoveEmptyEntries));
                }
                if (it.TryGetProperty("V6", out var v6) && v6.ValueKind == JsonValueKind.String)
                {
                    var s = v6.GetString();
                    if (!string.IsNullOrWhiteSpace(s) && s != "::")
                        st.V6.AddRange(s.Split('|', StringSplitOptions.RemoveEmptyEntries));
                }
                result.Add(st);
            }
        }
        catch (Exception ex) { App.LogCrash(ex); }
        return result;
    }

    // ---------------- Backup / Apply / Restore ----------------

    public class DnsBackup
    {
        public Dictionary<int, List<string>> Adapters { get; set; } = new(); // IPv4 per adapter
        public Dictionary<int, List<string>> V6 { get; set; } = new();        // IPv6 per adapter
    }

    private static DnsBackup LoadBackup() => Storage.Load("dns_backup.json", new DnsBackup());
    private static void SaveBackup(DnsBackup b) => Storage.Save("dns_backup.json", b);
    public static bool HasBackup() => LoadBackup().Adapters.Count > 0;

    private static async Task<bool> SetServersAsync(int ifIndex, List<string> v4, List<string> v6)
    {
        // Set-DnsClientServerAddress has no -AddressFamily parameter (that flag used to be
        // appended and always failed with a binding error, so IPv6-only entries applied
        // nothing and mixed entries reported a failure after applying the v4 part).
        // It takes both families in a single -ServerAddresses array.
        var all = new List<string>();
        if (v4 != null) all.AddRange(v4);
        if (v6 != null) all.AddRange(v6);

        string set = $"Set-DnsClientServerAddress -InterfaceIndex {ifIndex} -ServerAddresses ('{string.Join("','", all)}')";
        const string reset = "ResetServerAddresses";

        string script;
        if (all.Count == 0)
            script = $"Set-DnsClientServerAddress -InterfaceIndex {ifIndex} -{reset}";
        else if (all.Count == (v4?.Count ?? 0) || all.Count == (v6?.Count ?? 0))
            // Exactly one family has servers. Reset first: if the other family was
            // originally automatic it must end up automatic again, whatever the cmdlet
            // does with a family that is absent from -ServerAddresses.
            script = $"Set-DnsClientServerAddress -InterfaceIndex {ifIndex} -{reset}; {set}";
        else
            script = set;

        var (ok, err) = await Sys.PsAsync(script, 20000);
        return ok;
    }

    /// <summary>Applies a DNS entry to one adapter (backs up original DNS first).</summary>
    public static async Task<(bool ok, string message)> ApplyAsync(DnsEntry entry, int ifIndex)
    {
        var current = await GetCurrentAsync();
        AdapterDnsState target;
        if (ifIndex > 0)
        {
            // Never fall back to another adapter: silently writing DNS onto a different NIC
            // than the one the user picked is worse than reporting the adapter is missing.
            target = current.FirstOrDefault(a => a.IfIndex == ifIndex);
            if (target == null) return (false, "selected adapter is not active");
        }
        else
        {
            target = current.FirstOrDefault();
            if (target == null) return (false, "no active adapter");
        }

        // Backup original servers only once per adapter so Restore brings back the real original.
        var backup = LoadBackup();
        if (!backup.Adapters.ContainsKey(target.IfIndex))
        {
            backup.Adapters[target.IfIndex] = target.V4.ToList();
            backup.V6 ??= new Dictionary<int, List<string>>();
            backup.V6[target.IfIndex] = target.V6?.ToList() ?? new List<string>();
        }
        SaveBackup(backup);

        bool ok = await SetServersAsync(target.IfIndex, entry.ServersV4, entry.ServersV6);
        if (!ok) return (false, "Set-DnsClientServerAddress failed");

        await Sys.FlushDnsCacheAsync();
        HistoryService.Add("dns_changed", Lang.Lang.IsFa ? "تغییر DNS" : "DNS Changed",
            $"{target.AdapterName}: {entry.Name} ({entry.ServersSummary})");
        return (true, entry.Name);
    }

    /// <summary>Restores original DNS servers for every adapter that has a backup.</summary>
    public static async Task<(bool ok, string message)> RestoreAsync()
    {
        var backup = LoadBackup();
        if (backup.Adapters.Count == 0) return (false, "no backup");

        int done = 0;
        foreach (var kv in backup.Adapters)
        {
            List<string> v6 = null;
            if (backup.V6 != null) backup.V6.TryGetValue(kv.Key, out v6);
            v6 ??= new List<string>();

            bool ok = kv.Value.Count == 0 && v6.Count == 0
                ? await SetServersAsync(kv.Key, new List<string>(), new List<string>())
                : await SetServersAsync(kv.Key, kv.Value, v6);
            if (ok) done++;
        }
        await Sys.FlushDnsCacheAsync();

        // Keep whatever failed so the user can retry - dropping the whole backup after a
        // partial failure made the remaining adapters impossible to restore at all.
        if (done == backup.Adapters.Count) SaveBackup(new DnsBackup());

        HistoryService.Add("dns_restored", Lang.Lang.IsFa ? "بازگردانی DNS" : "DNS Restored",
            $"{done} adapter(s) restored to automatic/original DNS");
        return (done > 0, $"{done}/{backup.Adapters.Count}");
    }

    /// <summary>Human readable description of the current system DNS.</summary>
    public static async Task<string> DescribeCurrentAsync()
    {
        var states = await GetCurrentAsync();
        var active = states.FirstOrDefault(s => s.V4.Count > 0);
        if (active == null) return "Automatic (DHCP)";
        var match = FindMatching(active.V4);
        if (match != null) return $"{match.Name} — {active.Summary}";
        return $"Custom — {active.Summary}";
    }
}
