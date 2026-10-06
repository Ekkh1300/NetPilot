package com.netpilot.mobile.pc

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.io.ByteArrayOutputStream
import java.io.InputStream
import java.io.OutputStream
import java.net.InetAddress
import java.net.ServerSocket
import java.net.Socket
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicLong

/**
 * Minimal LAN HTTP proxy that lets the Windows PC ride this phone's tunnel.
 *
 * The phone is the one holding the VpnService route, so resolving + forwarding from
 * here is what actually gives the PC the phone's DNS answer and its exit path.
 *
 *  - absolute-URI `GET/POST/...` requests are forwarded directly (plain HTTP),
 *  - `CONNECT host:port` is tunnelled byte-for-byte (HTTPS),
 *  - hostnames are resolved with the resolver currently applied in the tunnel, so the
 *    PC gets the same DNS answer the phone would get.
 *
 * The socket is bound to the wildcard address, because a listener bound to a LAN address
 * whose interface happens to be down accepts nothing at all (the client's connect completes
 * and the connection is then never served) - see [start]. It only exists while sharing is
 * switched on, and one client per thread at a time with a hard idle timeout: nothing here can
 * wedge the process.
 */
object ProxyServer {

    const val DEFAULT_PORT = 8788
    /**
     * Concurrent connections.
     *
     * A browser opens six at a time and the desktop reuses a small fixed set, so a working
     * client never approaches this. It is a ceiling against a device being pinned, not a budget
     * for real traffic.
     */
    const val MAX_OPEN_CONNECTIONS = 64

    private val running = AtomicBoolean(false)
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    private val _port = java.util.concurrent.atomic.AtomicInteger(0)
    val port: Int get() = _port.get()

    val isRunning: Boolean get() = running.get()

    /** How long an upstream connection may sit idle before the relay gives up on it. */
    private const val IDLE_TIMEOUT_MS = 120_000

    private val served = AtomicLong(0L)
    fun servedConnections(): Long = served.get()

    /** Connections currently being handled. See the cap in the accept loop. */
    private val openConnections = AtomicInteger(0)

    /** Refused because [MAX_OPEN_CONNECTIONS] was reached. Surfaced so it is not invisible. */
    private val refused = AtomicLong(0L)

    /**
     * Connections refused for lack of capacity. A climbing count means something is opening
     * them without using them, which is worth showing rather than silently shedding.
     */
    fun refusedConnections(): Long = refused.get()

    /**
     * Injected by the VPN service; puts an outbound socket *into* the tunnel.
     *
     * The service excludes its own package from the VPN (the anti-loop rule that stops the
     * relay from looping through itself), so every socket this proxy opened would otherwise
     * leave through the phone's ordinary interface. The PC would still get internet, but
     * through the phone's real IP and outside its VPN - which is exactly the bug this
     * exists for: the tunnel has to carry the desktop's traffic too. protect() is the only
     * way back in, and it is valid only while a tunnel is up; when it is not, the socket
     * simply goes out the normal way (fail open, never break the desktop's browsing).
     */
    internal var protectSocket: ((Socket) -> Boolean)? = null
    internal var protectDatagram: ((java.net.DatagramSocket) -> Boolean)? = null

    private fun openSocket(): Socket = Socket().also { s -> runCatching { protectSocket?.invoke(s) } }
    private fun openDatagram(): java.net.DatagramSocket =
        java.net.DatagramSocket().also { d -> runCatching { protectDatagram?.invoke(d) } }

    @Volatile
    private var server: ServerSocket? = null

    @Volatile
    private var boundHost: String = ""

    @Volatile
    private var boundWildcard = false

    /** "host:port" the desktop should put into WinHTTP, or "" when not running. */
    fun endpoint(): String =
        if (isRunning && boundHost.isNotBlank()) "$boundHost:${_port.get()}" else ""

    fun localEndpoint(): String =
        if (isRunning) "127.0.0.1:${_port.get()}" else ""

    /** True when the listener could only be opened on the wildcard (see [start]). */
    fun isWildcardBound(): Boolean = boundWildcard

