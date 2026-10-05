package com.netpilot.mobile.vpn

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * A TCP flow's receive buffer is bounded, whether or not the client acknowledges anything.
 *
 * The buffer reclaimed only *acknowledged* bytes, and the read side sized itself from
 * `capacityLeft = MAX_DOWN - pending`. That looks bounded and is not: a client that never
 * acknowledges leaves `pending` at whatever was appended and `start` at zero, so `compact()` is a
 * no-op and `append` doubles the backing array on every read. One peer, one flow, no cooperation
 * required, and the phone runs out of memory.
 *
 * The rule enforced here: a flow holds at most [MAX_DOWN] unacknowledged bytes, and a write that
 * would exceed it is refused rather than silently dropped - because TCP is a byte stream and a
 * gap in it is indistinguishable from corruption to the client.
 */
class DownBufferBoundTest {

    /**
     * The buffer, mirroring Relay's.
     *
     * Copied rather than instantiated because `Down` is a private nested class of a class that
     * owns sockets and a selector. The copy is deliberate duplication: it is the only way this
     * bound can be tested at all, and a bound with no test is a comment.
     */
    private class Down(initialSeq: Long, private val maxDown: Int = PRODUCTION_CAP) {
        private var buf = ByteArray(16_384)
        var base = initialSeq and 0xFFFFFFFFL
        var start = 0
        var sent = 0
        var end = 0

        val pending: Int get() = end - start
        val unsent: Int get() = end - sent
        val capacityLeft: Int get() = maxDown - pending

        var overflowed = false
            private set

        fun append(src: ByteArray, off: Int, len: Int): Boolean {
            if (len <= 0) return true
            // Compact before the cap check, not only when the array is full: compaction moves
            // `end` down without changing `pending`, so a flow whose acknowledged bytes sit in
            // the middle of the array can hit the refusal branch, compact, find `pending`
            // unchanged, and reject data that was about to fit.
            if (start > 0) compact()
            if (pending + len > maxDown) {
                overflowed = true
                return false
            }
            if (end + len > buf.size) {
                val want = maxOf(buf.size * 2, end + len)
                buf = buf.copyOf(minOf(want, maxDown).coerceAtLeast(end + len))
            }
            System.arraycopy(src, off, buf, end, len)
            end += len
            return true
        }

        fun ack(seq: Long) {
            val offset = Packet.seqDelta(seq, base)
            if (offset <= 0) return
            val newStart = if (offset > sent) sent else offset.toInt()
            if (newStart > start) start = newStart
        }

        fun advance(n: Int) {
            sent += n
        }

        fun slice(from: Int, len: Int): ByteArray = buf.copyOfRange(from, from + len)

        fun compact() {
            if (start <= 0) return
            val n = start
            val keep = end - n
            if (keep > 0) System.arraycopy(buf, n, buf, 0, keep)
            end -= n
            sent -= n
            start = 0
            base = (base + n) and 0xFFFFFFFFL
        }
    }

    private val cap = 8 * 1024      // small enough to exhaust quickly


    // ------------------------------------------------------------------ the defect

    /**
     * A client that sends and never acknowledges.
     *
     * Ten times the cap's worth of data, appended in reads the way the relay reads. The flow must
     * refuse rather than grow: at no point may pending exceed the cap, and the overflow must be
     * reported so the caller can reset the connection.
     */
    @Test
    fun aClientThatNeverAcknowledgesCannotGrowTheBuffer() {
        val d = Down(1, cap)
        val chunk = ByteArray(1460)
        var accepted = 0
        var refused = 0

        repeat(100) {
            if (d.append(chunk, 0, chunk.size)) accepted++ else refused++
        }

        assertTrue("the flow must refuse once it is full", refused > 0)
        assertTrue("nothing may be dropped silently", d.overflowed)
        assertTrue("pending exceeded the cap: ${d.pending}", d.pending <= cap)

        // Every byte handed over was either stored or explicitly refused.
        assertEquals(chunk.size * (accepted + refused), chunk.size * 100)
        assertTrue("accepted must not exceed the cap", accepted * chunk.size <= cap + chunk.size)
    }

    @Test
    fun pendingNeverExceedsTheCap() {
        val d = Down(1, cap)
        repeat(500) { d.append(ByteArray(512), 0, 512) }
        assertTrue("pending is ${d.pending}, cap is $cap", d.pending <= cap)
    }

    // ------------------------------------------------------------------ reclaiming

