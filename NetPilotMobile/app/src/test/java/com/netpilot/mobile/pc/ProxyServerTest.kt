package com.netpilot.mobile.pc

import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import java.io.ByteArrayOutputStream
import java.io.InputStream
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.util.concurrent.atomic.AtomicReference

/**
 * The LAN proxy the PC points WinHTTP at — the real "PC rides the phone" data path.
 *
 * When the desktop answers `/share` it only runs
 * `netsh winhttp set proxy proxy-server="<phone>:8788"`; everything after that lands
 * here. So what has to be proven is the wire behaviour: CONNECT must tunnel HTTPS bytes
 * untouched, an absolute-URI request must be rewritten to origin form and forwarded, and
 * the listener must start/stop cleanly. Real sockets, real relay, JVM only.
 */
class ProxyServerTest {

    private val workers = java.util.concurrent.CopyOnWriteArrayList<Thread>()
    private var echo: ServerSocket? = null
    private var origin: ServerSocket? = null
    private val originLine = AtomicReference("")

    @Before
    fun setUp() {
        ProxyServer.stop()
    }

    @After
    fun tearDown() {
        ProxyServer.stop()
        runCatching { echo?.close() }
        runCatching { origin?.close() }
        echo = null
        origin = null
        workers.forEach { it.interrupt() }
        workers.clear()
    }

    // ------------------------------------------------------------------ tests

    @Test
    fun connectTunnelsBytesBothWays() {
        val target = startEcho()
        val host = startProxy()

        Socket(host, ProxyServer.port).use { client ->
            client.soTimeout = 5_000
            val out = client.getOutputStream()
            val input = client.getInputStream()

            out.write(
                "CONNECT 127.0.0.1:$target HTTP/1.1\r\nHost: 127.0.0.1:$target\r\n\r\n"
                    .toByteArray()
            )
            out.flush()

            val head = readHead(input)
            assertTrue("proxy refused the tunnel: $head", head.startsWith("HTTP/1.1 200"))

            val payload = "through-the-phone-tunnel"
            out.write(payload.toByteArray())
            out.flush()
            assertEquals(payload, readExact(input, payload.length))
        }
    }

    @Test
    fun absoluteUriIsRewrittenAndForwarded() {
        val body = "hello-from-the-origin"
        val target = startOrigin(body)
        val host = startProxy()

        Socket(host, ProxyServer.port).use { client ->
            client.soTimeout = 5_000
            val out = client.getOutputStream()
            val input = client.getInputStream()

            out.write(
                (
                    "GET http://127.0.0.1:$target/some/path?a=1 HTTP/1.1\r\n" +
                            "Host: 127.0.0.1:$target\r\n" +
                            "Proxy-Connection: keep-alive\r\n\r\n"
                    ).toByteArray()
            )
            out.flush()

            val response = readAll(input)
            assertTrue("no status line: $response", response.contains("HTTP/1.1 200"))
            assertTrue("body lost: $response", response.contains(body))
            // The origin must see origin-form, not the absolute URI a proxy client sends.
            assertEquals("GET /some/path?a=1 HTTP/1.1", originLine.get())
        }
    }

    @Test
    fun startStopAreIdempotentAndReleaseThePort() {
        startProxy()
        val port = ProxyServer.port
        assertTrue(port > 0)
        assertTrue(ProxyServer.endpoint().endsWith(":$port"))
        // A second start must not rebind or move the port the desktop was told about.
        ProxyServer.start(0)
        assertEquals(port, ProxyServer.port)

        ProxyServer.stop()
        assertFalse(ProxyServer.isRunning)
        assertEquals(0, ProxyServer.port)
        assertEquals("", ProxyServer.endpoint())
    }

    // ------------------------------------------------------------------ harness

    /**
     * Starts the proxy and returns the host the client should dial.
     *
     * Loopback, deliberately: the listener is on the wildcard (see ProxyServer.start) and the
     * advertised LAN address is not necessarily routable on the machine running the test - a
     * down interface with a stale address accepts nothing, which is exactly the trap this
     * test would otherwise walk into instead of testing the proxy.
     */
    private fun startProxy(): String {
        ProxyServer.start(0)
        assertTrue("proxy did not bind", ProxyServer.isRunning)
        assertTrue("proxy has no port", ProxyServer.port > 0)
        assertTrue("loopback must be reachable", reachable("127.0.0.1", ProxyServer.port))
        return "127.0.0.1"
    }

    private fun reachable(host: String, port: Int): Boolean = try {
        Socket().use { it.connect(InetSocketAddress(host, port), 1_500) }
        true
    } catch (_: Throwable) {
        false
    }

    /** Echo server: whatever the proxy relays in must come back out. */
    private fun startEcho(): Int {
        val s = ServerSocket(0, 50, InetAddress.getByName("127.0.0.1"))
        echo = s
        daemon("np-test-echo") {
            while (!Thread.currentThread().isInterrupted) {
                val c = try {
                    s.accept()
                } catch (_: Throwable) {
                    break
                }
                daemon("np-test-echo-conn") {
                    try {
                        c.use { sock ->
                            val i = sock.getInputStream()
                            val o = sock.getOutputStream()
                            val buf = ByteArray(4_096)
                            while (true) {
                                val n = i.read(buf)
                                if (n < 0) break
                                o.write(buf, 0, n)
                                o.flush()
                            }
                        }
                    } catch (_: Throwable) {
                    }
                }
            }
        }
        return s.localPort
    }

    /** Origin HTTP server that records the request line it received. */
    private fun startOrigin(body: String): Int {
        val s = ServerSocket(0, 50, InetAddress.getByName("127.0.0.1"))
        origin = s
        daemon("np-test-origin") {
            while (!Thread.currentThread().isInterrupted) {
                val c = try {
                    s.accept()
                } catch (_: Throwable) {
                    break
                }
                daemon("np-test-origin-conn") {
                    try {
                        c.use { sock ->
                            val head = readHead(sock.getInputStream())
                            originLine.set(head.lineSequence().firstOrNull()?.trim().orEmpty())
                            val payload = body.toByteArray()
                            val o = sock.getOutputStream()
                            o.write(
                                ("HTTP/1.1 200 OK\r\nContent-Length: ${payload.size}\r\n" +
                                        "Connection: close\r\n\r\n").toByteArray()
                            )
                            o.write(payload)
                            o.flush()
                        }
                    } catch (_: Throwable) {
                    }
                }
            }
        }
        return s.localPort
    }

    private fun daemon(name: String, block: () -> Unit): Thread =
        Thread({ block() }, name).apply {
            isDaemon = true
            start()
            workers.add(this)
        }

    private fun readHead(input: InputStream): String {
        val sb = StringBuilder()
        while (sb.length < 8_192) {
            val b = input.read()
            if (b < 0) break
            sb.append(b.toChar())
            if (sb.toString().endsWith("\r\n\r\n")) break
        }
        return sb.toString()
    }

    private fun readExact(input: InputStream, count: Int): String {
        val buf = ByteArray(count)
        var off = 0
        while (off < count) {
            val n = input.read(buf, off, count - off)
            if (n < 0) break
            off += n
        }
        return String(buf, 0, off)
    }

    private fun readAll(input: InputStream): String {
        val bytes = ByteArrayOutputStream()
        val buf = ByteArray(4_096)
        while (true) {
            val n = input.read(buf)
            if (n < 0) break
            bytes.write(buf, 0, n)
        }
        return bytes.toString("ISO-8859-1")
    }
}
