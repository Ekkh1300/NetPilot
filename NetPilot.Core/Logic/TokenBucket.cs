using System;
using System.Collections.Generic;

namespace NetPilot.Services;

/// <summary>
/// Token bucket used to shape an application's download.
///
/// The Windows implementation shaped downloads by toggling a firewall drop rule every 500 ms
/// based on real per-process traffic. Linux and macOS shape the same way - they have no
/// per-process upload throttle that works without a root kernel module, and download shaping
/// by dropping is portable. So the *decision* is shared here and only the "how do I drop"
/// part stays per-platform, which is the part that actually differs.
///
/// Semantics, unchanged from the original Windows code:
///   - the bucket is capped at twice the limit, so a burst of up to 2x is allowed and a
///     stalled transfer cannot bank credit for later;
///   - a tick that overruns is charged the elapsed time, not the timer period;
///   - going over budget empties the bucket and blocks, it does not go negative.
/// </summary>
public sealed class TokenBucket
{
    private readonly Dictionary<string, double> _tokens = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Charge every app that has a rule against its bucket and report who is over budget.</summary>
    /// <param name="apps">observed traffic, one entry per process</param>
    /// <param name="limitBps">per-app limit in bytes/second</param>
    /// <param name="seconds">elapsed since the previous tick</param>
    /// <returns>true when the app is over budget this tick and should be blocked</returns>
    public bool IsOverBudget(string appKey, double limitBps, double seconds, IReadOnlyList<(string Key, double DownBps)> apps)
    {
        if (limitBps <= 0) return false;

        double cap = limitBps * 2;

        // Sum every process of the app, not just the first: Chrome alone runs five processes
        // on the same binary, and charging only the busiest one lets the rule pass ~5x its
        // limit. The key is the app, so all of its processes collapse into one bucket.
        double bytes = 0;
        foreach (var (key, downBps) in apps)
        {
            if (string.Equals(key, appKey, StringComparison.OrdinalIgnoreCase))
                bytes += downBps * seconds;
        }

        double tokens;
        _tokens.TryGetValue(appKey, out tokens);
        tokens = Math.Min(cap, tokens + limitBps * seconds);

        bool blocked;
        if (bytes > tokens) { tokens = 0; blocked = true; }
        else { tokens -= bytes; blocked = false; }

        _tokens[appKey] = Math.Max(0, tokens);
        return blocked;
    }

    /// <summary>Forget an app's bucket (rule removed, or it should start fresh).</summary>
    public void Forget(string appKey) => _tokens.Remove(appKey);

    /// <summary>Forget every bucket - used when enforcement restarts, so no stale credit survives.</summary>
    public void Clear() => _tokens.Clear();

    public double Peek(string appKey) => _tokens.TryGetValue(appKey, out var v) ? v : 0;
}