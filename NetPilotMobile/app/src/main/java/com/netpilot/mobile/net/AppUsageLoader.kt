package com.netpilot.mobile.net

import android.app.AppOpsManager
import android.app.usage.NetworkStats
import android.app.usage.NetworkStatsManager
import android.app.usage.UsageStatsManager
import android.content.Context
import android.content.pm.PackageManager
import android.net.NetworkCapabilities
import android.net.TrafficStats
import android.os.Build
import android.os.Process
import com.netpilot.mobile.data.AppUsage
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

/**
 * Per-app traffic accounting.
 *
 * Two real sources are tried, in order:
 *  1. [NetworkStatsManager] — the platform's own per-UID accounting (survives restarts,
 *     covers all transports, reset by the system when the user resets data usage),
 *  2. [TrafficStats.getUidRxBytes] / [TrafficStats.getUidTxBytes] — fallback for devices
 *     where the stats service refuses third-party reads.
 *
 * If neither source yields numbers the UI says so instead of inventing them.
 *
 * NOTE: these classes live in `android.app.usage` (not `android.net`) — that is the
 * public SDK surface since API 23.
 */
object AppUsageLoader {

    data class Snapshot(
        val rows: List<AppUsage>,
        val source: String   // "netstats" | "trafficstats" | "none"
    )

    /** True when the user granted usage access (the gate Android documents for this data). */
    fun hasUsageAccess(context: Context): Boolean {
        val app = context.applicationContext
        val aom = app.getSystemService(Context.APP_OPS_SERVICE) as? AppOpsManager ?: return false
        return try {
            val mode = if (Build.VERSION.SDK_INT >= 29) {
                aom.unsafeCheckOpNoThrow(
                    AppOpsManager.OPSTR_GET_USAGE_STATS,
                    Process.myUid(), app.packageName
                )
            } else {
                @Suppress("DEPRECATION")
                aom.checkOpNoThrow(
                    AppOpsManager.OPSTR_GET_USAGE_STATS,
                    Process.myUid(), app.packageName
                )
            }
            mode == AppOpsManager.MODE_ALLOWED
        } catch (t: Throwable) {
            false
        }
    }

    /** Foreground usage time in ms over [windowMs] — shown next to the byte totals. */
    fun foregroundMs(context: Context, windowMs: Long = 24L * 3600_000L): Map<String, Long> {
        if (!hasUsageAccess(context)) return emptyMap()
        return try {
            val usm = context.applicationContext
                .getSystemService(Context.USAGE_STATS_SERVICE) as? UsageStatsManager
                ?: return emptyMap()
            val end = System.currentTimeMillis()
            val stats = usm.queryUsageStats(
                UsageStatsManager.INTERVAL_DAILY, end - windowMs, end
            ) ?: return emptyMap()
            stats.asSequence()
                .filter { it.totalTimeInForeground > 0L }
                .associate { it.packageName to it.totalTimeInForeground }
        } catch (t: Throwable) {
            emptyMap()
        }
    }

