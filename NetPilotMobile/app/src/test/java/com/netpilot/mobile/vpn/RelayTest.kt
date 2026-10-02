package com.netpilot.mobile.vpn

import com.netpilot.mobile.data.LimitRule
import com.netpilot.mobile.data.RuleMode
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Before
import org.junit.Test
import java.util.concurrent.ConcurrentLinkedQueue

/**
 * The relay itself, driven from real packets against a real loopback server.
 *
 * This is the part that cannot be proven by reading it: a SYN has to come back as a
 * SYN|ACK with a correct checksum, the payload has to survive the round trip through the
 * socket pair, a blocked application has to be refused instead of hanging, and DNS has to
 * stay with the tunnel's own resolver. All of it runs on the JVM — no device attached.
 */
class RelayTest {

    private val out = ConcurrentLinkedQueue<ByteArray>()
    private val dns = ConcurrentLinkedQueue<ByteArray>()

    private var relay: Relay? = null
    private var tcpEcho: TcpEcho? = null
    private var udpEcho: UdpEcho? = null

    private val clientIp = ip("10.111.222.1")
    private val serverIp = ip("127.0.0.1")

    @Before
    fun setUp() {
        FirewallStats.reset()
        out.clear()
        dns.clear()
    }

    @After
    fun tearDown() {
        relay?.close()
        relay = null
        tcpEcho?.close()
        tcpEcho = null
        udpEcho?.close()
        udpEcho = null
    }

    // ------------------------------------------------------------------ harness

    private fun startRelay(
        rules: List<LimitRule> = emptyList(),
        uid: Int = 7777,
        pkg: String? = "com.example.app"
    ) {
        val firewall = Firewall(
            ruleSource = { rules.associateBy { it.pkg } },
            ownerUid = { _, _, _, _, _ -> uid },
            pkgOf = { pkg }
        )
        relay = Relay(
            emit = { out.add(it) },
            protectSocket = { true },
            protectDatagram = { true },
            firewall = firewall,
            onDnsUdp = { query, _, _, _, _ -> dns.add(query) },
            onDnsTcp = { _, _, _, _ -> }
        ).also { it.start() }
    }

    private fun clientTcp(
        dstPort: Int,
        srcPort: Int = 40000,
        seq: Long,
        ack: Long = 0,
        flags: Int,
        options: ByteArray = ByteArray(0),
        payload: ByteArray = ByteArray(0)
    ) = Packet.buildTcp(
        srcIp = clientIp, srcPort = srcPort,
        dstIp = serverIp, dstPort = dstPort,
        seq = seq, ack = ack, flags = flags, window = 65535,
        options = options, payload = payload, ipId = 1
    )

    private fun sendTcp(
        dstPort: Int,
        srcPort: Int = 40000,
        seq: Long,
        ack: Long = 0,
        flags: Int,
        options: ByteArray = ByteArray(0),
        payload: ByteArray = ByteArray(0)
    ) {
        relay!!.onPacket(
            clientTcp(dstPort, srcPort, seq, ack, flags, options, payload),
            20, Packet.TCP, clientIp, serverIp
        )
    }

    /** Pulls the first emitted packet whose TCP header satisfies [pred]. */
    private fun awaitTcp(
        what: String,
        timeoutMs: Long = 5_000,
        pred: (packet: ByteArray, hdr: Packet.TcpHdr) -> Boolean
    ): Pair<ByteArray, Packet.TcpHdr> {
        val deadline = System.currentTimeMillis() + timeoutMs
        while (System.currentTimeMillis() < deadline) {
            val it = out.iterator()
            while (it.hasNext()) {
                val p = it.next()
                val h = Packet.parseTcp(p, 20)
                if (h != null && pred(p, h)) {
                    it.remove()
                    return p to h
                }
            }
            Thread.sleep(10)
        }
        fail("timed out waiting for $what")
        throw AssertionError("unreachable")
    }

    /** Payload of an emitted TCP segment: everything after the IP + TCP headers. */
    private fun tcpPayload(p: ByteArray, h: Packet.TcpHdr): ByteArray =
        if (p.size <= 20 + h.headerLen) ByteArray(0)
        else p.copyOfRange(20 + h.headerLen, p.size)

    private fun awaitDns(timeoutMs: Long = 5_000): ByteArray {
        val deadline = System.currentTimeMillis() + timeoutMs
        while (System.currentTimeMillis() < deadline) {
            dns.poll()?.let { return it }
            Thread.sleep(10)
        }
        fail("timed out waiting for the resolver callback")
        throw AssertionError("unreachable")
    }