    /** Start on [port]. Returns false when the port is taken or the address is missing. */
    fun start(port: Int = DEFAULT_PORT): Boolean {
        if (running.get()) return true
        return try {
            // The wildcard, deliberately. Binding the LAN address looked tighter, but a
            // server bound to an address whose interface is down accepts nothing at all:
            // the client's connect() completes (the stack loops it back locally) and then the
            // connection simply sits in the queue forever - verified on a machine whose WLAN
            // had dropped to "disconnected" while keeping its old address. A proxy that only
            // works on a machine with a pristine network is worse than one that works.
            // The exposure is bounded by what sharing is: the socket only exists while the
            // user has the tunnel up and sharing switched on, and it is closed with it.
            val ss = ServerSocket(port, 50, InetAddress.getByName("0.0.0.0"))
            server = ss
            _port.set(ss.localPort)
            boundWildcard = true
            // What the desktop is told to dial: the LAN address when there is one, so the
            // PC reaches the phone across the LAN/hotspot/tether instead of loopback.
            boundHost = localAddress() ?: "127.0.0.1"
            running.set(true)

            scope.launch {
                while (running.get() && isActive) {
                    val client = try {
                        ss.accept()
                    } catch (t: Throwable) {
                        // An accept failure (network interface torn down, fd limit reached)
                        // used to break out of the loop while `running` stayed true: the
                        // desktop kept being told a live endpoint that nothing was listening
                        // on, and start() refused to bring it back because it short-circuits
                        // on `running`. Give the state back instead of lying about it.
                        if (running.get()) stop()
                        break
                    }

                    // A cap on concurrent connections, not on the accept backlog.
                    //
                    // The backlog only bounds how many completed handshakes the kernel queues
                    // before it starts refusing; it does nothing once the connections are
                    // accepted. The socket is bound to 0.0.0.0, so anything on the same network
                    // can open one, and each is held for up to 20 s by the read timeout - so a
                    // few hundred idle opens from one host is enough to exhaust the phone's file
                    // descriptors and take the tunnel down with it. That is the whole attack, and
                    // it needs no credentials because the proxy is designed to serve the desktop.
                    //
                    // Refused rather than queued: the desktop opens a handful of connections and
                    // reusing the same ones, so a full queue means the desktop itself is misbehaving
                    // or the limit is set far too low, and neither is improved by waiting.
                    if (openConnections.get() >= MAX_OPEN_CONNECTIONS) {
                        refused.incrementAndGet()
                        runCatching { client.close() }
                        continue
                    }

                    openConnections.incrementAndGet()
                    served.incrementAndGet()
                    launch {
                        try {
                            handle(client)
                        } finally {
                            // Decremented on every path out, including an exception. Leaving it
                            // to be inferred from the socket closing would leak a slot per crash,
                            // and the proxy would reach its own limit having served nothing.
                            openConnections.decrementAndGet()
                        }
                    }
                }
            }
            boundHost.isNotBlank()
        } catch (t: Throwable) {
            running.set(false)
            false
        }
    }

    fun stop() {
        running.set(false)
        runCatching { server?.close() }
        server = null
        _port.set(0)
        boundHost = ""
        // Zeroed here as well as by each handler's finally. Every in-flight handler is about to
        // be interrupted by the scope going away, but that is not immediate - and if a start
        // followed quickly, the old count would otherwise still be occupying slots and the new
        // proxy would refuse connections that are perfectly good.
        openConnections.set(0)
    }

    // ------------------------------------------------------------------ connection

    private suspend fun handle(client: Socket) = withContext(Dispatchers.IO) {
        try {
            client.soTimeout = 20_000
            client.tcpNoDelay = true
            val input = BufferedInputStream(client.getInputStream())
            val output = BufferedOutputStream(client.getOutputStream())

            val head = readHead(input) ?: return@withContext
            val first = head.lineSequence().firstOrNull()?.trim().orEmpty()
            val parts = first.split(" ")
            if (parts.size < 3) {
                client.close()
                return@withContext
            }
            val method = parts[0].uppercase()
            val target = parts[1]

            if (method == "CONNECT") {
                tunnel(target, input, output)
            } else {
                forward(method, target, head, input, output)
            }
            output.flush()
        } catch (t: Throwable) {
            // A broken client is normal on a shared proxy; just drop it.
        } finally {
            runCatching { client.close() }
        }
    }

    /** CONNECT host:port — a raw byte relay once the upstream socket is up. */
    private fun tunnel(target: String, input: InputStream, output: OutputStream) {
        val (host, port) = splitHostPort(target, 443)
        val up = try {
            openSocket().apply {
                connect(java.net.InetSocketAddress(resolve(host), port), 6000)
                // An idle tunnel has to outlive a page load. This was 20 s - shorter than any
                // keep-alive a browser or a websocket client holds - so idle HTTPS
                // connections were torn down mid-session and had to redial, and websockets
                // died on a timer. Still bounded, because a sleeping peer must not pin a
                // thread forever and the accept backlog is only 50 deep.
                soTimeout = IDLE_TIMEOUT_MS
                tcpNoDelay = true
            }
        } catch (t: Throwable) {
            output.write("HTTP/1.1 502 Bad Gateway\r\n\r\n".toByteArray())
            return
        }

        try {
            output.write("HTTP/1.1 200 Connection established\r\n\r\n".toByteArray())
            output.flush()
            relay(input, up.getInputStream(), output, up.getOutputStream())
        } finally {
            runCatching { up.close() }
        }
    }

