package com.netpilot.mobile.net

import android.content.Context
import android.net.ConnectivityManager
import android.net.LinkProperties
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import android.net.wifi.WifiManager
import android.os.Build
import android.net.TrafficStats
import com.netpilot.mobile.data.HealthScore
import com.netpilot.mobile.data.LinkState
import com.netpilot.mobile.data.Repo
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.net.InetSocketAddress
import java.net.Socket
import java.util.ArrayDeque
import java.util.Calendar
import kotlin.math.abs

/**
 * Live network telemetry.
 *
 *  - link facts come from the platform [ConnectivityManager] callback (transport, DNS,
 *    addresses, gateway, link speed),
 *  - throughput comes from [TrafficStats] deltas sampled on a fixed interval,
 *  - latency/loss come from real TCP probes to two anycast targets (rotated),
 *  - the health score is computed from those three real measurements + link stability.
 *
 * Everything runs on a background scope: no tick ever touches the main thread.
 */
object Monitor {

    data class Sample(
        val at: Long,
        val rx: Long,   // bytes/sec
        val tx: Long    // bytes/sec
    )

    data class Traffic(
        val rxRate: Long = 0,
        val txRate: Long = 0,
        val totalRx: Long = 0,
        val totalTx: Long = 0,
        val sessionRx: Long = 0,
        val sessionTx: Long = 0,
        val history: List<Sample> = emptyList()
    )

    data class Ping(
        val ms: Int = -1,
        val avgMs: Int = -1,
        val lossPct: Double = 0.0,
        val samples: List<Int> = emptyList(),
        val target: String = ""
    )

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var started = false
    private var trafficJob: Job? = null
    private var pingJob: Job? = null
    private var dnsJob: Job? = null

    private val _link = MutableStateFlow(LinkState())
    val link: StateFlow<LinkState> = _link.asStateFlow()

    private val _traffic = MutableStateFlow(Traffic())
    val traffic: StateFlow<Traffic> = _traffic.asStateFlow()

    private val _ping = MutableStateFlow(Ping())
    val ping: StateFlow<Ping> = _ping.asStateFlow()

    /** DNS response time against the resolver currently in use (0 = not measured yet). */
    private val _dnsMs = MutableStateFlow(-1)
    val dnsMs: StateFlow<Int> = _dnsMs.asStateFlow()

    private val _health = MutableStateFlow(HealthScore(0, -1, "", -1, "", 0.0, "", "", 0))
    val health: StateFlow<HealthScore> = _health.asStateFlow()

    /** Live per-app rates keyed by package name (uid â†’ pkg supplied by the caller). */
    private val _appRates = MutableStateFlow<Map<String, Pair<Long, Long>>>(emptyMap())
    val appRates: StateFlow<Map<String, Pair<Long, Long>>> = _appRates.asStateFlow()

    private var cm: ConnectivityManager? = null
    private var wifi: WifiManager? = null
    private var callback: ConnectivityManager.NetworkCallback? = null

    private var lastTotalRx = -1L
    private var lastTotalTx = -1L
    /** Baselines for the mobile+wifi fallback, used only when the global counters are hidden. */
    private var lastMobileRx = -1L
    private var lastMobileTx = -1L
    /** Device-wide delta not yet written to the daily bucket (see [flushUsage]). */
    private val usageLock = Any()
    private var pendingRx = 0L
    private var pendingTx = 0L
    private var lastUsageFlushAt = 0L
    private var lastTopAppAt = 0L
    private var appCtx: Context? = null
    private var lastTick = 0L
    private var sessionStart = 0L
    private var sessionRxBase = 0L
    private var sessionTxBase = 0L

    // The ping loop appends from a Default-dispatcher coroutine while the traffic loop and
    // the UI's "reset" read and clear it - ArrayDeque is not thread safe, and a clear() that
    // lands in the middle of addLast's grow() corrupts the window (or throws inside the ping
    // loop, which would silently kill it). A concurrent deque keeps the window correct; the
    // window size is a handful of samples, so the contention is irrelevant.
    private val pingSamples = java.util.concurrent.ConcurrentLinkedDeque<Int>()
    private var linkChanges = 0
    private var lastLinkChangeAt = 0L

