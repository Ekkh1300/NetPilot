package com.netpilot.mobile.net

import kotlin.math.roundToInt

/**
 * Network health score (0-100) — pure arithmetic, no Android types, so `HealthTest` can
 * pin every curve.
 *
 * Three rules keep the number honest instead of pessimistic, which is what the old score
 * got wrong (it sat around 50% on a perfectly ordinary connection):
 *
 *  1. **A component nobody measured is left out**, not scored as if it were bad. The old
 *     code handed "no sample yet" a 60/100 — so the first minute of use was capped at
 *     ~85 before anything had even been measured. The remaining weights are
 *     renormalised instead, so four measured components and two both add to 100.
 *  2. **The curves start where real networks start.** A 150 ms DNS lookup or a 90 ms ping
 *     (ordinary on mobile data, behind a VPN, or on a busy 2.4 GHz channel) used to cost
 *     a third of their component; now those are "good", and only genuinely poor values
 *     score low. Latency and loss move together on purpose: one hiccup in twenty probes
 *     (5% loss on a 20-sample window) is a blip, not a broken network.
 *  3. **Fewer than three measured components means the number is guesswork**, so it is
 *     held back to [PARTIAL_CAP] rather than proudly reporting 100% at startup.
 *
 * Loss is passed as `NaN` when there is no probe window at all — "0% loss" measured from
 * zero samples would be a claim, not a measurement.
 */

/** A component that has not been measured. Excluded from the total rather than penalised. */
const val NO_SAMPLE = -1

/** With fewer than this many measured components the score is capped to [PARTIAL_CAP]. */
const val CONFIDENT_COMPONENTS = 3

/** Ceiling for a score built on too little evidence to be trusted. */
const val PARTIAL_CAP = 60

private const val W_LATENCY = 0.30
private const val W_DNS = 0.25
private const val W_LOSS = 0.25
private const val W_STABILITY = 0.20

/** The four component scores plus the assembled total. */
data class HealthParts(
    val score: Int,
    val dnsScore: Int,
    val latencyScore: Int,
    val lossScore: Int,
    val stabilityScore: Int,
    val online: Boolean,
) {
    /** How many components actually carry a measurement. */
    val known: Int
        get() = listOf(dnsScore, latencyScore, lossScore, stabilityScore).count { it != NO_SAMPLE }
}

/** Full recursive query against the configured resolver. */
fun rateDns(ms: Int): Int = when {
    ms < 0 -> NO_SAMPLE
    ms <= 50 -> 100
    ms <= 100 -> 92
    ms <= 200 -> 85
    ms <= 400 -> 70
    ms <= 800 -> 50
    else -> 30
}

/** Round trip of a TCP connect to a nearby anycast address. */
fun rateLatency(ms: Int): Int = when {
    ms < 0 -> NO_SAMPLE
    ms <= 25 -> 100
    ms <= 50 -> 95
    ms <= 80 -> 88
    ms <= 120 -> 80
    ms <= 200 -> 68
    ms <= 350 -> 48
    ms <= 600 -> 28
    else -> 12
}

/** `NaN` means the probe window does not exist yet. */
fun rateLoss(pct: Double): Int = when {
    pct.isNaN() -> NO_SAMPLE
    pct <= 0.0 -> 100
    pct <= 1.0 -> 96
    pct <= 2.0 -> 90
    pct <= 5.0 -> 72
    pct <= 10.0 -> 50
    pct <= 20.0 -> 30
    else -> 10
}

/**
 * Flaps seen in the last minute. A single blip (handover, doze, a router rebroadcasting)
 * is normal on a phone and used to cost 30 points — three quarters of this component,
 * and with the old weight nearly 8 points off the total for one hiccup.
 */
fun rateStability(recentChanges: Int): Int = when {
    recentChanges <= 0 -> 100
    recentChanges == 1 -> 92
    recentChanges == 2 -> 82
    recentChanges == 3 -> 70
    recentChanges == 4 -> 60
    else -> 50
}

/**
 * Assembles the score from whatever was actually measured.
 *
 * @param online the link is up at all; a disconnected link scores 0 regardless of the
 *   measurements still held from before it dropped.
 * @param dnsMs resolver time in ms, or a negative value when there is no sample.
 * @param avgMs average round trip in ms, or a negative value when there is no sample.
 * @param lossPct failed probes as a percentage, or `NaN` when no probe window exists.
 * @param recentLinkChanges link flaps inside the last minute.
 */
fun scoreHealth(
    online: Boolean,
    dnsMs: Int,
    avgMs: Int,
    lossPct: Double,
    recentLinkChanges: Int,
): HealthParts {
    val dns = rateDns(dnsMs)
    val latency = rateLatency(avgMs)
    val loss = rateLoss(lossPct)
    val stability = rateStability(recentLinkChanges)

    var weight = 0.0
    var total = 0.0
    fun add(w: Double, v: Int) {
        if (v != NO_SAMPLE) {
            weight += w
            total += w * v
        }
    }
    add(W_DNS, dns)
    add(W_LATENCY, latency)
    add(W_LOSS, loss)
    add(W_STABILITY, stability)

    var score = if (weight <= 0.0) 0.0 else total / weight
    if (listOf(dns, latency, loss, stability).count { it != NO_SAMPLE } < CONFIDENT_COMPONENTS) {
        score = minOf(score, PARTIAL_CAP.toDouble())
    }
    if (!online) score = 0.0

    return HealthParts(
        score = score.roundToInt().coerceIn(0, 100),
        dnsScore = dns,
        latencyScore = latency,
        lossScore = loss,
        stabilityScore = stability,
        online = online,
    )
}