    private fun awaitUdp(
        what: String,
        timeoutMs: Long = 5_000
    ): Pair<ByteArray, Packet.UdpHdr> {
        val deadline = System.currentTimeMillis() + timeoutMs
        while (System.currentTimeMillis() < deadline) {
            val it = out.iterator()
            while (it.hasNext()) {
                val p = it.next()
                val h = Packet.parseUdp(p, 20)
                if (h != null) {
                    it.remove()
                    return p to h
                }
            }
            Thread.sleep(10)
        }
        fail("timed out waiting for $what")
        throw AssertionError("unreachable")
    }

    private val mssOption = byteArrayOf(2, 4, 5, 0xB4.toByte())   // MSS 1460

    // ------------------------------------------------------------------ TCP

    @Test
    fun tcpStreamIsRelayedEndToEnd() {
        val echo = TcpEcho().also { tcpEcho = it }
        startRelay()

        val clientIsn = 0x1000_2000L
        val clientPort = 40000

        // 1. the application opens a connection
        sendTcp(echo.port, clientPort, seq = clientIsn, flags = Packet.SYN, options = mssOption)

        // 2. the relay terminates the handshake itself, upstream socket still dialling
        val (synAckPkt, synAck) = awaitTcp("SYN|ACK") { _, h ->
            h.dstPort == clientPort && h.flags and Packet.SYN != 0 && h.flags and Packet.ACK != 0
        }
        assertTrue("handshake is deliverable", checksumsValid(synAckPkt))
        assertEquals("acknowledges the client ISN", (clientIsn + 1) and 0xFFFFFFFFL, synAck.ack)
        assertEquals("passes the announced MSS through", 1460, synAck.mss)
        assertEquals("answers the source port", clientPort, synAck.dstPort)
        assertEquals("from the real destination", echo.port, synAck.srcPort)

        // 3. the application ACKs and pushes data straight away — even if the upstream
        //    connect has not finished, the bytes must be queued, not dropped.
        val ackSeq = (clientIsn + 1) and 0xFFFFFFFFL
        sendTcp(echo.port, clientPort, seq = ackSeq, ack = (synAck.seq + 1) and 0xFFFFFFFFL,
            flags = Packet.ACK)
        sendTcp(echo.port, clientPort, seq = ackSeq, ack = (synAck.seq + 1) and 0xFFFFFFFFL,
            flags = Packet.ACK or Packet.PSH, payload = "hello".toByteArray())

        // 4. the echo server's answer comes back through the socket pair
        val (echoPkt, echoHdr) = awaitTcp("the echoed payload") { p, h ->
            tcpPayload(p, h).isNotEmpty()
        }
        assertEquals(
            "bytes survived both directions",
            "hello",
            String(tcpPayload(echoPkt, echoHdr))
        )
        assertEquals(
            "acks everything the client sent",
            (clientIsn + 1 + 5) and 0xFFFFFFFFL,
            echoHdr.ack
        )
        assertTrue("every segment is checksum-correct", checksumsValid(echoPkt))

        assertTrue("the flow is counted for the UI", FirewallStats.relayedFlows >= 1)
        assertEquals("nothing was blocked", 0L, FirewallStats.blockedPackets)
        assertEquals("no attribution misses", 0L, FirewallStats.unidentifiedFlows)
    }

    @Test
    fun blockedConnectionIsRefusedWithRst() {
        val echo = TcpEcho().also { tcpEcho = it }
        startRelay(rules = listOf(LimitRule("com.example.app", "Example", RuleMode.BLOCK)))

        sendTcp(echo.port, seq = 0x1234L, flags = Packet.SYN, options = mssOption)

        val (pkt, h) = awaitTcp("RST") { _, hdr ->
            hdr.dstPort == 40000 && hdr.flags and Packet.RST != 0
        }
        assertTrue("refusal is deliverable", checksumsValid(pkt))
        assertEquals("RST with ACK of the SYN", Packet.RST or Packet.ACK, h.flags)
        assertEquals("a bare SYN costs one sequence number", 0x1235L, h.ack)
        assertEquals("from the refused destination", echo.port, h.srcPort)
        assertTrue("the block is reported to the UI", FirewallStats.blockedPackets > 0)
        assertEquals("the connection never opened", 0, FirewallStats.relayedFlows)
    }

    @Test
    fun allowedPackageIsNotRefused() {
        val echo = TcpEcho().also { tcpEcho = it }
        startRelay(rules = listOf(LimitRule("com.example.app", "Example", RuleMode.BLOCK)),
            pkg = "com.other.app")

        sendTcp(echo.port, seq = 0x1234L, flags = Packet.SYN, options = mssOption)
        val (_, h) = awaitTcp("SYN|ACK") { _, hdr ->
            hdr.dstPort == 40000 && hdr.flags and Packet.SYN != 0
        }
        assertTrue(h.flags and Packet.ACK != 0)
        assertEquals(0L, FirewallStats.blockedPackets)
    }

