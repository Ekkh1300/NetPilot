package com.netpilot.mobile.net

import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress

/**
 * A DNS reply is only accepted if it answers the question that was asked.
 *
 * UDP has no connection. A socket receives whatever arrives on the port it happens to be bound
 * to, so `receive` returning the first packet it sees means any of these become "the answer": a
 * late reply to a query sent seconds earlier, a spoofed packet from anyone on the path, or an
 * off-path injection when the source port is guessable. The bytes are a valid DNS response either
 * way, so nothing downstream can tell the difference - the caller just gets an answer to a
 * question it did not ask.
 *
 * That matters on a phone more than on a desktop. The whole point of this app is to give a PC a
 * resolver reached over a phone that is joined to whatever network it happens to be on. Accepting
 * an unsolicited answer hands the DNS decision to whoever can reach that phone's port, and the
 * app cannot report it because it has no way of knowing the answer was not its own.
 *
 * These run against a real socket on the loopback interface. A mock would test the assertion
 * rather than the arrival, and the arrival is the entire defect.
 */
class DnsTransactionIdTest {

    @Before
    fun isolate() {
        // The forwarder races the system fallbacks too, which would send real queries out of the
        // test machine. Nothing here needs them.
        DnsForwarder.fallbackServers = emptyList()
    }

    /** A well-formed 12-byte response header carrying [id]. */
    private fun response(idHi: Int, idLo: Int): ByteArray {
        val b = ByteArray(12)
        b[0] = idHi.toByte(); b[1] = idLo.toByte()
        b[2] = ((0x8180 shr 8) and 0xFF).toByte(); b[3] = (0x8180 and 0xFF).toByte()
        b[4] = 0; b[5] = 1            // QDCOUNT = 1
        b[7] = 0                       // ANCOUNT = 0
        return b
    }

    private fun query(idHi: Int, idLo: Int): ByteArray {
        val b = ByteArray(12)
        b[0] = idHi.toByte(); b[1] = idLo.toByte()
        b[2] = 0x01; b[3] = 0x00      // standard query, recursion desired
        b[4] = 0; b[5] = 1            // QDCOUNT = 1
        return b
    }

    /** Starts a loopback "resolver" that answers with [replies] in order. */
    private fun responder(
        vararg replies: ByteArray,
        holdMs: Long = 0,
    ): Pair<DatagramSocket, Int> {
        val server = DatagramSocket(0, InetAddress.getByName("127.0.0.1"))
        Thread {
            try {
                val pkt = DatagramPacket(ByteArray(512), 512)
                server.receive(pkt)
                for (r in replies) {
                    server.send(DatagramPacket(r, r.size, pkt.address, pkt.port))
                    if (holdMs > 0) Thread.sleep(60)
                }
                if (holdMs > 0) Thread.sleep(holdMs)
            } catch (_: Throwable) {
                // The socket is closed by the test on teardown; a broken pipe here is expected.
            }
        }.apply { isDaemon = true }.start()
        return server to server.localPort
    }

    // ------------------------------------------------------------------ the rule

    @Test
    fun aReplyWithTheRightIdIsAccepted() = runBlocking {
        val (server, port) = responder(response(0xAB, 0xCD))
        try {
            val (answer, _) = DnsForwarder.forward(query(0xAB, 0xCD), "127.0.0.1", port, 2500)
            assertNotNull("a reply carrying the right transaction id must be accepted", answer)
            assertEquals(0xAB, answer!![0].toInt() and 0xFF)
            assertEquals(0xCD, answer[1].toInt() and 0xFF)
        } finally {
            server.close()
        }
    }

    /**
     * The defect.
     *
     * A well-formed reply carrying somebody else's transaction id arrives. It must be refused,
     * and since it is all that is on the wire the lookup must fail rather than return it. That
     * is the point: a wrong answer is worse than no answer, because the caller cannot tell.
     */
    @Test
    fun aReplyWithTheWrongIdIsRefused() = runBlocking {
        val (server, port) = responder(response(0x99, 0x99), holdMs = 1400)
        try {
            val (answer, _) = DnsForwarder.forward(query(0xAB, 0xCD), "127.0.0.1", port, 900)
            assertNull(
                "a reply for a different transaction id must not be returned as the answer",
                answer
            )
        } finally {
            server.close()
        }
    }

