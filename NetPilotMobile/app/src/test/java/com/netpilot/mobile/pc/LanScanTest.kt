package com.netpilot.mobile.pc

import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.net.ServerSocket

/**
 * LAN discovery, which is what turns "type the PC's IP" into a single button.
 *
 * The interesting properties are the cheap ones to get wrong: the scan must not probe
 * the phone itself or the network/broadcast addresses (a wasted timeout each), must
 * cover the whole /24, must give up quickly when nothing answers, and must actually
 * notice a listener. All of it runs on the JVM against real sockets.
 */
class LanScanTest {

    private var listener: ServerSocket? = null

    @After
    fun tearDown() {
        runCatching { listener?.close() }
        listener = null
    }

    // ------------------------------------------------------------------ subnet

    @Test
    fun subnetCoversTheWhole24() {
        val hosts = LanScan.subnetOf("192.168.1.77")
        assertEquals(253, hosts.size)
        assertEquals("192.168.1.1", hosts.first())
        assertEquals("192.168.1.254", hosts.last())
        assertTrue(hosts.contains("192.168.1.10"))
        assertTrue(hosts.contains("192.168.1.200"))
    }

    @Test
    fun subnetSkipsSelfNetworkAndBroadcast() {
        val hosts = LanScan.subnetOf("10.0.0.5")
        assertFalse(hosts.contains("10.0.0.5"))   // the phone must not probe itself
        assertFalse(hosts.contains("10.0.0.0"))   // network address
        assertFalse(hosts.contains("10.0.0.255")) // broadcast
        assertTrue(hosts.contains("10.0.0.6"))
    }

    @Test
    fun subnetRejectsGarbageInsteadOfThrowing() {
        assertTrue(LanScan.subnetOf("").isEmpty())
        assertTrue(LanScan.subnetOf("localhost").isEmpty())
        assertTrue(LanScan.subnetOf("192.168.1").isEmpty())
        assertTrue(LanScan.subnetOf("999.168.1.1").isEmpty())
        assertTrue(LanScan.subnetOf("1.2.3.999").isEmpty())
    }

    @Test
    fun privateRangesAreRecognised() {
        assertTrue(LanScan.isPrivate("192.168.1.4"))
        assertTrue(LanScan.isPrivate("10.8.0.1"))
        assertTrue(LanScan.isPrivate("172.16.4.4"))
        assertTrue(LanScan.isPrivate("172.31.255.1"))
        assertFalse(LanScan.isPrivate("172.32.0.1"))  // outside 172.16/12
        assertFalse(LanScan.isPrivate("8.8.8.8"))
        assertFalse(LanScan.isPrivate("100.64.0.1"))  // CGNAT, not a LAN the PC can sit on
        assertFalse(LanScan.isPrivate("not.an.ip"))
    }

    @Test
    fun candidateListNeverContainsThePhoneItself() {
        val locals = LanScan.localAddresses()
        val candidates = LanScan.candidates()
        assertEquals(candidates.distinct(), candidates)
        for (self in locals) assertFalse("self $self in scan list", candidates.contains(self))
        // Whatever the machine is, every candidate has to look like an IPv4 address.
        val shape = Regex("^\\d{1,3}(\\.\\d{1,3}){3}$")
        for (c in candidates) assertTrue("bad candidate $c", shape.matches(c))
    }

    // ------------------------------------------------------------------ probing

    @Test
    fun probeFindsAListeningHost() {
        val s = ServerSocket(0, 1, java.net.InetAddress.getByName("127.0.0.1"))
        listener = s
        val hits = LanScan.probe(listOf("127.0.0.1"), s.localPort, timeoutMs = 800)
        assertEquals(listOf("127.0.0.1"), hits)
    }

    @Test
    fun probeReturnsNothingWhenNobodyAnswers() {
        // Bind to get a free port, then release it so the connect is refused immediately.
        val dead = ServerSocket(0).also { it.close() }.localPort
        val hits = LanScan.probe(listOf("127.0.0.1"), dead, timeoutMs = 500)
        assertTrue("expected no hits, got $hits", hits.isEmpty())
    }

    @Test
    fun probeReturnsWithinABoundAgainstAnUnroutableHost() {
        // TEST-NET-1 is reserved for documentation. What the network does with a connect
        // to it (drop, refuse, or a middlebox that answers for everything) is not ours to
        // decide — the guarantee this scan makes is that the caller is never left waiting
        // on a connect() that never ends.
        val started = System.currentTimeMillis()
        LanScan.probe(listOf("192.0.2.1"), 8787, timeoutMs = 400, workers = 4)
        val took = System.currentTimeMillis() - started
        assertTrue("scan took ${took}ms", took < 8_000)
    }

    @Test
    fun probeIgnoresNonsenseArguments() {
        assertTrue(LanScan.probe(emptyList(), 8787).isEmpty())
        assertTrue(LanScan.probe(listOf("127.0.0.1"), 0).isEmpty())
        assertTrue(LanScan.probe(listOf("127.0.0.1"), 70000).isEmpty())
    }
}
