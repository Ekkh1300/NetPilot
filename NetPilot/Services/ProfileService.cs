using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NetPilot.Models;

namespace NetPilot.Services;

/// <summary>Profiles bundle a DNS choice + net limits (+blocked apps) into one switchable preset.</summary>
public static class ProfileService
{
    private static readonly object _lock = new();
    private static List<Profile> _profiles;

    public static List<Profile> GetAll()
    {
        lock (_lock)
        {
            _profiles ??= Storage.Load("profiles.json", new List<Profile>());
            return _profiles.Select(Clone).ToList();
        }
    }

    private static void Save() { lock (_lock) Storage.Save("profiles.json", _profiles); }

    public static void EnsureDefaults()
    {
        lock (_lock)
        {
            _profiles ??= Storage.Load("profiles.json", new List<Profile>());
            if (_profiles.Count > 0) return;

            var cloudflare = DnsService.Presets.FirstOrDefault(p => p.Name == "Cloudflare");
            var adguard = DnsService.Presets.FirstOrDefault(p => p.Name == "AdGuard DNS");

            _profiles.Add(new Profile
            {
                Name = "Gaming",
                Description = "Low latency DNS, no limits",
                DnsEntryId = cloudflare?.Id ?? "",
                Rules = new List<LimitRule>(),
                IsDefault = true,
                Glyph = "\uE7FC",
                Color = "#F472B6",
            });
            _profiles.Add(new Profile
            {
                Name = "Browsing",
                Description = "Ad-blocking DNS, light limits",
                DnsEntryId = adguard?.Id ?? "",
                Rules = new List<LimitRule>(),
                IsDefault = true,
                Glyph = "\uE774",
                Color = "#38BDF8",
            });
            _profiles.Add(new Profile
            {
                Name = "Default",
                Description = "No DNS change, no limits",
                DnsEntryId = "",
                Rules = new List<LimitRule>(),
                IsDefault = true,
                Glyph = "\uE736",
                Color = "#818CF8",
            });
            Save();
        }
    }

    public static void Save(Profile profile)
    {
        lock (_lock)
        {
            var list = _profiles ??= Storage.Load("profiles.json", new List<Profile>());
            var existing = list.FirstOrDefault(p => p.Id == profile.Id);
            if (existing != null) list.Remove(existing);
            list.Add(profile);
            Save();
        }
    }

    public static void Delete(string id)
    {
        lock (_lock)
        {
            var p = _profiles?.FirstOrDefault(x => x.Id == id);
            if (p == null) return;
            if (p.IsDefault) return; // defaults are protected
            _profiles.Remove(p);
            Save();
        }
    }

    /// <summary>Creates a profile from the current system state (current DNS + active rules).
    /// Async: reading the current DNS spawns PowerShell, which used to block the UI thread.</summary>
    public static async Task<Profile> CreateFromCurrentAsync(string name)
    {
        var currentServers = (await DnsService.GetCurrentAsync().ConfigureAwait(false))
            .FirstOrDefault(s => s.V4.Count > 0)?.V4 ?? new List<string>();
        var match = DnsService.FindMatching(currentServers);
        return new Profile
        {
            Name = name,
            Description = "Custom profile",
            DnsEntryId = match?.Id ?? "",
            Rules = LimiterService.Instance.GetRules().Select(r => new LimitRule
            {
                AppPath = r.AppPath, AppName = r.AppName, Mode = r.Mode,
                DownLimitBps = r.DownLimitBps, UpLimitBps = r.UpLimitBps, Enabled = r.Enabled,
            }).ToList(),
            Glyph = "\uE736",
            Color = "#34D399",
        };
    }

    public static async Task ApplyAsync(Profile profile)
    {
        // 1) DNS
        if (!string.IsNullOrEmpty(profile.DnsEntryId))
        {
            var entry = DnsService.GetAll().FirstOrDefault(d => d.Id == profile.DnsEntryId);
            if (entry != null)
            {
                var states = await DnsService.GetCurrentAsync();
                int ifIndex = states.FirstOrDefault()?.IfIndex ?? 0;
                if (ifIndex > 0)
                    await DnsService.ApplyAsync(entry, ifIndex);
            }
        }

        // 2) Net limits (replace the whole set with the profile's rules)
        LimiterService.Instance.SetRules(profile.Rules.Select(r => new LimitRule
        {
            AppPath = r.AppPath, AppName = r.AppName, Mode = r.Mode,
            DownLimitBps = r.DownLimitBps, UpLimitBps = r.UpLimitBps, Enabled = r.Enabled,
        }).ToList());

        // 3) Remember active profile
        SettingsService.Current.ActiveProfileId = profile.Id;
        SettingsService.Save();

        HistoryService.Add("profile_applied", "Profile Applied", profile.Name);
    }

    private static Profile Clone(Profile p) => new()
    {
        Id = p.Id, Name = p.Name, Description = p.Description,
        DnsEntryId = p.DnsEntryId, Rules = p.Rules, IsDefault = p.IsDefault,
        Glyph = p.Glyph, Color = p.Color,
    };
}
