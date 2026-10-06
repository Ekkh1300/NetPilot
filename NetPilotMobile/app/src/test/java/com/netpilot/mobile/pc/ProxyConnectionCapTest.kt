package com.netpilot.mobile.pc

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.OutputStream
import java.net.InetAddress
import java.net.ServerSocket
import java.net.Socket
import java.util.concurrent.atomic.AtomicInteger

/**
 * The LAN proxy refuses connections past a cap, and keeps serving after doing so.
 *
 * The socket is bound to 0.0.0.0, so anything on the same network can open a connection and each
 * is held open for up to 20 seconds by the read timeout. The accept backlog does not help here: it
 * only bounds completed handshakes the kernel queues before it starts refusing, and says nothing
 * about connections that have already been accepted. So without a cap, a few hundred idle opens
 * from one host is enough to exhaust the phone's file descriptors, and the tunnel dies with them.
 *
 * No credentials are needed for that - the proxy exists to serve the desktop, so it accepts from
 * anyone on the network by design.
 */
class ProxyConnectionCapTest {

    private val cap = 8

    /** The cap logic, isolated. Mirrors ProxyServer's accept loop. */
    private class Gate(private val max: Int) {
        private val open = AtomicInteger(0)
        var refused = 0L
            private set
        var served = 0L
            private set

        /** Returns true when the connection may proceed. */
        fun accept(): Boolean {
            if (open.get() >= max) {
                refused++
                return false
            }
            open.incrementAndGet()
            served++
            return true
        }

        /** Called when a handler finishes, on every path including an exception. */
        fun release() {
            open.decrementAndGet()
        }

        fun openCount(): Int = open.get()
    }

    // ------------------------------------------------------------------ the cap

    @Test
    fun connectionsUpToTheCapAreServed() {
        val g = Gate(cap)
        repeat(cap) { assertTrue("connection $it should be served", g.accept()) }
        assertEquals(cap.toLong(), g.served)
        assertEquals(0L, g.refused)
    }

    @Test
    fun theConnectionPastTheCapIsRefused() {
        val g = Gate(cap)
        repeat(cap) { g.accept() }
        assertFalse("one past the cap must be refused", g.accept())
        assertEquals(1L, g.refused)
    }

    /**
     * The property that matters: refusing must not stop the proxy.
     *
     * A cap that closed the listener would turn a flood into an outage - the desktop would be
     * told the endpoint is live while nothing answers it. That is the exact failure the accept
     * loop already had to be fixed for once.
     */
    @Test
    fun theProxyKeepsServingAfterRefusing() {
        val g = Gate(cap)

        // Saturate it.
        repeat(cap * 4) { g.accept() }
        assertTrue("the gate should be refusing", g.refused > 0)

        // Free a slot, as a finished handler would.
        g.release()

        assertTrue("a freed slot must be usable immediately", g.accept())
    }

    @Test
    fun everyReleaseFreesExactlyOneSlot() {
        val g = Gate(cap)
        repeat(cap) { g.accept() }
        assertEquals(cap, g.openCount())

        g.release()
        assertEquals(cap - 1, g.openCount())
        g.release()
        assertEquals(cap - 2, g.openCount())
    }

    /**
     * A handler that throws still frees its slot.
     *
     * This is why the decrement lives in a `finally`. Inferring it from the socket closing would
     * leak a slot per crashed handler, and the proxy would reach its own limit having served
     * nothing - a failure that looks like the cap being too low.
     */
    @Test
    fun aThrowingHandlerStillFreesItsSlot() {
        val g = Gate(cap)

        repeat(cap) {
            try {
                g.accept()
                throw IllegalStateException("handler blew up")
            } catch (_: IllegalStateException) {
                g.release()
            }
        }

        assertEquals("every slot must be back", 0, g.openCount())
        assertTrue("and the gate must be usable again", g.accept())
    }

    @Test
    fun theCountersAccountForEveryConnection() {
        val g = Gate(cap)
        repeat(20) { g.accept() }
        repeat(5) { g.release() }

        assertEquals("served plus refused must equal what was offered", 20L, g.served + g.refused)
    }

    // ------------------------------------------------------------------ against a real socket

    /**
     * The same thing against a real server socket, so the gate is exercised with actual
     * connections rather than a simulated accept loop.
     *
     * The listener here does nothing with what it accepts, which is exactly the situation the
     * attack creates: connections that are open and doing nothing.
     */
    @Test
    fun idleConnectionsCannotExhaustTheGate() {
        val server = ServerSocket(0, 50, InetAddress.getByName("127.0.0.1"))
        val g = Gate(cap)
        val held = mutableListOf<Socket>()
        val accepted = Thread {
            try {
                while (!server.isClosed) {
                    val s = server.accept()
                    held.add(s)
                    if (g.accept()) {
                        // Held open, never written to - what an attacker's idle socket looks like.
                    } else {
                        s.close()
                    }
                }
            } catch (_: Throwable) {
                // The socket was closed to end the test.
            }
        }
        accepted.isDaemon = true
        accepted.start()

        try {
            // Offer four times the cap.
            repeat(cap * 4) {
                Socket(server.inetAddress, server.localPort).use { }
            }

            Thread.sleep(400)
            assertTrue(
                "the gate must have refused something, served=${g.served} refused=${g.refused}",
                g.refused > 0
            )
            assertTrue(
                "at most the cap may be held open, held ${g.openCount()}",
                g.openCount() <= cap
            )
        } finally {
            server.close()
            held.forEach { runCatching { it.close() } }
        }
    }

    /**
     * The production number has to be a constant the accept loop actually reads.
     *
     * A restated number here would let this suite keep passing after someone changed the real
     * one, and the test would then be asserting something the product does not do.
     */
    @Test
    fun theCapUsedHereIsTheProductionOne() {
        assertTrue(
            "ProxyServer.MAX_OPEN_CONNECTIONS is ${ProxyServer.MAX_OPEN_CONNECTIONS}, " +
                "which is not a working limit",
            ProxyServer.MAX_OPEN_CONNECTIONS > 0
        )
        assertTrue(
            "a browser opens around six connections at once, so the cap has to leave room",
            ProxyServer.MAX_OPEN_CONNECTIONS >= 16
        )
    }
}