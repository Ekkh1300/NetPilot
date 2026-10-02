package com.netpilot.mobile.vpn

/**
 * IPv4 / UDP / TCP header parsing and building for the local tunnel.
 *
 * Deliberately free of Android types: everything here is plain Kotlin, which is what lets
 * the relay be unit-tested on the host (`app/src/test`) even though no device is attached
 * to this machine. The service and the relay both go through this single implementation so
 * the two can never disagree about a header layout.
 */
internal object Packet {

    const val ICMP = 1
    const val TCP = 6
    const val UDP = 17

    // ---- TCP flags
    const val FIN = 0x01
    const val SYN = 0x02
    const val RST = 0x04
    const val PSH = 0x08
    const val ACK = 0x10

    /** Smallest window we are willing to advertise before stopping to read from a channel. */
    const val MAX_WINDOW = 65535

    // ------------------------------------------------------------------ reading

    fun ipOf(p: ByteArray, off: Int): Int =
        ((p[off].toInt() and 0xFF) shl 24) or
                ((p[off + 1].toInt() and 0xFF) shl 16) or
                ((p[off + 2].toInt() and 0xFF) shl 8) or
                (p[off + 3].toInt() and 0xFF)

    fun portOf(p: ByteArray, off: Int): Int =
        ((p[off].toInt() and 0xFF) shl 8) or (p[off + 1].toInt() and 0xFF)

    /** Unsigned 32-bit read — TCP sequence/acknowledgement numbers are modular. */
    fun u32(p: ByteArray, off: Int): Long =
        ((p[off].toLong() and 0xFF) shl 24) or
                ((p[off + 1].toLong() and 0xFF) shl 16) or
                ((p[off + 2].toLong() and 0xFF) shl 8) or
                (p[off + 3].toLong() and 0xFF)

    data class TcpHdr(
        val srcPort: Int,
        val dstPort: Int,
        val seq: Long,
        val ack: Long,
        val flags: Int,
        val window: Int,
        /** Header length in bytes including options. */
        val headerLen: Int = 0,
        /** Maximum segment size announced in the SYN options, or 0 when absent. */
        val mss: Int = 0
    )

    /** Parses a TCP header that starts at [ihl] (the IP header length). */
    fun parseTcp(p: ByteArray, ihl: Int): TcpHdr? {
        if (p.size < ihl + 20) return null
        val dataOffset = ((p[ihl + 12].toInt() and 0xFF) shr 4) * 4
        if (dataOffset < 20 || p.size < ihl + dataOffset) return null
        var mss = 0
        var o = ihl + 20
        val oEnd = ihl + dataOffset
        while (o + 1 < oEnd) {
            val kind = p[o].toInt() and 0xFF
            if (kind == 0) break                       // end of options
            if (kind == 1) { o++; continue }            // NOP
            val len = p[o + 1].toInt() and 0xFF
            if (len < 2 || o + len > oEnd) break
            if (kind == 2 && len == 4) {
                mss = ((p[o + 2].toInt() and 0xFF) shl 8) or (p[o + 3].toInt() and 0xFF)
            }
            o += len
        }
        return TcpHdr(
            srcPort = portOf(p, ihl),
            dstPort = portOf(p, ihl + 2),
            seq = u32(p, ihl + 4),
            ack = u32(p, ihl + 8),
            flags = p[ihl + 13].toInt() and 0xFF,
            window = ((p[ihl + 14].toInt() and 0xFF) shl 8) or (p[ihl + 15].toInt() and 0xFF),
            headerLen = dataOffset,
            mss = mss
        )
    }

    data class UdpHdr(
        val srcPort: Int,
        val dstPort: Int,
        /** Offset of the payload inside the IP packet. */
        val payloadOff: Int,
        val payloadEnd: Int
    )

    fun parseUdp(p: ByteArray, ihl: Int): UdpHdr? {
        if (p.size < ihl + 8) return null
        val len = ((p[ihl + 4].toInt() and 0xFF) shl 8) or (p[ihl + 5].toInt() and 0xFF)
        if (len < 8) return null
        val end = (ihl + len).coerceAtMost(p.size)
        val payloadOff = ihl + 8
        if (end < payloadOff) return null
        return UdpHdr(portOf(p, ihl), portOf(p, ihl + 2), payloadOff, end)
    }