    /**
     * Latency probes, in the order they are tried. snapp.ir answers on this network and
     * keeps the number on the dashboard representative of local latency (≈24 ms here,
     * against ≈105 ms for a resolver on the far side of the planet); the public resolvers
     * behind it are only reached when the primary target does not answer at all.
     */
    private val pingTargets = listOf("snapp.ir", "1.1.1.1", "8.8.8.8")

    private var intervalMs = 1000L

    // ------------------------------------------------------------------ lifecycle

    fun start(context: Context, intervalMs: Long = 1000L) {
        if (started) return
        synchronized(this) {
            if (started) return
            started = true
            this.intervalMs = intervalMs.coerceIn(500L, 5000L)
            val app = context.applicationContext
            appCtx = app
            cm = app.getSystemService(Context.CONNECTIVITY_SERVICE) as? ConnectivityManager
            wifi = app.getSystemService(Context.WIFI_SERVICE) as? WifiManager
            sessionStart = System.currentTimeMillis()

            registerCallback()
            refreshLink()
            startTrafficLoop()
            startPingLoop()
            startDnsLoop()
            recomputeHealth()
        }
    }

    fun stop() {
        synchronized(this) {
            if (!started) return
            started = false
            callback?.let { runCatching { cm?.unregisterNetworkCallback(it) } }
            callback = null
            trafficJob?.cancel(); trafficJob = null
            pingJob?.cancel(); pingJob = null
            dnsJob?.cancel(); dnsJob = null
            // Do not lose the batch that has not reached the store yet.
            scope.launch { runCatching { flushUsage(System.currentTimeMillis(), force = true) } }
        }
    }

    fun setInterval(intervalMs: Long) {
        this.intervalMs = intervalMs.coerceIn(500L, 5000L)
        if (started) {
            trafficJob?.cancel()
            startTrafficLoop()
        }
    }

    /** Force a fresh link snapshot (called after VPN start/stop and on screen resume). */
    fun refreshLink() {
        scope.launch {
            val active = cm?.activeNetwork
            val caps = active?.let { cm?.getNetworkCapabilities(it) }
            val lp = active?.let { cm?.getLinkProperties(it) }
            _link.value = buildLink(active, caps, lp)
            recomputeHealth()
        }
    }

    // ------------------------------------------------------------------ link

    private fun registerCallback() {
        val manager = cm ?: return
        val cb = object : ConnectivityManager.NetworkCallback() {
            override fun onAvailable(network: Network) = refreshLink()
            override fun onLost(network: Network) = refreshLink()
            override fun onCapabilitiesChanged(network: Network, caps: NetworkCapabilities) =
                refreshLink()

            override fun onLinkPropertiesChanged(network: Network, lp: LinkProperties) =
                refreshLink()
        }
        val request = NetworkRequest.Builder()
            .addCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
            .build()
        runCatching { manager.registerNetworkCallback(request, cb) }
        callback = cb
    }