    /** Read totals for every package that carries the INTERNET permission. */
    suspend fun load(context: Context, windowMs: Long = 7L * 24 * 3600_000L): Snapshot =
        withContext(Dispatchers.IO) {
        val app = context.applicationContext
        val pm = app.packageManager
        val self = app.packageName

        // uid -> label / key package. Shared-user ids collapse onto one row because that
        // is exactly how the platform accounts for them.
        val labels = HashMap<Int, String>()
        val pkgs = HashMap<Int, String>()
        runCatching {
            // GET_PERMISSIONS is what fills PackageInfo.requestedPermissions — without it
            // every entry looks permission-less, gets skipped below and the whole list
            // silently renders empty.
            @Suppress("DEPRECATION")
            val installed = pm.getInstalledPackages(PackageManager.GET_PERMISSIONS)
            for (p in installed) {
                val requested = p.requestedPermissions ?: continue
                if ("android.permission.INTERNET" !in requested) continue
                val info = p.applicationInfo ?: continue
                val uid = info.uid
                labels.getOrPut(uid) {
                    runCatching { info.loadLabel(pm)?.toString() ?: p.packageName }
                        .getOrDefault(p.packageName)
                }
                val cur = pkgs[uid]
                if (cur == null || cur == self) pkgs[uid] = p.packageName
            }
        }

        val now = System.currentTimeMillis()
        val from = now - windowMs   // bounded window: realistic, bounded work

        // ---- source 1: NetworkStats (per-UID, per-transport)
        val netstats = HashMap<Int, LongArray>()
        runCatching {
            val nsm = app.getSystemService(Context.NETWORK_STATS_SERVICE)
                    as? NetworkStatsManager ?: return@runCatching
            querySummary(nsm, from, now, netstats)
        }

        // ---- source 2: TrafficStats per uid (works when the stats service allows it)
        val trafficFallback = HashMap<Int, LongArray>()
        var trafficUsable = false
        for (uid in labels.keys) {
            val rx = uidRx(uid)
            val tx = uidTx(uid)
            if (rx >= 0L || tx >= 0L) trafficUsable = true
            trafficFallback[uid] = longArrayOf(rx.coerceAtLeast(0L), tx.coerceAtLeast(0L))
        }

        val source = when {
            netstats.isNotEmpty() -> "netstats"
            trafficUsable -> "trafficstats"
            else -> "none"
        }
        val rows = if (source == "netstats") netstats else trafficFallback

        val list = rows.mapNotNull { (uid, v) ->
            val label = labels[uid] ?: return@mapNotNull null
            if (v[0] <= 0L && v[1] <= 0L) return@mapNotNull null
            AppUsage(
                pkg = pkgs[uid] ?: label,
                label = label,
                uid = uid,
                rxTotal = v[0],
                txTotal = v[1]
            )
        }.sortedByDescending { it.total }

        Snapshot(list, source)
    }

    /**
     * Device-wide received/sent bytes for [windowMs], **without** resolving a single label.
     *
     * Network History needs one honest number per flush; going through [load] would mean
     * asking every installed package for its label once a minute.
     * Returns `null` when the platform refuses, so the caller can fall back to the delta
     * counter instead of writing zeros over a good day.
     */
    suspend fun deviceTotals(context: Context, windowMs: Long): LongArray? =
        withContext(Dispatchers.IO) {
            val nsm = context.applicationContext
                .getSystemService(Context.NETWORK_STATS_SERVICE) as? NetworkStatsManager
                ?: return@withContext null
            val now = System.currentTimeMillis()
            val into = HashMap<Int, LongArray>()
            runCatching { querySummary(nsm, now - windowMs, now, into) }.getOrNull() ?: return@withContext null
            var rx = 0L
            var tx = 0L
            for (v in into.values) {
                rx += v[0]
                tx += v[1]
            }
            longArrayOf(rx, tx)
        }

    private fun querySummary(
        nsm: NetworkStatsManager,
        from: Long,
        to: Long,
        into: HashMap<Int, LongArray>
    ) {
        // One walk per transport; buckets are keyed by UID.
        for (transport in intArrayOf(
            NetworkCapabilities.TRANSPORT_WIFI,
            NetworkCapabilities.TRANSPORT_CELLULAR,
            NetworkCapabilities.TRANSPORT_ETHERNET
        )) {
            runCatching {
                @Suppress("DEPRECATION")
                val stats = nsm.querySummary(transport, null, from, to) ?: return@runCatching
                val bucket = NetworkStats.Bucket()
                try {
                    while (stats.hasNextBucket()) {
                        stats.getNextBucket(bucket)
                        val uid = bucket.uid
                        if (uid < 0) continue
                        val rx = bucket.rxBytes
                        val tx = bucket.txBytes
                        if (rx <= 0L && tx <= 0L) continue
                        val cur = into.getOrPut(uid) { LongArray(2) }
                        cur[0] += rx
                        cur[1] += tx
                    }
                } finally {
                    runCatching { stats.close() }
                }
            }
        }
    }

    private fun uidRx(uid: Int): Long = runCatching { TrafficStats.getUidRxBytes(uid) }
        .getOrDefault(-1L)

    private fun uidTx(uid: Int): Long = runCatching { TrafficStats.getUidTxBytes(uid) }
        .getOrDefault(-1L)

    /** A snapshot of raw counters for a set of uids (used to derive live rates). */
    suspend fun rates(context: Context, uids: List<Int>): Map<Int, LongArray> =
        withContext(Dispatchers.IO) {
            val out = HashMap<Int, LongArray>()
            for (uid in uids) {
                out[uid] = longArrayOf(uidRx(uid).coerceAtLeast(0L), uidTx(uid).coerceAtLeast(0L))
            }
            out
        }
}