    // ------------------------------------------------------------------ writing

    fun writeShort(p: ByteArray, off: Int, v: Int) {
        p[off] = ((v ushr 8) and 0xFF).toByte()
        p[off + 1] = (v and 0xFF).toByte()
    }

    fun writeInt(p: ByteArray, off: Int, v: Int) {
        p[off] = ((v ushr 24) and 0xFF).toByte()
        p[off + 1] = ((v ushr 16) and 0xFF).toByte()
        p[off + 2] = ((v ushr 8) and 0xFF).toByte()
        p[off + 3] = (v and 0xFF).toByte()
    }

    fun writeU32(p: ByteArray, off: Int, v: Long) {
        p[off] = ((v ushr 24) and 0xFF).toByte()
        p[off + 1] = ((v ushr 16) and 0xFF).toByte()
        p[off + 2] = ((v ushr 8) and 0xFF).toByte()
        p[off + 3] = (v and 0xFF).toByte()
    }

    /**
     * Wrap-around subtraction for 32-bit sequence numbers: `a - b` interpreted as a signed
     * distance on the circular sequence space. Works across the 2^32 wrap point.
     */
    fun seqDelta(a: Long, b: Long): Long {
        var d = (a and 0xFFFFFFFFL) - (b and 0xFFFFFFFFL)
        if (d > 0x7FFFFFFFL) d -= 0x100000000L
        else if (d < -0x80000000L) d += 0x100000000L
        return d
    }

    fun ipChecksum(data: ByteArray, off: Int, len: Int): Int {
        var sum = 0L
        var i = off
        val end = off + len
        while (i + 1 < end) {
            sum += ((data[i].toInt() and 0xFF) shl 8) or (data[i + 1].toInt() and 0xFF)
            i += 2
        }
        if (i < end) sum += (data[i].toInt() and 0xFF) shl 8
        while (sum ushr 16 != 0L) sum = (sum and 0xFFFF) + (sum ushr 16)
        return sum.inv().toInt() and 0xFFFF
    }

    /** TCP checksum including the IPv4 pseudo header. [p] must hold the header at [tcpOff]. */
    fun tcpChecksum(p: ByteArray, tcpOff: Int, tcpLen: Int, srcIp: Int, dstIp: Int): Int {
        var sum = 0L
        sum += (srcIp ushr 16) and 0xFFFF
        sum += srcIp and 0xFFFF
        sum += (dstIp ushr 16) and 0xFFFF
        sum += dstIp and 0xFFFF
        sum += TCP
        sum += tcpLen
        var i = tcpOff
        val end = tcpOff + tcpLen
        while (i + 1 < end) {
            sum += ((p[i].toInt() and 0xFF) shl 8) or (p[i + 1].toInt() and 0xFF)
            i += 2
        }
        if (i < end) sum += (p[i].toInt() and 0xFF) shl 8
        while (sum ushr 16 != 0L) sum = (sum and 0xFFFF) + (sum ushr 16)
        val c = sum.inv().toInt() and 0xFFFF
        // A computed zero must be sent as 0xFFFF (RFC 768 / RFC 1071).
        return if (c == 0) 0xFFFF else c
    }

    private fun mssOption(mss: Int): ByteArray {
        val o = ByteArray(4)
        o[0] = 2
        o[1] = 4
        writeShort(o, 2, mss)
        return o
    }