    @Test
    fun unidentifiedFlowsFailClosedThenTheBreakerGivesUp() {
        val echo = TcpEcho().also { tcpEcho = it }
        startRelay(
            rules = listOf(LimitRule("com.example.app", "Example", RuleMode.BLOCK)),
            uid = Firewall.INVALID_UID
        )

        var refused = 0
        var opened = 0
        // Every SYN uses a fresh source port, so the relay asks the platform again instead
        // of reading a cached answer.
        for (i in 1..(Firewall.BREAKER_AFTER + 4)) {
            sendTcp(
                echo.port, srcPort = 40000 + i,
                seq = 0x1000L + i, flags = Packet.SYN, options = mssOption
            )
            val (_, h) = awaitTcp("decision for flow $i") { _, hdr ->
                hdr.dstPort == 40000 + i &&
                        (hdr.flags and Packet.RST != 0 || hdr.flags and Packet.SYN != 0)
            }
            if (h.flags and Packet.RST != 0) refused++ else opened++
        }

        // Misses 1..11 refuse; miss 12 trips the breaker and the rest go through.
        assertEquals(
            "refuses everything while the breaker is armed",
            Firewall.BREAKER_AFTER - 1,
            refused
        )
        assertTrue("eventually gives up instead of black-holing the device", opened > 0)
        assertTrue("the UI is told attribution is broken", FirewallStats.attributionBroken)
        assertTrue("missed flows are counted", FirewallStats.unidentifiedFlows > 0L)
    }

    // ------------------------------------------------------------------ UDP / DNS

    @Test
    fun datagramsAreNatedToTheServerAndBack() {
        val echo = UdpEcho().also { udpEcho = it }
        startRelay()

        val query = "ping".toByteArray()
        relay!!.onPacket(
            Packet.buildUdp(query, clientIp, 52000, serverIp, echo.port, ipId = 7),
            20, Packet.UDP, clientIp, serverIp
        )

        val (pkt, h) = awaitUdp("the echoed datagram")
        assertEquals("ping", String(pkt.copyOfRange(h.payloadOff, h.payloadEnd)))
        assertEquals("answer goes back to the app", 52000, h.dstPort)
        assertEquals("and comes from the server", echo.port, h.srcPort)
        assertTrue(checksumsValid(pkt))
    }

    @Test
    fun dnsQueriesStayWithTheTunnelResolver() {
        startRelay()
        val query = dnsQuery()

        relay!!.onPacket(
            Packet.buildUdp(query, clientIp, 51000, serverIp, 53, ipId = 8),
            20, Packet.UDP, clientIp, serverIp
        )

        val handled = awaitDns()
        assertEquals(
            "the resolver receives the untouched question",
            query.toList(),
            handled.toList()
        )
        // Nothing may be relayed to the network on port 53.
        assertEquals("DNS is not forwarded by the relay", 0, out.size)
    }

    @Test
    fun blockedUdpFlowSendsNothing() {
        startRelay(rules = listOf(LimitRule("com.example.app", "Example", RuleMode.BLOCK)))
        relay!!.onPacket(
            Packet.buildUdp("secret".toByteArray(), clientIp, 52000, serverIp, 9999, ipId = 9),
            20, Packet.UDP, clientIp, serverIp
        )
        Thread.sleep(200)
        assertEquals(0, out.size)
        assertTrue(FirewallStats.blockedPackets > 0)
    }

    @Test
    fun nonIpTrafficIsCountedRatherThanFaked() {
        startRelay()
        val icmp = ByteArray(28)   // pretend ICMP: the relay has no raw socket for it
        relay!!.onPacket(icmp, 20, Packet.ICMP, clientIp, serverIp)
        assertEquals("honesty over pretending it was forwarded", 1L, FirewallStats.icmpDropped)
        assertEquals(0, out.size)
    }

    // ------------------------------------------------------------------ helpers

    /** Smallest question the resolver will accept: header + `example.com` + A/IN. */
    private fun dnsQuery(): ByteArray {
        val q = ByteArray(64)
        q[1] = 0x34.toByte()      // transaction id
        q[2] = 0x01.toByte()      // standard query, recursion desired
        q[5] = 0x01.toByte()      // qdcount = 1
        var o = 12
        for (label in listOf("example", "com")) {
            q[o++] = label.length.toByte()
            for (c in label) q[o++] = c.code.toByte()
        }
        q[o++] = 0
        q[o++] = 0; q[o++] = 1    // type A
        q[o++] = 0; q[o++] = 1    // class IN
        return q.copyOf(o)
    }
}
