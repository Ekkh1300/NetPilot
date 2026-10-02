package com.netpilot.mobile.vpn

import com.netpilot.mobile.data.LimitRule
import com.netpilot.mobile.data.RuleMode
import java.util.concurrent.ConcurrentHashMap

/** What the engine must do with one flow. [Pass] carries the package when the platform
 * identified it, so a live rule change can be re-evaluated without asking again. */
internal sealed class Decision {
    data class Pass(val pkg: String?) : Decision()
    object Block : Decision()
    data class Shape(val pkg: String) : Decision()
}

/** A decision plus whether the platform actually told us which app owns the flow. */
internal data class Outcome(val decision: Decision, val identified: Boolean)

/**
 * Token bucket: [ratePerSec] bytes per second with a bounded burst.
 *
 * Both directions of a limited connection are shaped with one of these; TCP is paced by
 * asking for [available] bytes before writing a segment, UDP is policed with [spend]
 * (a datagram the budget cannot cover is dropped — UDP has no back-pressure to lean on).
 *
 * The clock is injected as nanotime so the host tests can drive it.
 */
internal class TokenBucket(val ratePerSec: Long, burstPerSec: Long = ratePerSec / 4) {

    private val rate = ratePerSec.toDouble()
    private val burst = (if (burstPerSec > 0) burstPerSec else 8_192L).coerceAtLeast(4_096L).toDouble()
    private var tokens = burst
    private var last = 0L

    @Synchronized
    fun available(now: Long): Long {
        refill(now)
        return tokens.toLong()
    }

    /**
     * Pays for [want] bytes and returns how many were actually taken (0 when the bucket
     * cannot cover them).
     *
     * Peeking with [available] and then sending without paying is what made the rate
     * limits unenforceable: the bucket refilled on every call and nothing was ever
     * deducted, so *every* call saw a full burst and the real throughput was
     * "burst x calls per second" instead of the configured rate.
     */
    @Synchronized
    fun reserve(now: Long, want: Long): Long {
        if (want <= 0L) return 0L
        refill(now)
        if (tokens < want) return 0L
        tokens -= want
        return want
    }

    /** Hands unspent bytes back (a partial socket write, a cancelled segment). */
    @Synchronized
    fun refund(now: Long, n: Long) {
        if (n <= 0L) return
        refill(now)
        tokens = kotlin.math.min(burst, tokens + n)
    }

    @Synchronized
    fun spend(n: Long, now: Long): Boolean {
        refill(now)
        if (tokens < n) return false
        tokens -= n
        return true
    }

    private fun refill(now: Long) {
        if (last == 0L) {
            last = now
            return
        }
        val dt = (now - last) / 1_000_000_000.0
        if (dt <= 0.0) return
        last = now
        tokens = kotlin.math.min(burst, tokens + dt * rate)
    }
}

/**
 * Turns the stored rules (plus any schedule that is active right now) into per-flow
 * decisions, and owns the rate-limit buckets.
 *
 * Attribution works by asking the platform for the UID that owns a connection
 * (`ConnectivityManager.getConnectionOwnerUid`, Android 10+) and mapping that UID to a
 * package. When the platform refuses to say, the engine fails **closed** while any block
 * rule exists — but a circuit breaker flips it to permissive and raises
 * [FirewallStats.attributionBroken] instead of blackholing the whole device on a build
 * that never answers.
 *
 * Free of Android types: the service injects the two lookups, the tests inject fakes.
 */