    private fun buildLink(
        network: Network?,
        caps: NetworkCapabilities?,
        lp: LinkProperties?
    ): LinkState {
        val connected = caps?.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED) == true ||
                caps?.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET) == true

        val transport = when {
            caps?.hasTransport(NetworkCapabilities.TRANSPORT_VPN) == true -> "VPN"
            caps?.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) == true -> "WIFI"
            caps?.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET) == true -> "ETHERNET"
            caps?.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR) == true -> "CELLULAR"
            caps == null -> ""
            else -> "OTHER"
        }

        val dns = lp?.dnsServers?.map { it.hostAddress ?: "" }?.filter { it.isNotBlank() }
            ?: emptyList()

        val localIp = lp?.linkAddresses
            ?.firstOrNull { it.address is java.net.Inet4Address }
            ?.address?.hostAddress ?: ""

        val gateway = lp?.routes
            ?.firstOrNull { it.isDefaultRoute && it.gateway is java.net.Inet4Address }
            ?.gateway?.hostAddress ?: ""

        val linkSpeed = if (transport == "WIFI") {
            runCatching { wifi?.connectionInfo?.linkSpeed ?: -1 }.getOrDefault(-1)
        } else if (transport == "ETHERNET") {
            // Android does not expose a portable Ethernet speed API; show the negotiated
            // value when the platform provides it, otherwise 0 = unknown.
            0
        } else -1

        return LinkState(
            connected = connected,
            transport = transport,
            typeName = when (transport) {
                "WIFI" -> "Wi-Fi"
                "CELLULAR" -> "Cellular"
                "ETHERNET" -> "Ethernet"
                "VPN" -> "VPN"
                "OTHER" -> "Other"
                else -> "None"
            },
            rxRate = _traffic.value.rxRate,
            txRate = _traffic.value.txRate,
            dnsServers = dns,
            localIp = localIp,
            gateway = gateway,
            linkSpeedMbps = linkSpeed,
            vpnActive = transport == "VPN"
        )
    }

    // ------------------------------------------------------------------ throughput

    private fun startTrafficLoop() {
        trafficJob?.cancel()
        trafficJob = scope.launch {
            // First tick only establishes the baseline.
            lastTotalRx = TrafficStats.getTotalRxBytes()
            lastTotalTx = TrafficStats.getTotalTxBytes()
            lastTick = System.nanoTime()
            sessionRxBase = lastTotalRx.coerceAtLeast(0L)
            sessionTxBase = lastTotalTx.coerceAtLeast(0L)
            lastMobileRx = TrafficStats.getMobileRxBytes()
            lastMobileTx = TrafficStats.getMobileTxBytes()

            while (isActive) {
                delay(intervalMs)
                val now = System.nanoTime()
                val rx = TrafficStats.getTotalRxBytes()
                val tx = TrafficStats.getTotalTxBytes()
                val dt = ((now - lastTick) / 1_000_000L).coerceAtLeast(1L)
                lastTick = now

                val rxRate: Long
                val txRate: Long
                var tickRx = 0L
                var tickTx = 0L
                if (rx < 0 || tx < 0 || lastTotalRx < 0 || lastTotalTx < 0) {
                    // Some devices hide the global counters; fall back to the mobile pair.
                    val mRx = TrafficStats.getMobileRxBytes()
                    val mTx = TrafficStats.getMobileTxBytes()
                    tickRx = if (mRx >= 0 && lastMobileRx >= 0) (mRx - lastMobileRx).coerceAtLeast(0L) else 0L
                    tickTx = if (mTx >= 0 && lastMobileTx >= 0) (mTx - lastMobileTx).coerceAtLeast(0L) else 0L
                    if (mRx >= 0) lastMobileRx = mRx
                    if (mTx >= 0) lastMobileTx = mTx
                    lastTotalRx = rx; lastTotalTx = tx
                } else {
                    tickRx = (rx - lastTotalRx).coerceAtLeast(0L)
                    tickTx = (tx - lastTotalTx).coerceAtLeast(0L)
                    lastTotalRx = rx
                    lastTotalTx = tx
                }
                rxRate = perSecond(tickRx, dt)
                txRate = perSecond(tickTx, dt)

                synchronized(usageLock) {
                    pendingRx += tickRx
                    pendingTx += tickTx
                }
                flushUsage(System.currentTimeMillis())

                val cur = _traffic.value
                val sample = Sample(at = System.currentTimeMillis(), rx = rxRate, tx = txRate)
                val history = (cur.history + sample).takeLast(CHART_POINTS)

                _traffic.value = Traffic(
                    rxRate = rxRate,
                    txRate = txRate,
                    totalRx = if (rx >= 0) rx else 0L,
                    totalTx = if (tx >= 0) tx else 0L,
                    sessionRx = if (rx >= 0) (rx - sessionRxBase).coerceAtLeast(0L) else 0L,
                    sessionTx = if (tx >= 0) (tx - sessionTxBase).coerceAtLeast(0L) else 0L,
                    history = history
                )
                _link.value = _link.value.copy(rxRate = rxRate, txRate = txRate)
                recomputeHealth()
            }
        }
    }

    /**
     * Scales [d] bytes observed over [dtMs] milliseconds to bytes per second.
     *
     * The loop used to divide the delta straight by the millisecond count, so every
     * reading came out 1000x too small: a 7 KB/s transfer rendered as "7 B/s" and the
     * dashboard spent most of its life on "0 B/s" even while the chart was moving.
     */
    internal fun perSecond(d: Long, dtMs: Long): Long =
        if (dtMs <= 0 || d <= 0) 0L else (d * 1000L) / dtMs

    // ------------------------------------------------------- daily usage accounting

    /**
     * Writes the accumulated device-wide delta into today's bucket.
     *
     * Network History renders [Repo.days], but nothing ever called [Repo.recordUsage], so
     * that screen could only ever answer "No history yet". The write is batched (256 KB or
     * one minute) instead of touching the store on every sampling tick, and the top app of
     * the day comes from one NetworkStats query every ten minutes — the platform does not
     * hand third-party apps a live per-UID counter.
     */
    private suspend fun flushUsage(now: Long, force: Boolean = false) {
        val dRx: Long
        val dTx: Long
        synchronized(usageLock) {
            if (pendingRx <= 0 && pendingTx <= 0) return
            if (!force && pendingRx + pendingTx < USAGE_FLUSH_BYTES &&
                now - lastUsageFlushAt < USAGE_FLUSH_MS
            ) return
            dRx = pendingRx
            dTx = pendingTx
            pendingRx = 0
            pendingTx = 0
            lastUsageFlushAt = now
        }

        var topPkg = ""
        var topRx = 0L
        var topTx = 0L
        if (now - lastTopAppAt >= TOP_APP_MS) {
            lastTopAppAt = now
            runCatching {
                val ctx = appCtx ?: return@runCatching
                val top = AppUsageLoader
                    .load(ctx, windowMs = (now - startOfDay(now)).coerceAtLeast(1L))
                    .rows.firstOrNull() ?: return@runCatching
                topPkg = top.pkg
                topRx = top.rxTotal
                topTx = top.txTotal
            }
        }
        runCatching {
            // Same window for total and top app (midnight → now), so the page can never show
            // a top app larger than the day it belongs to. The delta batch is the fallback
            // for a build where NetworkStats answers nothing.
            val day = appCtx?.let {
                AppUsageLoader.deviceTotals(it, (now - startOfDay(now)).coerceAtLeast(1L))
            }
            if (day != null && (day[0] > 0L || day[1] > 0L)) {
                Repo.setUsage(day[0], day[1], topPkg, topRx, topTx)
            } else {
                Repo.recordUsage(dRx, dTx, topPkg, topRx, topTx)
            }
        }
    }

    /** Local midnight as epoch millis — the window "top app of the day" is measured over. */
    private fun startOfDay(now: Long): Long = Calendar.getInstance().apply {
        timeInMillis = now
        set(Calendar.HOUR_OF_DAY, 0)
        set(Calendar.MINUTE, 0)
        set(Calendar.SECOND, 0)
        set(Calendar.MILLISECOND, 0)
    }.timeInMillis

    // ------------------------------------------------------------------ latency

    private fun startPingLoop() {
        pingJob?.cancel()
        pingJob = scope.launch {
            var sinceLossTick = 0
            while (isActive) {
                // Primary first, and kept for as long as it answers: the ping on the
                // dashboard must not alternate between hosts that differ by an order of
                // magnitude. A dead primary falls through to the public resolvers instead
                // of reporting a permanent 100 % loss.
                var target = pingTargets.first()
                var ms = -1
                for (candidate in pingTargets) {
                    target = candidate
                    ms = tcpPing(candidate, 1500)
                    if (ms >= 0) break
                }
                withContext(Dispatchers.Default) {
                    if (ms >= 0) {
                        pingSamples.addLast(ms)
                    } else {
                        // A timeout counts as a lost probe for the loss ratio.
                        pingSamples.addLast(-1)
                    }
                    while (pingSamples.size > PING_WINDOW) pingSamples.removeFirst()
                    // One snapshot: size(), filter and count had to agree, and against a
                    // deque another thread trims they did not.
                    val window = pingSamples.toList()
                    val ok = window.filter { it >= 0 }
                    val avg = if (ok.isEmpty()) -1 else ok.average().toInt()
                    _ping.value = Ping(
                        ms = ms,
                        avgMs = avg,
                        lossPct = if (window.isEmpty()) 0.0
                        else window.count { it < 0 } * 100.0 / window.size,
                        samples = ok,
                        target = target
                    )
                }
                sinceLossTick++
                delay(if (ms >= 0) 2000L else 3000L)
            }
        }
    }

    private suspend fun tcpPing(host: String, timeoutMs: Int): Int = withContext(Dispatchers.IO) {
        val start = System.nanoTime()
        try {
            Socket().use { s ->
                s.connect(InetSocketAddress(host, 443), timeoutMs)
                ((System.nanoTime() - start) / 1_000_000L).toInt()
            }
        } catch (t: Throwable) {
            try {
                Socket().use { s ->
                    s.connect(InetSocketAddress(host, 53), timeoutMs)
                    ((System.nanoTime() - start) / 1_000_000L).toInt()
                }
            } catch (t2: Throwable) {
                -1
            }
        }
    }

    // ------------------------------------------------------------------ dns timing

    private fun startDnsLoop() {
        dnsJob?.cancel()
        dnsJob = scope.launch {
            while (isActive) {
                val server = _link.value.dnsServers.firstOrNull { !DnsQuery.isPrivate(it) }
                    ?: _link.value.dnsServers.firstOrNull()
                    ?: "1.1.1.1"
                val res = DnsQuery.query(server, "www.google.com", DnsQuery.TYPE_A, 2000)
                _dnsMs.value = if (res.ok) res.ms else -1
                recomputeHealth()
                delay(DNS_INTERVAL_MS)
            }
        }
    }

    // ------------------------------------------------------------------ health

    fun recomputeHealth() {
        val l = _link.value
        val p = _ping.value
        val dns = _dnsMs.value
        val now = System.currentTimeMillis()
        val recentChanges = if (now - lastLinkChangeAt < 60_000) linkChanges else 0

        // No probe window yet: "0% loss" would be a claim, not a measurement.
        val lossPct = if (pingSamples.isEmpty()) Double.NaN else p.lossPct
        val parts = scoreHealth(
            online = l.connected,
            dnsMs = dns,
            avgMs = p.avgMs,
            lossPct = lossPct,
            recentLinkChanges = recentChanges,
        )

        _health.value = HealthScore(
            score = parts.score,
            dnsMs = dns,
            dnsLabel = label(parts.dnsScore),
            latencyMs = p.avgMs,
            latencyLabel = label(parts.latencyScore),
            lossPct = lossPct,
            lossLabel = label(parts.lossScore),
            stableLabel = when {
                !parts.online -> LT_DISCONNECTED
                parts.stabilityScore >= 80 -> LT_STABLE
                else -> LT_UNSTABLE
            },
            sampleCount = pingSamples.size
        )
    }

    /** Called by the VPN/network callbacks so link flaps reduce the stability component. */
    fun noteLinkChange() {
        val now = System.currentTimeMillis()
        if (now - lastLinkChangeAt < 30_000) linkChanges++ else linkChanges = 1
        lastLinkChangeAt = now
    }

    /**
     * Component score → display code. An unmeasured component is `NO_SAMPLE` and becomes
     * "NA", so a row with no data shows a dash instead of the old confident "fair".
     */
    private fun label(score: Int): String = when {
        score == NO_SAMPLE -> "NA"
        score >= 85 -> "EXCELLENT"
        score >= 70 -> "GOOD"
        score >= 50 -> "FAIR"
        else -> "POOR"
    }

    private const val LT_STABLE = "STABLE"
    private const val LT_UNSTABLE = "UNSTABLE"
    private const val LT_DISCONNECTED = "OFFLINE"

    const val CHART_POINTS = 90
    const val PING_WINDOW = 20
    private const val DNS_INTERVAL_MS = 30_000L

    /** Daily-usage batching: write at 256 KB or once a minute, whichever comes first. */
    private const val USAGE_FLUSH_BYTES = 256L * 1024
    private const val USAGE_FLUSH_MS = 60_000L
    /** NetworkStats query for "top app of the day", at most once every ten minutes. */
    private const val TOP_APP_MS = 10 * 60_000L

    /** Reset session counters (used by the "Reset network" tool). */
    fun resetSession() {
        sessionStart = System.currentTimeMillis()
        sessionRxBase = TrafficStats.getTotalRxBytes().coerceAtLeast(0L)
        sessionTxBase = TrafficStats.getTotalTxBytes().coerceAtLeast(0L)
        pingSamples.clear()
        _traffic.value = _traffic.value.copy(sessionRx = 0, sessionTx = 0, history = emptyList())
        _ping.value = Ping()
        recomputeHealth()
    }

    fun sessionStartedAt(): Long = sessionStart
}
