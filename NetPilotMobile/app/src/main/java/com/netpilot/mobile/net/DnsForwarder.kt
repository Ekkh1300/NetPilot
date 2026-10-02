package com.netpilot.mobile.net

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.withContext
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetSocketAddress
import java.net.Socket
import java.util.concurrent.ConcurrentHashMap

/**
 * Raw DNS forwarding used by the tunnel: takes a client's query exactly as it came off
 * the wire, sends it to the configured resolver and returns the answer bytes untouched.
 *
 * A tiny response cache absorbs repeated lookups (browsers hammer the same names) and
 * gives the dashboard real cache-hit numbers.
 */
object DnsForwarder {

    /**
     * Sockets created here must be excluded from the tunnel, otherwise the forwarder would
     * send its own packets straight back into the TUN and black-hole them. The VPN service
     * installs these hooks from its onCreate().
     */
    @Volatile
    var onUdpSocket: ((DatagramSocket) -> Unit)? = null

    @Volatile
    var onTcpSocket: ((Socket) -> Unit)? = null

    private data class CacheEntry(
        val payload: ByteArray,
        val at: Long,
        val ttl: Long
    )

    private val cache = ConcurrentHashMap<String, CacheEntry>()
    private const val MAX_CACHE = 256
    private const val MIN_TTL_MS = 5_000L
    private const val MAX_TTL_MS = 60_000L

    /**
     * Resolvers the tunnel captured from the real network before it went up, used when the
     * selected one does not answer.
     *
     * Without this a resolver that is unreachable (a captive portal, a VPN that filters port
     * 53, a typo in a custom entry) means *every* lookup times out - applying a DNS would
     * take the whole phone offline. Falling back keeps the internet working; the app simply
     * resolves through the old server, which is what it did before the change.
     */
    @Volatile
    var fallbackServers: List<String> = emptyList()

    /** A dead fallback must not double the time a client waits for its answer. */
    private const val FALLBACK_TIMEOUT_MS = 1500

    private fun key(query: ByteArray): String? {
        if (query.size < 14) return null
        // Skip the 12-byte header + first QNAME (no compression in questions).
        var i = 12
        val sb = StringBuilder(32)
        while (i < query.size) {
            val len = query[i].toInt() and 0xFF
            if (len == 0) { i++; break }
            if (len and 0xC0 != 0) return null
            i++
            if (i + len > query.size) return null
            sb.append(query, i, i + len).append('.')
            i += len
        }
        if (i + 4 > query.size) return null
        val qtype = ((query[i].toInt() and 0xFF) shl 8) or (query[i + 1].toInt() and 0xFF)
        val flags = ((query[2].toInt() and 0xFF) shl 8) or (query[3].toInt() and 0xFF)
        val recursion = (flags and 0x80) != 0
        if (!recursion) return null
        return "$sb|$qtype"
    }

