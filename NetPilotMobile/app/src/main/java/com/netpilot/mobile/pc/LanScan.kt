package com.netpilot.mobile.pc

import java.net.Inet4Address
import java.net.InetSocketAddress
import java.net.NetworkInterface
import java.net.Socket
import java.util.Collections
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean

/**
 * Finds the desktop bridge on the LAN so nobody has to type an IP address.
 *
 * The Windows side speaks HTTP only (`GET /api/v1/ping` on TCP 8787) and has no UDP
 * listener, so a broadcast/mDNS handshake with it is impossible without changing the
 * desktop. Discovery therefore works the way every tethering client finds its peer:
 * a *bounded* connect-scan of the phone's own /24, then an HTTP confirmation that the
 * answering port really is NetPilot (something else may be listening on 8787).
 *
 * Pure `java.net` — no Android types — so the scan logic runs in JVM unit tests.
 */
object LanScan {

    const val DEFAULT_PORT = 8787

    /** Threads in flight. 64 × 400 ms covers a /24 in well under two seconds. */
    private const val WORKERS = 64
    private const val CONNECT_TIMEOUT_MS = 400

    /** Enough open ports to cover a busy /24 without turning the scan into a port-knock. */
    private const val MAX_HITS = 8

    // ------------------------------------------------------------------ addresses

    /**
     * IPv4 addresses of interfaces that could host a LAN peer, private ranges first:
     * Wi-Fi (192.168/10/172.16), hotspot (192.168.43.1 / 192.168.137.1) and USB tethering
     * all live there. A cellular CGNAT address is kept only as a last resort, because the
     * PC can never be a peer of it.
     */
    fun localAddresses(): List<String> = runCatching {
        val all = Collections.list(NetworkInterface.getNetworkInterfaces())
            .filter { it.isUp && !it.isLoopback }
            .flatMap { Collections.list(it.inetAddresses) }
            .filterIsInstance<Inet4Address>()
            .filterNot { it.isLoopbackAddress }
            .mapNotNull { it.hostAddress }
            .distinct()
        all.filter { isPrivate(it) }.ifEmpty { all }
    }.getOrDefault(emptyList())

    /** 10/8, 172.16/12 and 192.168/16 — the ranges a PC can actually sit behind. */
    fun isPrivate(ip: String): Boolean {
        val p = ip.split(".")
        if (p.size != 4) return false
        val a = p[0].toIntOrNull() ?: return false
        val b = p[1].toIntOrNull() ?: return false
        return a == 10 || (a == 192 && b == 168) || (a == 172 && b in 16..31)
    }

    /**
     * The .0/24 of [self] as host strings, minus [self] itself and minus the network and
     * broadcast addresses (those never answer a TCP connect, and probing them just costs
     * a timeout).
     */
    fun subnetOf(self: String): List<String> {
        val p = self.split(".")
        if (p.size != 4) return emptyList()
        val a = p[0].toIntOrNull() ?: return emptyList()
        val b = p[1].toIntOrNull() ?: return emptyList()
        val c = p[2].toIntOrNull() ?: return emptyList()
        val d = p[3].toIntOrNull() ?: return emptyList()
        if (a !in 1..223 || b !in 0..255 || c !in 0..255 || d !in 0..255) return emptyList()
        return (1..254).map { "$a.$b.$c.$it" }.filter { it != self }
    }

    /** Every /24 the phone belongs to, de-duplicated. */
    fun candidates(): List<String> =
        localAddresses().flatMap { subnetOf(it) }.distinct()

    // ------------------------------------------------------------------ probing

    /**
     * Connect-probes [hosts] on [port] and returns the ones that accepted, capped at
     * [maxHits]. Never blocks longer than roughly `timeoutMs` past the last socket: the scan
     * is a convenience, so it must not be able to hang the caller.
     *
     * The cap bounds the work, it is not a "stop at the first hit" switch: returning a single
     * host meant discovery gave up whenever something *other* than the bridge (a printer, a
     * TV) answered first, and the phone reported "something is listening but it is not
     * NetPilot" while the real bridge was three addresses away.
     */
    fun probe(
        hosts: List<String>,
        port: Int,
        timeoutMs: Int = CONNECT_TIMEOUT_MS,
        workers: Int = WORKERS,
        maxHits: Int = MAX_HITS
    ): List<String> {
        if (hosts.isEmpty() || port !in 1..65535) return emptyList()
        val hits = CopyOnWriteArrayList<String>()
        val full = AtomicBoolean(false)
        val pool = Executors.newFixedThreadPool(workers.coerceIn(1, 128))
        try {
            hosts.forEach { host ->
                pool.execute {
                    if (full.get()) return@execute
                    try {
                        Socket().use { s -> s.connect(InetSocketAddress(host, port), timeoutMs) }
                        if (hits.size >= maxHits) {
                            full.set(true)
                        } else {
                            hits.add(host)
                            if (hits.size >= maxHits) full.set(true)
                        }
                    } catch (_: Throwable) {
                        // closed port / unreachable host — both are normal in a scan
                    }
                }
            }
        } finally {
            pool.shutdown()
            pool.awaitTermination(timeoutMs * 3L + 1_500L, TimeUnit.MILLISECONDS)
            if (!pool.isTerminated) pool.shutdownNow()
        }
        return hits
    }
}