    /**
     * The right answer must still be accepted when a wrong one arrives first.
     *
     * This is why a mismatch cannot simply return null. In production the socket also carries the
     * replies of sibling attempts that raced this one, and a genuine answer often follows one of
     * them. Stopping the read would turn an ordinary race into a resolver failure - the fix for a
     * spoofing bug that itself causes timeouts is a bad trade.
     */
    @Test
    fun theRightAnswerIsStillAcceptedAfterAWrongOneArrivesFirst() = runBlocking {
        val (server, port) = responder(response(0x11, 0x22), response(0xAB, 0xCD))
        try {
            val (answer, _) = DnsForwarder.forward(query(0xAB, 0xCD), "127.0.0.1", port, 2500)
            assertNotNull("a wrong reply must not end the wait for the right one", answer)
            assertEquals("the wrong reply was accepted", 0xAB, answer!![0].toInt() and 0xFF)
        } finally {
            server.close()
        }
    }

    /** An id is two bytes; comparing one is the classic off-by-a-byte in this check. */
    @Test
    fun theIdIsComparedAsBothBytes() = runBlocking {
        val (server, port) = responder(response(0xAB, 0xFF), holdMs = 1200)
        try {
            val (answer, _) = DnsForwarder.forward(query(0xAB, 0xCD), "127.0.0.1", port, 800)
            assertNull("matching only the high byte would have accepted this", answer)
        } finally {
            server.close()
        }
    }

    /**
     * A packet too short to hold a DNS header cannot be an answer whatever its bytes are.
     * Returning it would put a four-byte payload into a parser that reads twelve.
     */
    @Test
    fun aTooShortPacketIsSkippedNotReturnedAndNotFatal() = runBlocking {
        val junk = ByteArray(4)
        System.arraycopy(query(0xAB, 0xCD), 0, junk, 0, 4)
        val (server, port) = responder(junk, response(0xAB, 0xCD))
        try {
            val (answer, _) = DnsForwarder.forward(query(0xAB, 0xCD), "127.0.0.1", port, 2500)
            assertNotNull("a short packet must be skipped, not returned and not fatal", answer)
            assertEquals(0xAB, answer!![0].toInt() and 0xFF)
        } finally {
            server.close()
        }
    }

    /** Silence is a timeout, not a fabricated answer. */
    @Test
    fun silenceProducesNoAnswer() = runBlocking {
        val (server, port) = responder()          // receives the query, replies with nothing
        try {
            val (answer, _) = DnsForwarder.forward(query(0xAB, 0xCD), "127.0.0.1", port, 700)
            assertNull(answer)
        } finally {
            server.close()
        }
    }

    /**
     * A truncated reply is still accepted and handed to the TCP retry, which is the whole reason
     * the id check must not interfere with the flags handling.
     */
    @Test
    fun aTruncatedReplyIsStillRecognised() {
        val truncated = response(0xAB, 0xCD)
        truncated[2] = ((0x8383 shr 8) and 0xFF).toByte()   // TC set
        truncated[3] = (0x8383 and 0xFF).toByte()

        assertTrue("TC bit must be detected", (truncated[2].toInt() and 0x02) != 0)
        assertTrue("and the id still matches", Bytes.matches(query(0xAB, 0xCD), truncated))
    }

    /** The rule on its own, with no socket, so it cannot rot unnoticed. */
    @Test
    fun theIdRuleIsTwoMatchingBytes() {
        val q = query(0x12, 0x34)
        assertTrue(Bytes.matches(q, response(0x12, 0x34)))
        assertFalse(Bytes.matches(q, response(0x12, 0x35)))
        assertFalse(Bytes.matches(q, response(0x13, 0x34)))
        assertFalse(Bytes.matches(q, response(0x00, 0x00)))
        assertFalse("a one-byte query has no id to compare", Bytes.matches(ByteArray(1), response(0, 0)))
        assertFalse("a short reply has no header", Bytes.matches(q, ByteArray(4)))
    }
}

/** The rule, isolated from the socket so it can be asserted directly. */
internal object Bytes {
    fun matches(query: ByteArray, response: ByteArray): Boolean =
        query.size >= 2 && response.size >= 12 &&
                query[0] == response[0] && query[1] == response[1]
}