    /** Plain HTTP: rebuild an origin-form request and relay the response. */
    private fun forward(
        method: String,
        target: String,
        head: String,
        input: InputStream,
        output: OutputStream
    ) {
        val (host, port, path) = splitAbsolute(target)
        val up = try {
            openSocket().apply {
                connect(java.net.InetSocketAddress(resolve(host), port), 6000)
                soTimeout = IDLE_TIMEOUT_MS
                tcpNoDelay = true
            }
        } catch (t: Throwable) {
            output.write("HTTP/1.1 502 Bad Gateway\r\n\r\n".toByteArray())
            return
        }

        try {
            val outHead = buildString {
                var first = true
                for (line in head.lineSequence()) {
                    val l = line.trim()
                    if (l.isEmpty()) break
                    if (first) {
                        append("$method $path HTTP/1.1\r\n")
                        first = false
                        continue
                    }
                    // The proxy's own framing headers must not be forwarded.
                    val name = l.substringBefore(":").lowercase()
                    if (name == "proxy-connection" || name == "connection") continue
                    append(l).append("\r\n")
                }
                append("\r\n")
            }
            val us = up.getOutputStream()
            us.write(outHead.toByteArray(Charsets.ISO_8859_1))
            us.flush()

            relay(input, up.getInputStream(), output, us)
        } finally {
            runCatching { up.close() }
        }
    }

    /**
     * Client ⇄ upstream pump. Stops at the first end-of-stream or socket timeout so a
     * sleeping browser cannot pin a thread forever.
     */
    private fun relay(
        clientIn: InputStream,
        upIn: InputStream,
        clientOut: OutputStream,
        upOut: OutputStream
    ) {
        val toUp = Thread {
            try {
                copy(clientIn, upOut)
            } catch (t: Throwable) {
            } finally {
                runCatching { upOut.flush() }
            }
        }.apply { isDaemon = true; name = "np-proxy-up"; start() }

        try {
            copy(upIn, clientOut)
            clientOut.flush()
        } catch (t: Throwable) {
        } finally {
            toUp.interrupt()
        }
    }

    private fun copy(src: InputStream, dst: OutputStream) {
        val buf = ByteArray(16_384)
        while (true) {
            val n = src.read(buf)
            if (n < 0) break
            if (n == 0) continue
            dst.write(buf, 0, n)
            dst.flush()
        }
    }

    /**
     * Reads the request head up to the blank line (\r\n\r\n, tolerated as \n\n).
     * Bounded at 16 KB so a hostile client cannot grow the buffer without limit.
     */
    private fun readHead(input: InputStream): String? {
        val out = ByteArrayOutputStream(1024)
        var p1 = -1
        var p2 = -1
        var p3 = -1
        while (true) {
            val b = input.read()
            if (b < 0) return if (out.size() > 0) out.toString("ISO-8859-1") else null
            out.write(b)
            val end = (p3 == '\r'.code && p2 == '\n'.code && p1 == '\r'.code && b == '\n'.code) ||
                    (p1 == '\n'.code && b == '\n'.code)
            if (end) break
            p3 = p2; p2 = p1; p1 = b
            if (out.size() > 16_384) break
        }
        val text = out.toString("ISO-8859-1")
        return if (text.isBlank()) null else text
    }

    // ------------------------------------------------------------------ helpers

    /** Resolve through the resolver the tunnel currently uses (falls back to the system). */
    private fun resolve(host: String): InetAddress {
        if (host.matches(Regex("^\\d{1,3}(\\.\\d{1,3}){3}$"))) {
            return InetAddress.getByName(host)
        }
        val catalog = com.netpilot.mobile.core.AppGraph.dnsCatalog
        val server = catalog.find(com.netpilot.mobile.vpn.VpnState.dnsId.value)?.primary
            ?: com.netpilot.mobile.net.Monitor.link.value.dnsServers.firstOrNull()

        if (server != null) {
            runCatching {
                val ip = java.net.InetAddress.getByName(server)
                val q = openDatagram()
                try {
                    q.soTimeout = 1500
                    val msg = buildQuery(host)
                    q.send(
                        java.net.DatagramPacket(
                            msg, msg.size,
                            java.net.InetSocketAddress(ip, 53)
                        )
                    )
                    val buf = ByteArray(2048)
                    val pkt = java.net.DatagramPacket(buf, buf.size)
                    q.receive(pkt)
                    val a = parseAAnswer(pkt.data, pkt.length)
                    if (a != null) return java.net.InetAddress.getByName(a)
                } finally {
                    q.close()
                }
            }
        }
        return InetAddress.getByName(host)
    }

