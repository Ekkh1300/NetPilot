using System;
using System.IO;
using System.Threading.Tasks;

namespace NetPilot.Services;

/// <summary>
/// Windows Firewall manipulation. Block rules are created/removed rarely (netsh),
/// while the download shaper toggles the rule's Enabled flag via COM (fast, in-process).
/// </summary>
public static class FirewallHelper
{
    private static readonly object _lock = new();
    private static dynamic _policy;

    private static dynamic Policy()
    {
        if (_policy != null) return _policy;
        lock (_lock)
        {
            if (_policy != null) return _policy;
            var t = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            _policy = Activator.CreateInstance(t);
        }
        return _policy;
    }

    private static dynamic NewRule(string name, string appPath, bool outbound)
    {
        var t = Type.GetTypeFromProgID("HNetCfg.FWRule");
        dynamic rule = Activator.CreateInstance(t);
        rule.Name = name;
        rule.Description = "NetPilot network control";
        rule.ApplicationName = appPath;
        rule.Direction = outbound ? 2 : 1; // 2=out, 1=in
        rule.Action = 0;                   // 0 = block
        rule.Protocol = 256;               // all
        rule.Enabled = false;
        rule.Profiles = 0x7FFFFFFF;        // all profiles
        rule.Grouping = "NetPilot";
        return rule;
    }

    public static bool AddBlockRules(string appPath, out string outName, out string inName)
    {
        outName = RuleName(appPath, "out");
        inName = RuleName(appPath, "in");
        try
        {
            var rules = Policy().Rules;
            RemoveByName(outName); RemoveByName(inName);
            rules.Add(NewRule(appPath != null ? outName : outName, appPath, true));
            rules.Add(NewRule(inName, appPath, false));
            return true;
        }
        catch (Exception ex)
        {
            App.LogCrash(ex);
            return false;
        }
    }

    public static void RemoveRulesFor(string appPath)
    {
        RemoveByName(RuleName(appPath, "out"));
        RemoveByName(RuleName(appPath, "in"));
    }

    public static void RemoveByName(string ruleName)
    {
        // COM already removes every rule with this name, so netsh is only a fallback.
        // Spawning it unconditionally cost up to 5s per removal (two per rule) and it ran
        // on the caller's thread - usually the UI thread.
        try
        {
            var rules = Policy().Rules;
            try
            {
                if (rules.Item(ruleName) == null) return; // nothing to remove
            }
            catch { return; }                             // not present
            rules.Remove(ruleName);
            return;
        }
        catch { /* COM unavailable -> fall back to netsh below */ }

        try
        {
            // `using`: this path runs on the 15s schedule timer, and a Process holds a live
            // kernel handle until it is disposed (or finalised), so the fallback leaked one
            // handle per stale rule per tick.
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "netsh.exe",
                Arguments = $"advfirewall firewall delete rule name=\"{ruleName}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
            });
            p?.WaitForExit(5000);
        }
        catch { }
    }

    /// <summary>Enables/disables an existing block rule (used by the download shaper).</summary>
    public static void SetRuleEnabled(string ruleName, bool enabled)
    {
        try
        {
            dynamic rule = Policy().Rules.Item(ruleName);
            rule.Enabled = enabled;
        }
        catch { /* rule missing; ignore */ }
    }

    public static bool RuleExists(string ruleName)
    {
        try { dynamic r = Policy().Rules.Item(ruleName); return r != null; }
        catch { return false; }
    }

    public static string RuleName(string appPath, string dir)
    {
        string key = string.IsNullOrEmpty(appPath) ? "any" :
            Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
                System.Text.Encoding.UTF8.GetBytes(appPath.ToLowerInvariant()))[..8]).ToLowerInvariant();
        return $"NetPilot_{dir}_{key}";
    }

    /// <summary>Adds an allow rule (used when a Limit/Block rule is removed for a system app).</summary>
    public static void AddAllowRule(string name, string appPath)
    {
        try
        {
            var t = Type.GetTypeFromProgID("HNetCfg.FWRule");
            dynamic rule = Activator.CreateInstance(t);
            rule.Name = name;
            rule.ApplicationName = appPath;
            rule.Direction = 2;
            rule.Action = 2; // allow
            rule.Protocol = 256;
            rule.Enabled = true;
            rule.Profiles = 0x7FFFFFFF;
            rule.Grouping = "NetPilot";
            Policy().Rules.Add(rule);
        }
        catch { }
    }
}
