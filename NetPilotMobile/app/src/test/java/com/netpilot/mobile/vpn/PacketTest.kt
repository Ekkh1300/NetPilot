package com.netpilot.mobile.vpn

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Header layout and checksums.
 *
 * The expected values in here were produced by an independent Python implementation
 * (byte-wise one's complement sum, pseudo header spelled out by hand) — not by the code
 * under test — so a mistake in [Packet] cannot hide behind a matching re-run of itself.
 */
class PacketTest {

    // ------------------------------------------------------------------ reference vectors

    @Test
    fun tcpSynAckChecksumMatchesReference() {
        val pkt = Packet.buildTcp(
            srcIp = ip("10.111.222.1"), srcPort = 40000,
            dstIp = ip("192.168.1.20"), dstPort = 443,
            seq = 0x11223344L, ack = 0x55667788L,
            flags = Packet.SYN or Packet.ACK, window = 0xFFFF,
            options = byteArrayOf(2, 4, 5, 0xB4.toByte()),   // MSS 1460
            ipId = 0x1234
        )
        assertEquals("IP header is 20 bytes", 44, pkt.size)
        assertEquals("IPv4 header checksum", 0x7E6B, u16(pkt, 10))
        assertEquals("TCP checksum (with pseudo header)", 0x3E99, u16(pkt, 36))
        assertTrue("self-consistent checksums", checksumsValid(pkt))
    }

    @Test
    fun tcpDataSegmentChecksumMatchesReference() {
        val pkt = Packet.buildTcp(
            srcIp = ip("10.111.222.1"), srcPort = 40000,
            dstIp = ip("192.168.1.20"), dstPort = 443,
            seq = 0x11223345L, ack = 0x55667789L,
            flags = Packet.ACK or Packet.PSH, window = 0x1000,
            payload = "hello".toByteArray(),
            ipId = 0x1234
        )
        assertEquals(45, pkt.size)
        assertEquals(0x7E6A, u16(pkt, 10))
        assertEquals(0x0276, u16(pkt, 36))
        assertTrue(checksumsValid(pkt))
    }

    @Test
    fun udpDatagramChecksumMatchesReference() {
        val pkt = Packet.buildUdp(
            payload = "hi".toByteArray(),
            srcIp = ip("8.8.8.8"), srcPort = 53,
            dstIp = ip("10.111.222.1"), dstPort = 50000,
            ipId = 0x4321
        )
        assertEquals(30, pkt.size)
        assertEquals("IPv4 header checksum", 0xFF2D, u16(pkt, 10))
        assertEquals("UDP checksum field stays 0 (legal in IPv4)", 0, u16(pkt, 26))
        assertTrue(checksumsValid(pkt))

        val h = Packet.parseUdp(pkt, 20)
        assertNotNull(h)
        assertEquals(53, h!!.srcPort)
        assertEquals(50000, h.dstPort)
        assertEquals("hi", String(pkt.copyOfRange(h.payloadOff, h.payloadEnd)))
    }

    // ------------------------------------------------------------------ parsing

    @Test
    fun parseTcpReadsFlagsWindowAndMss() {
        val pkt = Packet.buildTcp(
            srcIp = ip("10.111.222.1"), srcPort = 1234,
            dstIp = ip("1.1.1.1"), dstPort = 443,
            seq = 7L, ack = 9L,
            flags = Packet.SYN, window = 12345,
            options = byteArrayOf(2, 4, 5, 0xB4.toByte()),
            ipId = 1
        )
        val h = Packet.parseTcp(pkt, 20)
        assertNotNull(h)
        assertEquals(1234, h!!.srcPort)
        assertEquals(443, h.dstPort)
        assertEquals(7L, h.seq)
        assertEquals(9L, h.ack)
        assertEquals(Packet.SYN, h.flags)
        assertEquals(12345, h.window)
        assertEquals(24, h.headerLen)
        assertEquals(1460, h.mss)
    }

    @Test
    fun parseTcpRejectsTruncatedHeader() {
        assertEquals(null, Packet.parseTcp(ByteArray(24), 20))
        val ok = Packet.buildTcp(
            srcIp = 1, srcPort = 1, dstIp = 2, dstPort = 2,
            seq = 0, ack = 0, flags = Packet.ACK, window = 1, ipId = 1
        )
        assertEquals(null, Packet.parseTcp(ok, 40))
    }

    @Test
    fun seqDeltaHandlesWrapAround() {
        assertEquals(0L, Packet.seqDelta(0xFFFFFFFFL, 0xFFFFFFFFL))
        assertEquals(5L, Packet.seqDelta(10L, 5L))
        assertEquals(-5L, Packet.seqDelta(5L, 10L))
        // 0x00000003 is *after* 0xFFFFFFFE by 5 in sequence space.
        assertEquals(5L, Packet.seqDelta(0x00000003L, 0xFFFFFFFEL))
        assertEquals(-5L, Packet.seqDelta(0xFFFFFFFEL, 0x00000003L))
        // Nothing in one sequence space may be mistaken for ~4 billion bytes away.
        assertTrue(kotlin.math.abs(Packet.seqDelta(1L, 0xFFFFFFFEL)) <= 10L)
    }

    @Test
    fun mssOptionIsSkippedWhenNotAHostOption() {
        // NOP, NOP, MSS — walks the option list instead of assuming a fixed offset.
        val pkt = Packet.buildTcp(
            srcIp = ip("10.111.222.1"), srcPort = 1,
            dstIp = ip("10.0.0.1"), dstPort = 2,
            seq = 0, ack = 0, flags = Packet.SYN, window = 1,
            options = byteArrayOf(1, 1, 2, 4, 5, 0x0C, 0x00, 0x00),
            ipId = 1
        )
        assertEquals(1292, Packet.parseTcp(pkt, 20)!!.mss)
    }

    // ------------------------------------------------------------------ refusal path

    @Test
    fun refusedSynIsAnsweredWithRstAck() {
        val syn = Packet.buildTcp(
            srcIp = ip("10.111.222.1"), srcPort = 40000,
            dstIp = ip("192.168.1.20"), dstPort = 443,
            seq = 0x0A0B0C0DL, ack = 0, flags = Packet.SYN, window = 65535, ipId = 1
        )
        val rst = Packet.buildRst(syn, 20, ip("192.168.1.20"), ip("10.111.222.1"), 0x1234)
        assertNotNull(rst)

        val h = Packet.parseTcp(rst!!, 20)!!
        assertEquals("answers the source port", 443, h.srcPort)
        assertEquals("answers the destination port", 40000, h.dstPort)
        assertEquals(Packet.RST or Packet.ACK, h.flags)
        assertEquals("bare SYN consumes one sequence number", 0x0A0B0C0EL, h.ack)
        assertEquals(0L, h.seq)
        assertTrue("refusal is deliverable", checksumsValid(rst))

        // A segment that already carries an ACK is reset with a bare RST at SEG.ACK.
        val data = Packet.buildTcp(
            srcIp = ip("10.111.222.1"), srcPort = 40000,
            dstIp = ip("192.168.1.20"), dstPort = 443,
            seq = 5L, ack = 6L, flags = Packet.ACK, window = 1, ipId = 1
        )
        val rst2 = Packet.buildRst(data, 20, ip("192.168.1.20"), ip("10.111.222.1"), 7)!!
        val h2 = Packet.parseTcp(rst2, 20)!!
        assertEquals(Packet.RST, h2.flags)
        assertEquals(6L, h2.seq)
        assertTrue(checksumsValid(rst2))
    }

    @Test
    fun buildTcpKeepsTheHeaderOnAWordBoundary() {
        // The data offset is stored in 32-bit words, so options are padded rather than
        // silently truncating the header length.
        val options = byteArrayOf(2, 4, 5, 0xB4.toByte())
        val pkt = Packet.buildTcp(
            srcIp = 1, srcPort = 1, dstIp = 2, dstPort = 2,
            seq = 0, ack = 0, flags = Packet.SYN, window = 1,
            options = options, ipId = 1
        )
        assertEquals(44, pkt.size)
        assertEquals(0x60, (pkt[32].toInt() and 0xFF))   // data offset 6 words

        // One stray byte must not produce an off-word header.
        val odd = Packet.buildTcp(
            srcIp = 1, srcPort = 1, dstIp = 2, dstPort = 2,
            seq = 0, ack = 0, flags = Packet.SYN, window = 1,
            options = byteArrayOf(1, 1, 1), ipId = 1
        )
        val headerBytes = ((odd[32].toInt() and 0xFF) shr 4) * 4
        assertEquals("3 bytes of options pad up to a whole word", 24, headerBytes)
        assertEquals("20 IP + 24 TCP", 44, odd.size)
        assertTrue(checksumsValid(odd))
    }

    // ------------------------------------------------------------------ helpers

    private fun ip(s: String): Int {
        val parts = s.split(".")
        return ((parts[0].toInt() and 0xFF) shl 24) or
                ((parts[1].toInt() and 0xFF) shl 16) or
                ((parts[2].toInt() and 0xFF) shl 8) or
                (parts[3].toInt() and 0xFF)
    }

    private fun u16(p: ByteArray, off: Int): Int =
        ((p[off].toInt() and 0xFF) shl 8) or (p[off + 1].toInt() and 0xFF)

    /**
     * Independent checksum verifier: a naive int accumulation folded at the end, plus the
     * RFC 1071 property that a packet carrying a correct checksum folds to zero.
     */
    private fun checksumsValid(p: ByteArray): Boolean {
        if (p.size < 20) return false
        val ihl = (p[0].toInt() and 0x0F) * 4
        if (ihl < 20 || p.size < ihl) return false
        val total = u16(p, 2)
        if (total != p.size) return false
        if (fold(sum(p, 0, ihl)) != 0xFFFF) return false

        val proto = p[9].toInt() and 0xFF
        if (proto != Packet.TCP) return true

        val tcpLen = total - ihl
        val pseudo = ByteArray(12)
        System.arraycopy(p, 12, pseudo, 0, 4)
        System.arraycopy(p, 16, pseudo, 4, 4)
        pseudo[9] = proto.toByte()
        pseudo[10] = ((tcpLen ushr 8) and 0xFF).toByte()
        pseudo[11] = (tcpLen and 0xFF).toByte()

        val all = ByteArray(12 + tcpLen)
        System.arraycopy(pseudo, 0, all, 0, 12)
        System.arraycopy(p, ihl, all, 12, tcpLen)
        return fold(sum(all, 0, all.size)) == 0xFFFF
    }

    private fun sum(data: ByteArray, off: Int, len: Int): Int {
        var s = 0
        var i = off
        val end = off + len
        while (i + 1 < end) {
            s += ((data[i].toInt() and 0xFF) shl 8) or (data[i + 1].toInt() and 0xFF)
            if (s > 0xFFFF) s = (s and 0xFFFF) + (s ushr 16)
            i += 2
        }
        if (i < end) s += (data[i].toInt() and 0xFF) shl 8
        return s
    }

    private fun fold(v: Int): Int {
        var x = v
        while (x ushr 16 != 0) x = (x and 0xFFFF) + (x ushr 16)
        return x
    }
}
