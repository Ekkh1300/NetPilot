package com.netpilot.mobile.vpn

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/**
 * The length-prefixed framing for DNS over TCP.
 *
 * This is a copy of the reader that lives inside `NetPilotVpnService`, which cannot be
 * instantiated in a JVM test - it extends `VpnService`. Duplicating it is the lesser evil: the
 * alternative is that the framing has no test at all, and it is the piece most likely to be
 * wrong quietly.
 *
 * It is here because of a real bug. The tunnel read **one** message per inbound segment and
 * discarded the rest, sitting on top of a dead `ackFlag || psh` expression. DNS over TCP is
 * explicitly allowed to pipeline - a client may put two queries in one segment, which is what the
 * two-byte length prefix exists to disambiguate - so the second query sat unanswered in the
 * buffer until some later segment happened to arrive, and if none did, it was never answered at
 * all. The client had already been ACKed, so it waited on a resolver that was working fine.
 *
 * Nothing failed. One query just never came back, which is close to the worst shape a bug can
 * take on a phone.
 */
class DnsFramingTest {

    private class Session {
        val input = java.io.ByteArrayOutputStream()

        fun append(b: ByteArray) = input.write(b)

        /** One message, or null when the buffer does not hold a complete one yet. */
        fun takeMessage(): ByteArray? {
            val bytes = input.toByteArray()
            if (bytes.size < 2) return null
            val len = ((bytes[0].toInt() and 0xFF) shl 8) or (bytes[1].toInt() and 0xFF)
            if (bytes.size < len + 2) return null
            val msg = bytes.copyOfRange(2, len + 2)
            input.reset()
            if (bytes.size > len + 2) {
                input.write(bytes, len + 2, bytes.size - len - 2)
            }
            return msg
        }

        /** Every complete message, draining the buffer. The fix. */
        fun drainMessages(): List<ByteArray> {
            val out = ArrayList<ByteArray>()
            while (true) out.add(takeMessage() ?: return out)
        }
    }

    private fun frame(payload: ByteArray): ByteArray {
        val out = ByteArray(2 + payload.size)
        out[0] = ((payload.size shr 8) and 0xFF).toByte()
        out[1] = (payload.size and 0xFF).toByte()
        payload.copyInto(out, 2)
        return out
    }

    // ------------------------------------------------------------------ basics

    @Test
    fun oneMessageRoundTrips() {
        val s = Session()
        val msg = byteArrayOf(0x12, 0x34, 0xAB.toByte())
        s.append(frame(msg))

        assertArrayEquals(msg, s.drainMessages().single())
    }

    @Test
    fun anIncompleteMessageIsHeldRatherThanReturned() {
        val s = Session()
        val msg = byteArrayOf(1, 2, 3, 4, 5)
        val framed = frame(msg)

        // A length prefix promising more than arrived.
        s.append(framed.copyOfRange(0, 4))
        assertNull("a partial message must not be handed on as if it were complete", s.takeMessage())

        // And now the rest.
        s.append(framed.copyOfRange(4, framed.size))
        assertArrayEquals(msg, s.takeMessage())
    }

    @Test
    fun aZeroLengthMessageIsStillAMessage() {
        // The prefix is a length, so zero means an empty DNS message - which is a real thing a
        // peer can send. Dropping it would desynchronise the stream: every following message
        // would be read one length out of step.
        val s = Session()
        s.append(byteArrayOf(0, 0))
        assertEquals(0, s.drainMessages().single().size)
    }

    // ------------------------------------------------------------------ the bug

    /**
     * The reason this class exists.
     *
     * Two queries in one segment, which is legal and which the length prefix exists to support.
     * Reading one and returning - which is what the tunnel did - answers the first and leaves the
     * second in the buffer with nothing scheduled to look at it again.
     */
    @Test
    fun twoQueriesInOneSegmentAreBothAnswered() {
        val s = Session()
        val first = byteArrayOf(0xAA.toByte(), 0xBB.toByte())
        val second = byteArrayOf(0xCC.toByte(), 0xDD.toByte(), 0xEE.toByte())

        val segment = frame(first) + frame(second)
        s.append(segment)

        val messages = s.drainMessages()

        assertEquals("both queries must come out of one segment", 2, messages.size)
        assertArrayEquals(first, messages[0])
        assertArrayEquals(second, messages[1])
    }

    @Test
    fun threeQueriesInOneSegmentAreAllAnswered() {
        val s = Session()
        val first = byteArrayOf(1, 2, 3)
        val second = byteArrayOf(4, 5)
        val third = byteArrayOf(6, 7, 8, 9)
        s.append(frame(first) + frame(second) + frame(third))

        val messages = s.drainMessages()
        assertEquals(3, messages.size)
        assertArrayEquals(first, messages[0])
        assertArrayEquals(second, messages[1])
        assertArrayEquals(third, messages[2])
    }

    /**
     * The stream must not drift.
     *
     * If a reader took a length from the wrong offset, the first message would come out right and
     * every later one wrong - so a single message passing proves nothing. This asserts the
     * framing stays aligned across a run of differently sized messages.
     */
    @Test
    fun theStreamStaysAlignedAcrossDifferentlySizedMessages() {
        val payloads = listOf(
            ByteArray(1) { 0x01 },
            ByteArray(255) { 0x02 },
            ByteArray(256) { 0x03 },     // crosses the one-byte boundary
            ByteArray(4096) { 0x04 },
            ByteArray(1) { 0x05 },
        )

        val s = Session()
        val expected = ArrayList<ByteArray>()
        for (p in payloads) {
            s.append(frame(p))
            expected.add(p)
        }

        val got = s.drainMessages()
        assertEquals(expected.size, got.size)
        for (i in expected.indices) {
            assertArrayEquals("message $i was read at the wrong offset", expected[i], got[i])
        }
    }

    @Test
    fun aTrailingPartialMessageSurvivesForTheNextSegment() {
        val s = Session()
        val complete = byteArrayOf(0x11, 0x22)
        val partial = byteArrayOf(0x33, 0x44, 0x55)

        val framed = frame(complete)
        s.append(framed)
        s.append(frame(partial).copyOfRange(0, 3))       // prefix plus one byte

        assertEquals("only the complete one now", 1, s.drainMessages().size)

        // The rest of the partial arrives.
        s.append(frame(partial).copyOfRange(3, frame(partial).size))
        assertArrayEquals(partial, s.drainMessages().single())
    }

    @Test
    fun anEmptyBufferYieldsNothing() {
        val s = Session()
        assertEquals(0, s.drainMessages().size)
    }

    @Test
    fun aSingleLengthByteIsNotEnoughToReadAnything() {
        // One byte cannot hold a length, so nothing is readable. Reading a length from a
        // half-arrived prefix is how a stream starts reading garbage lengths.
        val s = Session()
        s.append(byteArrayOf(0x00))
        assertEquals(0, s.drainMessages().size)
    }

    @Test
    fun theLengthPrefixIsBigEndian() {
        // 0x0102 is 258 bytes. Read little-endian it would be 0x0201 = 513, and the reader would
        // wait for bytes that never come - the message simply never arrives.
        val payload = ByteArray(258) { 0x7F }
        val s = Session()
        s.append(frame(payload))

        assertArrayEquals(payload, s.drainMessages().single())
    }
}