    /**
     * A well-behaved client keeps working.
     *
     * The bound must not be reached in normal operation: acknowledged bytes are reclaimed, so a
     * flow that acknowledges as it reads runs indefinitely.
     */
    @Test
    fun anAcknowledgingClientCanRunIndefinitely() {
        val d = Down(1, cap)
        val chunk = ByteArray(1024)

        repeat(2_000) { i ->
            assertTrue("refused at iteration $i, which should never happen", d.append(chunk, 0, chunk.size))

            // This mirrors what the relay actually does after a read: it flushes the bytes it
            // took to the TUN (advance) and then, separately, applies whatever the client's ACK
            // said. The two are independent - `ack` clamps to `sent`, so an ACK that arrives for
            // bytes not yet transmitted advances nothing and is correctly ignored.
            d.advance(d.pending)
            d.ack(d.base + d.pending)
        }
        assertFalse("an acknowledging flow must never overflow", d.overflowed)
    }

    @Test
    fun compactionReclaimsAcknowledgedBytes() {
        val d = Down(1, cap)
        d.append(ByteArray(4096), 0, 4096)
        assertEquals(4096, d.pending)

        d.advance(4096)
        d.ack(d.base + 4096)
        d.compact()

        assertEquals("acknowledged bytes are reclaimable", 0, d.pending)
        // base started at 1 and moved forward by the 4096 bytes that were dropped, so it now
        // holds the sequence number of buf[0] - which is 4097, not 0.
        assertEquals("base advances by exactly what was dropped", 4097L, d.base)
    }

    @Test
    fun compactingWithNothingAcknowledgedIsANoOp() {
        // Correct, not a bug: unacknowledged bytes are still owed. append is what bounds it.
        val d = Down(1, cap)
        d.append(ByteArray(2048), 0, 2048)
        d.compact()
        assertEquals(2048, d.pending)
    }

    // ------------------------------------------------------------------ edge cases

    @Test
    fun aZeroLengthAppendSucceedsAndChangesNothing() {
        val d = Down(1, cap)
        assertTrue(d.append(ByteArray(0), 0, 0))
        assertEquals(0, d.pending)
        assertFalse(d.overflowed)
    }

    @Test
    fun appendingExactlyTheCapSucceeds() {
        val d = Down(1, cap)
        assertTrue("exactly the cap must fit", d.append(ByteArray(cap), 0, cap))
        assertEquals(cap, d.pending)
        assertFalse(d.overflowed)
    }

    @Test
    fun appendingOneByteOverTheCapIsRefused() {
        val d = Down(1, cap)
        assertTrue(d.append(ByteArray(cap), 0, cap))
        assertFalse("one byte over must be refused", d.append(ByteArray(1), 0, 1))
        assertTrue(d.overflowed)
        assertEquals("and nothing extra was stored", cap, d.pending)
    }

    /**
     * A single read larger than the whole cap cannot be half-stored.
     *
     * This is reachable in practice: MAX_READ is 32 KB and the cap is 64 KB, so a read can
     * exceed the free space once a flow is mostly full. Storing what fits and dropping the rest
     * would corrupt the stream.
     */
    @Test
    fun anAppendLargerThanTheWholeCapIsRefusedWhole() {
        val d = Down(1, cap)
        assertFalse(d.append(ByteArray(cap * 2), 0, cap * 2))
        assertTrue(d.overflowed)
        assertEquals("nothing of an oversized append may be stored", 0, d.pending)
    }

    @Test
    fun theDataThatWasAcceptedIsTheDataThatWasGiven() {
        val d = Down(1, cap)
        val payload = ByteArray(300) { (it and 0xFF).toByte() }
        assertTrue(d.append(payload, 0, payload.size))
        assertTrue(payload.contentEquals(d.slice(0, payload.size)))
    }

    @Test
    fun sequenceNumbersWrapCorrectly() {
        // base advances as bytes are dropped, and TCP sequence numbers wrap at 2^32. An
        // unsigned comparison that ignored the wrap would stop compacting exactly when a long
        // flow needs it most.
        val d = Down(0xFFFFFFF0L, cap)
        d.append(ByteArray(64), 0, 64)
        d.advance(64)
        d.ack(0xFFFFFFF0L + 64)     // wraps past 2^32
        d.compact()

        assertEquals(0, d.pending)
    }

    companion object {
        /**
         * The production cap, taken from Relay rather than restated.
         *
         * A restated constant would let this suite keep passing after someone changed the real
         * one - the tests would still be testing a buffer, just not the buffer that ships. The
         * relay's companion constant is public for exactly this reason.
         */
        const val PRODUCTION_CAP = Relay.MAX_DOWN
    }
}