internal class Firewall(
    private val ruleSource: () -> Map<String, LimitRule>,
    private val ownerUid: (proto: Int, srcIp: Int, srcPort: Int, dstIp: Int, dstPort: Int) -> Int,
    private val pkgOf: (uid: Int) -> String?
) {

    @Volatile private var blocked: Set<String> = emptySet()

    /** pkg -> [download bytes/sec, upload bytes/sec]; 0 in a slot means "no cap". */
    @Volatile private var limits: Map<String, LongArray> = emptyMap()

    @Volatile private var anyBlock = false

    private val buckets = ConcurrentHashMap<String, TokenBucket>()

    private val lock = Any()
    private var hits = 0
    private var misses = 0

    init {
        refresh()
    }

    /** Re-reads the rules (cheap) and drops buckets whose rate no longer matches. */
    fun refresh() {
        val rules = runCatching { ruleSource() }.getOrDefault(emptyMap())
        val block = HashSet<String>()
        val lim = HashMap<String, LongArray>()
        for ((pkg, r) in rules) {
            when (r.mode) {
                RuleMode.BLOCK -> block.add(pkg)
                RuleMode.LIMIT -> {
                    if (r.downBps > 0 || r.upBps > 0) lim[pkg] = longArrayOf(r.downBps, r.upBps)
                }
                RuleMode.ALLOW -> Unit
            }
        }
        blocked = block
        limits = lim
        anyBlock = block.isNotEmpty()
        // A bucket remembers the rate it was created with, so a rule edited from 512 KB/s to
        // 128 KB/s would otherwise keep shaping at the old speed until the rule is removed.
        // Dropping it makes the next packet build a fresh one at the new rate.
        buckets.keys.retainAll { key ->
            val pkg = key.substringBefore('|')
            val rate = limits[pkg]?.get(if (key.endsWith("|u")) 1 else 0) ?: -1L
            rate > 0 && buckets[key]?.ratePerSec == rate
        }
    }

    /** Attribution for a brand new flow. Called once per flow (the relay caches it). */
    fun decide(
        proto: Int, srcIp: Int, srcPort: Int, dstIp: Int, dstPort: Int
    ): Outcome {
        val uid = runCatching { ownerUid(proto, srcIp, srcPort, dstIp, dstPort) }
            .getOrDefault(INVALID_UID)
        if (uid == INVALID_UID) {
            // Both counters are read for the decision *and* for the warning, so they have to
            // be read inside the same lock that wrote them - read outside, a torn pair could
            // declare a healthy engine broken (or the other way round).
            val failClosed: Boolean
            val tripped: Boolean
            synchronized(lock) {
                misses++
                failClosed = anyBlock && !(hits == 0 && misses >= BREAKER_AFTER)
                tripped = hits == 0 && misses >= BREAKER_AFTER
            }
            if (tripped && !FirewallStats.attributionBroken) {
                FirewallStats.attributionBroken = true
            }
            return Outcome(
                if (failClosed) Decision.Block else Decision.Pass(null),
                identified = false
            )
        }
        synchronized(lock) { hits++ }
        val pkg = runCatching { pkgOf(uid) }.getOrNull()
            ?: return Outcome(Decision.Pass(null), identified = true)
        return Outcome(decidePkg(pkg), identified = true)
    }

    /** Pure rule lookup — no platform call, safe to repeat on every packet of a flow. */
    fun decidePkg(pkg: String?): Decision = when {
        pkg == null -> Decision.Pass(null)
        pkg in blocked -> Decision.Block
        else -> {
            val l = limits[pkg]
            if (l == null || (l[0] <= 0L && l[1] <= 0L)) Decision.Pass(pkg)
            else Decision.Shape(pkg)
        }
    }

    /** Bytes the direction may send right now; [Long.MAX_VALUE] when nothing caps it. */
    fun available(pkg: String?, up: Boolean): Long {
        if (pkg == null) return Long.MAX_VALUE
        val rate = rateOf(pkg, up) ?: return Long.MAX_VALUE
        return bucket(pkg, up, rate).available(System.nanoTime())
    }

    /**
     * Takes up to [want] bytes out of the direction's budget and returns how many were
     * really granted (0 when the bucket is empty). Callers must send exactly what they got:
     * this is the accounting half of a limit, [available] only ever peeks.
     */
    fun reserve(pkg: String?, up: Boolean, want: Long): Long {
        if (want <= 0L) return 0L
        if (pkg == null) return want
        val rate = rateOf(pkg, up) ?: return want
        return bucket(pkg, up, rate).reserve(System.nanoTime(), want)
    }

    /** Returns [n] unused bytes to the budget (partial socket write, abandoned segment). */
    fun refund(pkg: String?, up: Boolean, n: Long) {
        if (pkg == null || n <= 0L) return
        val rate = rateOf(pkg, up) ?: return
        bucket(pkg, up, rate).refund(System.nanoTime(), n)
    }

    /** True when [n] bytes fit in the budget (used for datagrams, which cannot be paced). */
    fun spend(pkg: String?, up: Boolean, n: Long): Boolean {
        if (pkg == null || n <= 0L) return true
        val rate = rateOf(pkg, up) ?: return true
        return bucket(pkg, up, rate).spend(n, System.nanoTime())
    }

    private fun rateOf(pkg: String, up: Boolean): Long? {
        val l = limits[pkg] ?: return null
        val rate = if (up) l[1] else l[0]
        return if (rate > 0) rate else null
    }

    private fun bucket(pkg: String, up: Boolean, rate: Long): TokenBucket {
        val key = "$pkg|" + (if (up) "u" else "d")
        buckets[key]?.let { return it }
        val created = TokenBucket(rate)
        val prev = buckets.putIfAbsent(key, created)
        return prev ?: created
    }

    companion object {
        const val INVALID_UID = -1

        /**
         * How many flow decisions in a row may come back unidentified, with not a single
         * successful identification among them, before the engine stops failing closed.
         */
        const val BREAKER_AFTER = 12
    }
}

/**
 * Live counters shown on the limiter screen. Kept as plain volatiles: the UI polls them,
 * and neither side needs a flow.
 */
object FirewallStats {
    @Volatile var active = false
    @Volatile var blockedPackets = 0L
    @Volatile var unidentifiedFlows = 0L
    @Volatile var relayedFlows = 0
    @Volatile var limitedDatagrams = 0L
    @Volatile var icmpDropped = 0L
    @Volatile var ipv6Dropped = 0L
    @Volatile var attributionBroken = false

    fun reset() {
        active = false
        blockedPackets = 0L
        unidentifiedFlows = 0L
        relayedFlows = 0
        limitedDatagrams = 0L
        icmpDropped = 0L
        ipv6Dropped = 0L
        attributionBroken = false
    }
}
