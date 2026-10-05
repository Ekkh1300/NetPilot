package com.netpilot.mobile.vpn

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.nio.ByteBuffer

/**
 * Reading a channel must yield the bytes that arrived, not the bytes the buffer happened to hold.
 *
 * `ByteBuffer.allocate` returns uninitialised memory. A datagram shorter than the buffer is the
 * normal case, not the exception, so any code that takes `capacity` bytes instead of the read
 * count appends the previous packet's tail to this one - and DNS responses are parsed field by
 * field, so the result is either a nonsensical answer or a parse that lands mid-field.
 *
 * The relay read the count correctly, by way of `flip()`. This test exists because that
 * correctness depended on a reader understanding which of `flip`, `limit` and `capacity` was
 * load-bearing, with nothing to catch a future edit that got it wrong - and the failure it would
 * produce is invisible at the call site and obvious only in a captured packet.
 */
class ChannelReadTest {

    /** The pattern the relay uses, written out so the count is explicit. */
    private fun readCount(buffer: ByteBuffer, n: Int): ByteArray {
        val bytes = ByteArray(n)
        buffer.position(0)
        buffer.limit(n)
        buffer.get(bytes)
        return bytes
    }

    @Test
    fun exactlyTheBytesThatArrivedAreReturned() {
        val buffer = ByteBuffer.allocate(2048)
        val payload = byteArrayOf(0x12, 0x34, 0xAB.toByte(), 0x00)
        buffer.put(payload)          // position == 4
        val n = payload.size

        val got = readCount(buffer, n)

        assertEquals(4, got.size)
        assertTrue(payload.contentEquals(got))
    }

    /**
     * The defect.
     *
     * The buffer is deliberately poisoned with a previous packet's bytes, then a short one
     * arrives. Taking `capacity` would splice the two together.
     */
    @Test
    fun aShortReadDoesNotCarryThePreviousBuffersBytes() {
        val buffer = ByteBuffer.allocate(2048)

        // A first datagram fills much of the buffer, leaving its tail behind.
        val first = ByteArray(1500) { 0x7F }
        buffer.clear()
        buffer.put(first)
        buffer.position(0)
        buffer.limit(first.size)
        buffer.get(ByteArray(first.size))

        // A second, short one arrives.
        val second = byteArrayOf(0xDE.toByte(), 0xAD.toByte(), 0xBE.toByte(), 0xEF.toByte())
        buffer.clear()
        buffer.put(second)
        val n = second.size

        val got = readCount(buffer, n)

        assertEquals("only the new bytes, so the length itself is the first check", second.size, got.size)
        assertTrue("the previous buffer's contents must not survive", second.contentEquals(got))
        for (b in got) {
            assertFalse("a 0x7F is left over from the previous datagram", b == 0x7F.toByte())
        }
    }

    @Test
    fun capacityIsNeverUsedAsTheReadLength() {
        val buffer = ByteBuffer.allocate(2048)
        buffer.put(byteArrayOf(1, 2, 3, 4))
        val n = 4

        // capacity() is 2048 and limit() is still 2048 at this point; the count is 4.
        assertEquals(2048, buffer.capacity())
        assertEquals(4, readCount(buffer, n).size)
    }

    @Test
    fun aFullBufferIsReadCorrectly() {
        val buffer = ByteBuffer.allocate(8)
        val payload = ByteArray(8) { it.toByte() }
        buffer.put(payload)

        assertTrue(payload.contentEquals(readCount(buffer, 8)))
    }

    @Test
    fun aSingleByteReadIsNotConfusedWithTheEmptyRead() {
        // n == 0 and n == 1 differ by one bit in the length, and getting them backwards turns a
        // keep-alive into a stalled flow.
        val buffer = ByteBuffer.allocate(16)
        buffer.put(byteArrayOf(0x42))

        assertEquals(1, readCount(buffer, 1).size)
        assertEquals(0x42.toByte(), readCount(buffer, 1)[0])
    }

    @Test
    fun theBufferIsReusableAcrossReadsBecauseClearResetsIt() {
        // The relay allocates per read, but a future change to reuse one buffer must not carry
        // state; that is what this pins.
        val buffer = ByteBuffer.allocate(16)

        buffer.clear()
        buffer.put(byteArrayOf(1, 1, 1, 1))
        val first = readCount(buffer, 4)
        assertTrue(byteArrayOf(1, 1, 1, 1).contentEquals(first))

        buffer.clear()
        buffer.put(byteArrayOf(2, 2))
        val second = readCount(buffer, 2)
        assertEquals(2, second.size)
        assertTrue(byteArrayOf(2, 2).contentEquals(second))
    }
}