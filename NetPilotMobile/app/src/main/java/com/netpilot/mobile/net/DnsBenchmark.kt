package com.netpilot.mobile.net

import com.netpilot.mobile.data.DnsBenchResult
import com.netpilot.mobile.data.DnsEntry
import com.netpilot.mobile.data.DnsProbe
import com.netpilot.mobile.data.Store
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withPermit
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject

/**
 * Real DNS benchmark: resolves a fixed name against every candidate resolver, [rounds]
 * probes each, and ranks by average latency with a penalty for packet loss.
 *
 * All work runs on the IO dispatcher with a bounded concurrency, so the UI never blocks
 * and the test does not flood the network at once.
 */
class DnsBenchmark(private val store: Store) {

    data class Progress(
        val running: Boolean = false,
        val done: Int = 0,
        val total: Int = 0,
        val current: String = "",
        val results: List<DnsBenchResult> = emptyList()
    )

    private val _progress = MutableStateFlow(Progress())
    val progress: StateFlow<Progress> = _progress.asStateFlow()

    /** Cached result of the last completed run (restored from disk on first access). */
    private val _results = MutableStateFlow<List<DnsBenchResult>>(loadCached())
    val results: StateFlow<List<DnsBenchResult>> = _results.asStateFlow()

    suspend fun run(
        entries: List<DnsEntry>,
        rounds: Int = 3,
        probeName: String = "www.google.com",
        timeoutMs: Int = 2000,
        concurrency: Int = 4
    ): List<DnsBenchResult> = coroutineScope {
        if (entries.isEmpty()) return@coroutineScope emptyList()
        if (_progress.value.running) return@coroutineScope _results.value

        _progress.value = Progress(running = true, done = 0, total = entries.size)
        try {
            val semaphore = Semaphore(concurrency)
            var finished = 0

            val perEntry: List<Pair<DnsEntry, List<DnsQuery.ProbeStats>>> = entries.map { entry ->
                async(Dispatchers.IO) {
                    semaphore.withPermit {
                        // One server out of the set is probed per round; all servers of an
                        // entry are probed in parallel inside a round to keep the test fair.
                        val stats = entry.servers.map { server ->
                            async {
                                DnsQuery.probe(
                                    server = server,
                                    name = probeName,
                                    rounds = rounds,
                                    timeoutMs = timeoutMs,
                                    parallel = false
                                )
                            }
                        }.awaitAll()

                        val idx = synchronized(this) { ++finished }
                        _progress.value = _progress.value.copy(
                            done = idx, current = entry.name
                        )
                        entry to stats
                    }
                }
            }.awaitAll()

            val ranked = rank(perEntry)
            _results.value = ranked
            _progress.value = Progress(running = false, done = entries.size, total = entries.size)
            save(ranked)
            ranked
        } catch (t: Throwable) {
            _progress.value = Progress(running = false)
            _results.value
        }
    }

    /** Combine per-server probes of one resolver into a single result and sort by score. */
    private fun rank(perEntry: List<Pair<DnsEntry, List<DnsQuery.ProbeStats>>>): List<DnsBenchResult> {
        val merged = perEntry.map { (entry, stats) ->
            val probes = stats.map {
                DnsProbe(
                    server = it.server,
                    ok = it.success > 0,
                    ms = it.avgMs,
                    lossPct = it.lossPct,
                    avgMs = it.avgMs,
                    jitterMs = it.jitterMs
                )
            }
            val best = stats.filter { it.avgMs > 0 }.map { it.avgMs }
            val avg = if (best.isEmpty()) -1 else best.average().toInt()
            val jitter = if (stats.isEmpty()) 0 else stats.map { it.jitterMs }.average().toInt()
            val attempts = stats.sumOf { it.attempts }
            val success = stats.sumOf { it.success }
            val loss = if (attempts == 0) 100.0 else (attempts - success) * 100.0 / attempts
            DnsBenchResult(
                entry = entry,
                probes = probes,
                avgMs = avg,
                jitterMs = jitter,
                lossPct = loss
            )
        }
        // Loss dominates the ordering: a fast resolver that drops half the queries is not usable.
        fun score(r: DnsBenchResult): Double {
            if (r.avgMs <= 0) return Double.MAX_VALUE
            return r.avgMs + r.lossPct * 40.0 + r.jitterMs * 0.5
        }
        return merged.sortedBy { score(it) }
            .mapIndexed { i, r -> r.copy(rank = i + 1) }
    }

    /** Take the fastest resolver of the last run ("" when there is no usable data). */
    fun fastest(): DnsBenchResult? = _results.value.firstOrNull { it.avgMs > 0 }

    fun clear() {
        _results.value = emptyList()
        _progress.value = Progress()
        runCatching { store.edit(Store.D_BENCH) { it.put("results", JSONArray()) } }
    }

    // ------------------------------------------------------------------ persistence

    private fun save(list: List<DnsBenchResult>) {
        runCatching {
            store.edit(Store.D_BENCH) { doc ->
                val arr = JSONArray()
                list.forEach { r ->
                    arr.put(JSONObject().apply {
                        put("id", r.entry.id)
                        put("avg", r.avgMs)
                        put("jitter", r.jitterMs)
                        put("loss", r.lossPct)
                        put("rank", r.rank)
                    })
                }
                doc.put("results", arr)
                doc.put("ts", System.currentTimeMillis())
            }
        }
    }

    private fun loadCached(): List<DnsBenchResult> {
        return runCatching {
            val arr = store.getArray(Store.D_BENCH, "results")
            if (arr.length() == 0) return emptyList()
            val catalog = com.netpilot.mobile.core.AppGraph.dnsCatalog
            val rows = ArrayList<DnsBenchResult>(arr.length())
            for (i in 0 until arr.length()) {
                val o = arr.getJSONObject(i)
                val entry = catalog.find(o.optString("id")) ?: continue
                rows.add(
                    DnsBenchResult(
                        entry = entry,
                        probes = emptyList(),
                        avgMs = o.optInt("avg", -1),
                        jitterMs = o.optInt("jitter"),
                        lossPct = o.optDouble("loss", 0.0),
                        rank = o.optInt("rank", i + 1)
                    )
                )
            }
            rows.sortedBy { if (it.avgMs <= 0) Int.MAX_VALUE else it.avgMs }
                .mapIndexed { i, r -> r.copy(rank = i + 1) }
        }.getOrElse { emptyList() }
    }
}