    private fun splitHostPort(target: String, defPort: Int): Pair<String, Int> {
        val idx = target.lastIndexOf(':')
        if (idx <= 0) return target to defPort
        val h = target.substring(0, idx)
        val p = target.substring(idx + 1).toIntOrNull() ?: return target to defPort
        return h to p
    }

    private data class Abs(val host: String, val port: Int, val path: String)

    /** Absolute-form request target (`http://host[:port]/path`) → origin parts. */
    private fun splitAbsolute(target: String): Abs {
        val rest = target.substringAfter("://", target)
        val slash = rest.indexOf('/')
        val authority = if (slash >= 0) rest.substring(0, slash) else rest
        val path = if (slash >= 0) rest.substring(slash) else "/"
        val (h, p) = splitHostPort(authority, 80)
        return Abs(h, p, path)
    }

    private fun localAddress(): String? = runCatching {
        val interfaces = java.util.Collections.list(
            java.net.NetworkInterface.getNetworkInterfaces()
        )
        interfaces
            .firstOrNull { ni ->
                ni.isUp && !ni.isLoopback && java.util.Collections.list(ni.inetAddresses)
                    .any { it is java.net.Inet4Address && !it.isLoopbackAddress }
            }
            ?.let { ni -> java.util.Collections.list(ni.inetAddresses) }
            ?.firstOrNull { it is java.net.Inet4Address }
            ?.hostAddress
    }.getOrNull()

    // ------------------------------------------------------------------ tiny DNS client
    // Only A records are needed to turn a hostname into a connect() target.

    private fun buildQuery(name: String): ByteArray {
        val out = ByteArrayOutputStream()
        val id = (System.nanoTime() and 0xFFFF).toInt()
        out.write((id shr 8) and 0xFF); out.write(id and 0xFF)
        out.write(0x01); out.write(0x00)   // RD
        out.write(0x00); out.write(0x01)   // QD
        out.write(0x00); out.write(0x00)   // AN
        out.write(0x00); out.write(0x00)   // NS
        out.write(0x00); out.write(0x00)   // AR
        for (label in name.trim().trim('.').split(".")) {
            val b = label.toByteArray(Charsets.UTF_8)
            out.write(b.size)
            out.write(b)
        }
        out.write(0x00)
        out.write(0x00); out.write(0x01)   // A
        out.write(0x00); out.write(0x01)   // IN
        return out.toByteArray()
    }

    private fun parseAAnswer(msg: ByteArray, len: Int): String? {
        if (len < 12) return null
        val an = ((msg[6].toInt() and 0xFF) shl 8) or (msg[7].toInt() and 0xFF)
        if (an <= 0) return null
        var pos = 12
        // skip question
        while (pos < len && msg[pos] != 0.toByte()) {
            pos += (msg[pos].toInt() and 0xFF) + 1
        }
        pos += 5   // 0x00 + type + class
        for (i in 0 until an) {
            if (pos >= len) return null
            // name (may be a compression pointer)
            val first = msg[pos].toInt() and 0xFF
            if (first and 0xC0 == 0xC0) pos += 2 else {
                while (pos < len && msg[pos] != 0.toByte()) {
                    pos += (msg[pos].toInt() and 0xFF) + 1
                }
                pos++
            }
            if (pos + 10 > len) return null
            val type = ((msg[pos].toInt() and 0xFF) shl 8) or (msg[pos + 1].toInt() and 0xFF)
            val rdlen = ((msg[pos + 8].toInt() and 0xFF) shl 8) or (msg[pos + 9].toInt() and 0xFF)
            pos += 10
            if (pos + rdlen > len) return null
            if (type == 1 && rdlen == 4) {
                return "${msg[pos].toInt() and 0xFF}.${msg[pos + 1].toInt() and 0xFF}." +
                        "${msg[pos + 2].toInt() and 0xFF}.${msg[pos + 3].toInt() and 0xFF}"
            }
            pos += rdlen
        }
        return null
    }
}
