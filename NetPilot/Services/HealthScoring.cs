using System;
using System.Collections.Generic;

namespace NetPilot.Services;

/// <summary>
/// The network health score (0-100) as pure arithmetic: no service, no timer, no I/O, so
/// the curves can be asserted by <see cref="SelfTest"/> without a network or a window.
///
/// Three rules keep the number honest instead of pessimistic — the score used to sit
/// around 50% on a perfectly ordinary connection:
///
///  1. A component nobody measured is *left out* and the remaining weights are
///     renormalised. Handing "no sample yet" a fixed low value meant the first minute of
///     use was capped before anything had been measured.
///  2. The curves start where real networks start. A 200 ms DNS lookup or a 120 ms ping
///     — unremarkable over Wi-Fi, a hotspot or behind a VPN — used to cost a third of
///     their component; now those read "good" and only genuinely poor values read poor.
///     Loss follows the probe window: MonitorService keeps 20 samples, so one dropped
///     reply is 5%, a blip rather than a disaster.
///  3. Fewer than <see cref="ConfidentComponents"/> measured components means the number
///     is guesswork, so it is held back to <see cref="PartialCap"/> instead of proudly
///     reporting 100% at startup.
///
/// Unknown is <see cref="Unknown"/> (-1) everywhere, matching the -1 the model already
/// uses for "no reading". <see cref="FailedDns"/> is narrower: a resolver query that was
/// actually attempted and failed, which is a real signal and is scored poorly rather
/// than skipped.
/// </summary>
public static class HealthScoring
{
    /// <summary>No measurement exists for this component.</summary>
    public const int Unknown = -1;

    /// <summary>The component was measured and the measurement failed.</summary>
    public const int FailedDns = -2;

    public const double WLatency = 0.30;
    public const double WDns = 0.25;
    public const double WLoss = 0.25;
    public const double WStability = 0.20;

    /// <summary>Below this many measured components the score is capped, not reported.</summary>
    public const int ConfidentComponents = 3;

    /// <summary>Ceiling for a score built on too little evidence to be trusted.</summary>
    public const int PartialCap = 60;

    // ------------------------------------------------------------------ components

    /// <summary>Full recursive query against the configured resolver.</summary>
    public static double RateDns(double ms)
    {
        if (ms == FailedDns) return 30;   // attempted and failed: a real signal, not a blank
        if (ms <= 0) return Unknown;
        return ms switch { <= 50 => 100, <= 100 => 92, <= 200 => 85, <= 400 => 70, <= 800 => 50, _ => 30 };
    }

    /// <summary>Round trip to the monitor target; 0 means the reply never came.</summary>
    public static double RateLatency(double ms) =>
        ms <= 0 ? Unknown
                : ms switch { <= 25 => 100, <= 50 => 95, <= 80 => 88, <= 120 => 80, <= 200 => 68, <= 350 => 48, <= 600 => 28, _ => 12 };

    /// <summary>Percentage of the last 20 probes that were dropped.</summary>
    public static double RateLoss(double pct) =>
        double.IsNaN(pct) || pct < 0 ? Unknown
                                     : pct <= 0 ? 100 : pct <= 1 ? 96 : pct <= 2 ? 90 : pct <= 5 ? 72 : pct <= 10 ? 50 : pct <= 20 ? 30 : 10;

    /// <summary>Share of the last minute of samples that were online, as a percentage.</summary>
    public static double RateStability(double pct) =>
        double.IsNaN(pct) || pct < 0 ? Unknown : Math.Clamp(pct, 0, 100);

    // -------------------------------------------------------------------- assembly

    /// <summary>
    /// Weighted average of the components that were actually measured, capped when too
    /// few of them were. Offline handling stays with the caller: "offline" means
    /// something different for a link that is definitionally down than for a probe that
    /// merely stopped answering.
    /// </summary>
    public static double Score(double dnsMs, double pingMs, double lossPct, double stabilityPct)
    {
        double dns = RateDns(dnsMs);
        double lat = RateLatency(pingMs);
        double loss = RateLoss(lossPct);
        double stab = RateStability(stabilityPct);

        double weight = 0, total = 0;
        void Add(double w, double v)
        {
            if (v == Unknown) return;
            weight += w;
            total += w * v;
        }

        Add(WDns, dns);
        Add(WLatency, lat);
        Add(WLoss, loss);
        Add(WStability, stab);

        double score = weight <= 0 ? 0 : total / weight;
        int known = (dns != Unknown ? 1 : 0) + (lat != Unknown ? 1 : 0)
                  + (loss != Unknown ? 1 : 0) + (stab != Unknown ? 1 : 0);
        if (known < ConfidentComponents) score = Math.Min(score, PartialCap);

        return Math.Clamp(score, 0, 100);
    }