    /**
     * Forward [query] to [server] and return the response.
     * Returns null on timeout / network error (the caller then drops the packet, which the
     * OS turns into a resolver timeout for the app — the same behaviour as a dead resolver).
     */
    suspend fun forward(
        query: ByteArray,
        server: String,
        port: Int = 53,
        timeoutMs: Int = 2500
    ): Pair<ByteArray?, Boolean> = withContext(Dispatchers.IO) {
        val qid = ((query[0].toInt() and 0xFF) shl 8) or (query[1].toInt() and 0xFF)
        val ck = key(query)
        if (ck != null) {
            val hit = cache[ck]
            if (hit != null && System.currentTimeMillis() - hit.at < hit.ttl) {
                val cached = hit.payload.copyOf()
                // Restamp the ID so it matches this client's query.
                cached[0] = ((qid shr 8) and 0xFF).toByte()
                cached[1] = (qid and 0xFF).toByte()
                return@withContext cached to true
            }
        }

        val start = System.currentTimeMillis()

        // Every resolver is asked at the same time and the first answer wins.
        //
        // This used to be a sequential walk: the selected resolver got up to 2.5 s, and only
        // then each system fallback got 1.5 s in turn. On a host where the first resolver
        // silently drops packets - the common case, an ISP resolver that stopped answering -
        // *every* lookup on the device paid that penalty, and with three fallbacks a single
        // page load could spend 7 s before the first byte of DNS came back. Racing turns the
        // worst case from "sum of all timeouts" into "slowest resolver", and a healthy
        // resolver keeps winning the race exactly as before.
        val candidates = ArrayList<Pair<String, Int>>(fallbackServers.size + 1)
        candidates.add(server to timeoutMs)
        for (alt in fallbackServers) {
            if (alt == server) continue
            candidates.add(alt to FALLBACK_TIMEOUT_MS)
        }

        val msg = race(query, candidates, port)
        if (msg == null) return@withContext null to false

        val ms = (System.currentTimeMillis() - start).toInt()
        if (ck != null && msg.size > 12) {
            val ttl = extractMinTtl(msg)
            if (ttl > 0) {
                if (cache.size >= MAX_CACHE) cache.clear()
                cache[ck] = CacheEntry(
                    payload = msg.copyOf(),
                    at = System.currentTimeMillis(),
                    ttl = ttl.coerceIn(MIN_TTL_MS, MAX_TTL_MS)
                )
            }
        }
        msg to false
    }

    /**
     * Asks every candidate resolver at once and returns the first real answer.
     *
     * Polls completion instead of using `select` so it stays on APIs that cannot change
     * under us; the granularity is 10 ms, which is far below anything a user perceives. A
     * loser is cancelled as soon as somebody answers, so a dead resolver costs one query's
     * worth of work rather than a thread per lookup.
     */
    private suspend fun race(
        query: ByteArray,
        candidates: List<Pair<String, Int>>,
        port: Int
    ): ByteArray? = coroutineScope {
        if (candidates.isEmpty()) return@coroutineScope null
        if (candidates.size == 1) {
            return@coroutineScope attempt(query, candidates[0].first, port, candidates[0].second)
        }

        val jobs = candidates.map { (srv, t) ->
            async(Dispatchers.IO) { attempt(query, srv, port, t) }
        }
        val budget = candidates.maxOf { it.second } + 400L
        val deadline = System.currentTimeMillis() + budget

        var answer: ByteArray? = null
        while (answer == null) {
            val done = jobs.filter { it.isCompleted }
            if (done.isNotEmpty()) {
                for (j in done) {
                    val bytes = j.await()
                    if (bytes != null) { answer = bytes; break }
                }
                // Everyone finished and nobody answered - no reason to keep waiting.
                if (answer == null && jobs.all { it.isCompleted }) break
            } else if (System.currentTimeMillis() >= deadline) {
                break
            } else {
                delay(10)
            }
        }
        jobs.forEach { it.cancel() }
        answer
    }

    /** One query/response round trip, answer bytes untouched. */
    private fun attempt(query: ByteArray, server: String, port: Int, timeoutMs: Int): ByteArray? {
        return try {
            DatagramSocket().use { sock ->
                onUdpSocket?.invoke(sock)
                sock.soTimeout = timeoutMs
                sock.send(
                    DatagramPacket(
                        query, query.size,
                        InetSocketAddress(java.net.InetAddress.getByName(server), port)
                    )
                )
                val buf = ByteArray(8192)
                val packet = DatagramPacket(buf, buf.size)
                sock.receive(packet)
                val resp = buf.copyOf(packet.length)

                if (resp.size > 3 && (resp[2].toInt() and 0x02) != 0) {
                    // Truncated: prefer the full answer over TCP, but never throw away a
                    // reply we already have when TCP is refused - a network that filters
                    // port 53/TCP would otherwise turn a working (short) answer into a
                    // resolver timeout for the app.
                    tcpExchange(query, server, port, timeoutMs) ?: resp
                } else {
                    resp
                }
            }
        } catch (t: Throwable) {
            null
        }
    }