    /**
     * Builds a complete IPv4 + TCP packet.
     *
     * @param options TCP options, must pad to a multiple of four bytes.
     */
    fun buildTcp(
        srcIp: Int, srcPort: Int,
        dstIp: Int, dstPort: Int,
        seq: Long, ack: Long,
        flags: Int, window: Int,
        options: ByteArray = ByteArray(0),
        payload: ByteArray = ByteArray(0),
        ipId: Int
    ): ByteArray {
        // The data offset counts 32-bit words: pad a stray byte instead of emitting a
        // header whose length silently truncates.
        val opts =
            if (options.size % 4 == 0) options
            else options + ByteArray(4 - options.size % 4)
        val hlen = 20 + opts.size
        val tcpLen = hlen + payload.size
        val p = ByteArray(20 + tcpLen)

        p[0] = 0x45
        writeShort(p, 2, 20 + tcpLen)          // IPv4 total length = header + TCP segment
        writeShort(p, 4, ipId and 0xFFFF)
        p[6] = 0x40                    // don't fragment (segments never exceed the TUN MTU)
        p[7] = 0
        p[8] = 64                      // TTL
        p[9] = TCP.toByte()
        writeInt(p, 12, srcIp)
        writeInt(p, 16, dstIp)
        writeShort(p, 10, ipChecksum(p, 0, 20))

        writeShort(p, 20, srcPort)
        writeShort(p, 22, dstPort)
        writeU32(p, 24, seq)
        writeU32(p, 28, ack)
        p[32] = ((hlen / 4) shl 4).toByte()
        p[33] = flags.toByte()
        writeShort(p, 34, window and 0xFFFF)
        if (opts.isNotEmpty()) System.arraycopy(opts, 0, p, 40, opts.size)
        if (payload.isNotEmpty()) System.arraycopy(payload, 0, p, 40 + opts.size, payload.size)
        writeShort(p, 36, tcpChecksum(p, 20, tcpLen, srcIp, dstIp))
        return p
    }

    /** SYN|ACK for a fresh connection, carrying our MSS. */
    fun buildSynAck(
        serverIp: Int, serverPort: Int,
        clientIp: Int, clientPort: Int,
        ourSeq: Long, clientSeq: Long,
        mss: Int, window: Int, ipId: Int
    ): ByteArray = buildTcp(
        srcIp = serverIp, srcPort = serverPort,
        dstIp = clientIp, dstPort = clientPort,
        seq = ourSeq, ack = clientSeq,
        flags = SYN or ACK, window = window,
        options = mssOption(mss), ipId = ipId
    )

    /**
     * Reset for a segment we refuse to carry. RFC 793: answer a bare SYN with
     * RST|ACK at seq 0 / ack = SEG.SEQ + 1 so the application fails immediately
     * instead of waiting for a timeout.
     */
    fun buildRst(
        p: ByteArray, ihl: Int,
        srcIp: Int, dstIp: Int,
        ipId: Int
    ): ByteArray? {
        val h = parseTcp(p, ihl) ?: return null
        val bareSyn = (h.flags and SYN) != 0 && (h.flags and ACK) == 0
        return if (bareSyn) {
            buildTcp(
                srcIp = srcIp, srcPort = h.dstPort,
                dstIp = dstIp, dstPort = h.srcPort,
                seq = 0, ack = (h.seq + 1) and 0xFFFFFFFFL,
                flags = RST or ACK, window = 0, ipId = ipId
            )
        } else {
            buildTcp(
                srcIp = srcIp, srcPort = h.dstPort,
                dstIp = dstIp, dstPort = h.srcPort,
                seq = h.ack, ack = 0,
                flags = RST, window = 0, ipId = ipId
            )
        }
    }

    /** IPv4 + UDP packet. The UDP checksum stays 0, which IPv4 explicitly allows. */
    fun buildUdp(
        payload: ByteArray,
        srcIp: Int, srcPort: Int,
        dstIp: Int, dstPort: Int,
        ipId: Int
    ): ByteArray {
        val udpLen = 8 + payload.size
        val total = 20 + udpLen
        val out = ByteArray(total)
        out[0] = 0x45
        writeShort(out, 2, total)
        writeShort(out, 4, ipId and 0xFFFF)
        out[6] = 0x40
        out[8] = 64
        out[9] = UDP.toByte()
        writeInt(out, 12, srcIp)
        writeInt(out, 16, dstIp)
        writeShort(out, 10, ipChecksum(out, 0, 20))
        writeShort(out, 20, srcPort)
        writeShort(out, 22, dstPort)
        writeShort(out, 24, udpLen)
        writeShort(out, 26, 0)
        if (payload.isNotEmpty()) System.arraycopy(payload, 0, out, 28, payload.size)
        return out
    }
}