    /// <summary>Component score → the label shown next to it; a blank component reads "—".</summary>
    public static string Rating(double score) =>
        score == Unknown ? "—"
                         : score >= 85 ? "Excellent" : score >= 65 ? "Good" : score >= 40 ? "Fair" : "Poor";

    // --------------------------------------------------------------------- selftest

    /// <summary>
    /// Asserts the curves against the situations they exist to judge. Returns the list of
    /// failures — empty means every expectation held. Run with `NetPilot.exe --healthtest`
    /// (writes healthcheck.txt), which is how this side of the project checks itself: the
    /// math is pure, so it does not need a phone, a screenshot or a live network.
    /// </summary>
    public static List<string> SelfTest()
    {
        var fail = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            if (!ok) fail.Add(name + (detail.Length > 0 ? " (" + detail + ")" : ""));
        }

        // Ordinary, healthy conditions must read as healthy — this is the whole complaint.
        double wifi = Score(dnsMs: 60, pingMs: 30, lossPct: 0, stabilityPct: 100);
        Check("healthy Wi-Fi below 90", wifi >= 90, wifi.ToString("0"));

        double mobile = Score(150, 95, 0, 100);
        Check("mobile/VPN below 80", mobile >= 80, mobile.ToString("0"));

        // The exact shape that produced ~56% under the old curves.
        double ordinary = Score(180, 120, 4, 95);
        Check("180ms/120ms/4% below 75", ordinary >= 75, ordinary.ToString("0"));

        // One dropped reply in twenty is a blip.
        double blip = Score(60, 30, 5, 100);
        Check("5% loss below 85", blip >= 85, blip.ToString("0"));

        // …but a genuinely bad connection must still be called bad.
        double bad = Score(900, 500, 25, 60);
        Check("broken network above 40", bad <= 40, bad.ToString("0"));

        // Not enough evidence → capped, never a confident 100.
        double cold = Score(-1, 0, 0, -1);
        Check("cold start not capped", cold == PartialCap, cold.ToString("0"));

        double partial = Score(40, -1, -1, 100);
        Check("two components not capped", partial == PartialCap, partial.ToString("0"));

        double three = Score(60, 30, -1, 100);
        Check("three components still capped", three > PartialCap, three.ToString("0"));

        // Excluded ≠ penalised: not knowing DNS must beat a measured 400 ms lookup.
        double noDns = Score(-1, 30, 0, 100);
        double poorDns = Score(400, 30, 0, 100);
        Check("unknown DNS scored below a measured bad one", noDns >= poorDns, noDns + " vs " + poorDns);

        // A failed resolver query is a signal, not a blank.
        Check("failed DNS not scored", RateDns(FailedDns) == 30, RateDns(FailedDns).ToString("0"));
        Check("failed DNS indistinguishable from never probed", RateDns(Unknown) == Unknown);

        // Monotone: more damage never reads as a better network.
        var lats = new double[] { 10, 30, 60, 100, 150, 250, 400, 700 };
        for (int i = 1; i < lats.Length; i++)
        {
            double prev = Score(60, lats[i - 1], 0, 100);
            double cur = Score(60, lats[i], 0, 100);
            Check($"latency {lats[i - 1]}→{lats[i]} improved", cur <= prev, prev + "→" + cur);
        }

        var losses = new double[] { 0, 0.5, 1, 3, 6, 12, 25, 60 };
        for (int i = 1; i < losses.Length; i++)
        {
            double prev = Score(60, 30, losses[i - 1], 100);
            double cur = Score(60, 30, losses[i], 100);
            Check($"loss {losses[i - 1]}→{losses[i]} improved", cur <= prev, prev + "→" + cur);
        }

        // Labels keep their established boundaries, and a blank component reads as one.
        Check("85 is not Excellent", Rating(85) == "Excellent", Rating(85));
        Check("64 is not Fair", Rating(64) == "Fair", Rating(64));
        Check("unknown rating not a dash", Rating(Unknown) == "—", Rating(Unknown));

        return fail;
    }
}