    private fun extractMinTtl(msg: ByteArray): Long {
        // Walk the answer section for the smallest TTL (best-effort; 0 means "don't cache").
        try {
            if (msg.size < 12) return 0
            var i = 4
            fun u16(): Int {
                if (i + 1 >= msg.size) throw java.io.EOFException()
                val v = ((msg[i].toInt() and 0xFF) shl 8) or (msg[i + 1].toInt() and 0xFF)
                i += 2
                return v
            }
            val qd = u16(); val an = u16(); u16(); u16()
            repeat(qd) {
                while (i < msg.size) {
                    val len = msg[i].toInt() and 0xFF
                    i++
                    if (len == 0) break
                    if (len and 0xC0 != 0) break
                    i += len
                }
                i += 4
            }
            var min = Long.MAX_VALUE
            repeat(an) {
                while (i < msg.size) {
                    val len = msg[i].toInt() and 0xFF
                    i++
                    if (len == 0) break
                    if (len and 0xC0 != 0) break
                    i += len
                }
                u16(); u16()             // type, class
                // 32-bit TTL: high half shifted by 16, not 32 (which made every TTL above
                // 65535s read as a nonsense value before the clamp hid it).
                val ttl = ((u16().toLong() shl 16) or u16().toLong())
                val rdlen = u16()
                if (ttl in 1 until min) min = ttl
                i += rdlen
            }
            return if (min == Long.MAX_VALUE) 0L else min * 1000L
        } catch (t: Throwable) {
            return 0L
        }
    }

    private fun tcpExchange(
        query: ByteArray,
        server: String,
        port: Int,
        timeoutMs: Int
    ): ByteArray? = try {
        Socket().use { s ->
            onTcpSocket?.invoke(s)
            s.connect(InetSocketAddress(java.net.InetAddress.getByName(server), port), timeoutMs)
            s.soTimeout = timeoutMs
            val out = s.getOutputStream()
            out.write((query.size shr 8) and 0xFF)
            out.write(query.size and 0xFF)
            out.write(query)
            out.flush()
            val inp = s.getInputStream()
            val hi = inp.read(); val lo = inp.read()
            if (hi < 0 || lo < 0) return null
            val len = (hi shl 8) or lo
            val buf = ByteArray(len)
            var read = 0
            while (read < len) {
                val n = inp.read(buf, read, len - read)
                if (n < 0) break
                read += n
            }
            if (read == len) buf else null
        }
    } catch (t: Throwable) {
        null
    }

    /**
     * Plain DNS-over-TCP exchange for a message that arrived length-prefixed on the
     * tunnel's TCP port (no caching: this path only runs when the client asked for it).
     */
    fun forwardTcp(message: ByteArray, server: String, port: Int = 53, timeoutMs: Int = 3000): Pair<ByteArray?, Boolean> =
        try {
            Socket().use { s ->
                onTcpSocket?.invoke(s)
                s.connect(InetSocketAddress(java.net.InetAddress.getByName(server), port), timeoutMs)
                s.soTimeout = timeoutMs
                val out = s.getOutputStream()
                out.write((message.size shr 8) and 0xFF)
                out.write(message.size and 0xFF)
                out.write(message)
                out.flush()
                val inp = s.getInputStream()
                val hi = inp.read(); val lo = inp.read()
                if (hi < 0 || lo < 0) return null to false
                val len = (hi shl 8) or lo
                val buf = ByteArray(len)
                var read = 0
                while (read < len) {
                    val n = inp.read(buf, read, len - read)
                    if (n < 0) break
                    read += n
                }
                if (read == len) buf to false else null to false
            }
        } catch (t: Throwable) {
            null to false
        }

    fun clearCache() = cache.clear()

    fun cacheSize(): Int = cache.size
}
