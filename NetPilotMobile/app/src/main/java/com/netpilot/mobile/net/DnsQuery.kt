package com.netpilot.mobile.net

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket
import java.util.Random
import kotlin.math.max

/**
 * Minimal, dependency-free DNS client (UDP with TCP fallback) used by the benchmark, the
 * Smart-DNS detector, the NSLookup tool and the local tunnel. Mirrors the desktop app's
 * DnsQuery: real packets on the wire, real timing, real loss accounting.
 */
object DnsQuery {

    const val TYPE_A = 1
    const val TYPE_AAAA = 28
    const val TYPE_CNAME = 5
    const val TYPE_MX = 15
    const val TYPE_TXT = 16
    const val TYPE_NS = 2
    const val TYPE_PTR = 12
    const val TYPE_SOA = 6

    data class Result(
        val ok: Boolean,
        val ms: Int,
        val ip: String = "",
        val cname: String = "",
        val rcode: Int = 0,
        val answerCount: Int = 0,
        val raw: ByteArray? = null,
        val error: String = ""
    ) {
        override fun equals(other: Any?): Boolean = this === other
        override fun hashCode(): Int = ms
    }

    // ------------------------------------------------------------------ wire format

    private fun encodeName(name: String): ByteArray {
        val out = ByteArrayOutputStream(64)
        val clean = name.trim().trimEnd('.')
        if (clean.isNotEmpty()) {
            for (label in clean.split('.')) {
                val bytes = label.toByteArray(Charsets.US_ASCII)
                out.write(bytes.size)
                out.write(bytes)
            }
        }
        out.write(0)
        return out.toByteArray()
    }

    internal fun buildQuery(id: Int, name: String, type: Int, wantDnssec: Boolean = false): ByteArray {
        val out = ByteArrayOutputStream(128)
        out.write((id shr 8) and 0xFF)
        out.write(id and 0xFF)
        out.write(0x01)  // RD
        out.write(0x00)
        out.write(0x00); out.write(0x01)  // QDCOUNT = 1
        out.write(0x00); out.write(0x00)  // ANCOUNT
        out.write(0x00); out.write(0x00)  // NSCOUNT
        // ARCOUNT must be written here: it cannot be patched afterwards, because
        // ByteArrayOutputStream.toByteArray() returns a copy.
        out.write(0x00); out.write(if (wantDnssec) 0x01 else 0x00)
        out.write(encodeName(name))
        out.write((type shr 8) and 0xFF); out.write(type and 0xFF)
        out.write(0x00); out.write(0x01)  // IN
        if (wantDnssec) {
            // EDNS0 OPT pseudo-record (RFC 6891), full 11-byte layout:
            // NAME(root,1) + TYPE(2) + CLASS(2) + TTL(4) + RDLENGTH(2).
            out.write(0x00)                 // root label
            out.write(0x00); out.write(0x29) // TYPE = OPT (41)
            out.write(0x04); out.write(0xD0) // CLASS = advertised UDP payload size (1232)
            out.write(0x00)                  // TTL byte 1: extended RCODE
            out.write(0x00)                  // TTL byte 2: version
            out.write(0x00); out.write(0x00) // TTL bytes 3-4: DO bit (0) + Z (0)
            out.write(0x00); out.write(0x00) // RDLENGTH = 0 (no options)
        }
        return out.toByteArray()
    }

    private class Reader(private val b: ByteArray) {
        var pos = 0
        fun u8(): Int = if (pos < b.size) (b[pos++].toInt() and 0xFF) else 0
        fun u16(): Int = (u8() shl 8) or u8()
        // Two 16-bit halves of a 32-bit big-endian field: high half shifts by 16.
        fun u32(): Long = ((u16().toLong()) shl 16) or u16().toLong()

        fun name(): String {
            val sb = StringBuilder()
            var hops = 0
            while (pos < b.size && hops++ < 64) {
                val len = u8()
                if (len == 0) break
                if (len and 0xC0 == 0xC0) {
                    val ptr = ((len and 0x3F) shl 8) or u8()
                    val save = pos
                    pos = ptr
                    val tail = name()
                    pos = save
                    if (sb.isNotEmpty()) sb.append('.')
                    sb.append(tail)
                    break
                }
                if (pos + len > b.size) break
                if (sb.isNotEmpty()) sb.append('.')
                sb.append(String(b, pos, len, Charsets.US_ASCII))
                pos += len
            }
            return sb.toString()
        }
    }

    data class Answer(
        val name: String,
        val type: Int,
        val ttl: Int,
        val data: String
    )

    data class Parsed(
        val rcode: Int,
        val answers: List<Answer>,
        val truncated: Boolean
    ) {
        val first: String get() = answers.firstOrNull()?.data ?: ""
    }

    /** Parse a DNS response message. Tolerant: malformed packets never throw. */
    fun parse(msg: ByteArray?): Parsed {
        if (msg == null || msg.size < 12) return Parsed(-1, emptyList(), false)
        return try {
            val r = Reader(msg)
            // Header layout: 0-1 ID, 2-3 flags, 4-5 QDCOUNT, 6-7 ANCOUNT,
            // 8-9 NSCOUNT, 10-11 ARCOUNT. Skipping 4 bytes would read QDCOUNT
            // as the flags word, so every NOERROR reply (QDCOUNT=1) looked like
            // SERVFAIL and every probe reported "unknown".
            r.pos = 2
            val flags = r.u16()
            val qd = r.u16()
            val an = r.u16()
            r.u16(); r.u16()
            repeat(qd) { r.name(); r.u16(); r.u16() }
            val answers = ArrayList<Answer>(an)
            repeat(an) {
                if (r.pos >= msg.size) return@repeat
                val owner = r.name()
                val type = r.u16()
                r.u16()            // class
                val ttl = r.u32().toInt()
                val rdlen = r.u16()
                if (r.pos + rdlen > msg.size) return@repeat
                val start = r.pos
                val data = when (type) {
                    TYPE_A -> if (rdlen == 4) listOf(
                        msg[start].toInt() and 0xFF, msg[start + 1].toInt() and 0xFF,
                        msg[start + 2].toInt() and 0xFF, msg[start + 3].toInt() and 0xFF
                    ).joinToString(".") else ""
                    TYPE_AAAA -> if (rdlen == 16) formatIpv6(msg.copyOfRange(start, start + 16)) else ""
                    else -> ""
                }
                val text = when (type) {
                    TYPE_A, TYPE_AAAA -> data
                    TYPE_CNAME, TYPE_NS, TYPE_PTR -> {
                        val sub = Reader(msg); sub.pos = start; sub.name()
                    }
                    else -> data
                }
                answers.add(Answer(owner, type, ttl, text))
                r.pos = start + rdlen
            }
            Parsed(flags and 0x0F, answers, flags and 0x0200 != 0)
        } catch (t: Throwable) {
            Parsed(-1, emptyList(), false)
        }
    }

    private fun formatIpv6(b: ByteArray): String {
        val parts = ArrayList<String>(8)
        var i = 0
        while (i < 16) {
            val v = ((b[i].toInt() and 0xFF) shl 8) or (b[i + 1].toInt() and 0xFF)
            parts.add(v.toString(16))
            i += 2
        }
        return parts.joinToString(":")
    }

    // ------------------------------------------------------------------ transport

    /** One UDP exchange against [server]:53 with a hard timeout. */
    suspend fun query(
        server: String,
        name: String,
        type: Int = TYPE_A,
        timeoutMs: Int = 2500
    ): Result = withContext(Dispatchers.IO) {
        val id = Random().nextInt(0x10000)
        val payload = buildQuery(id, name, type, wantDnssec = true)
        val start = System.currentTimeMillis()
        try {
            val addr = InetAddress.getByName(server)
            var msg: ByteArray? = null

            DatagramSocket().use { sock ->
                sock.soTimeout = timeoutMs
                val target = InetSocketAddress(addr, 53)
                sock.send(DatagramPacket(payload, payload.size, target))
                val buf = ByteArray(4096)
                val deadline = start + timeoutMs
                while (msg == null) {
                    val remaining = deadline - System.currentTimeMillis()
                    if (remaining <= 0) break
                    sock.soTimeout = remaining.toInt().coerceAtLeast(1)
                    val packet = DatagramPacket(buf, buf.size)
                    try {
                        sock.receive(packet)
                    } catch (t: java.net.SocketTimeoutException) {
                        break
                    }
                    val copy = buf.copyOf(packet.length)
                    val head = Reader(copy)
                    val respId = head.u16()
                    if (respId != id) continue          // stray packet, keep waiting
                    msg = copy
                }
                if (msg != null && parse(msg).truncated) {
                    // Over TCP we would get the full answer, but a network that filters port
                    // 53/TCP leaves us with nothing at all - and the caller then reported a
                    // plain "timeout" although a (truncated) answer had already arrived. Keep
                    // what we have and only prefer the TCP version when it really came back.
                    val overTcp = tcpQuery(addr, id, payload, timeoutMs)
                    if (overTcp != null) msg = overTcp
                }
            }

            val ms = (System.currentTimeMillis() - start).toInt()
            if (msg == null) return@withContext Result(false, -1, error = "timeout")
            val parsed = parse(msg)
            val first = parsed.answers.firstOrNull { it.type == type }?.data
                ?: parsed.answers.firstOrNull()?.data ?: ""
            Result(
                ok = parsed.rcode == 0 && parsed.answers.isNotEmpty(),
                ms = ms,
                ip = first,
                cname = parsed.answers.firstOrNull { it.type == TYPE_CNAME }?.data ?: "",
                rcode = parsed.rcode,
                answerCount = parsed.answers.size,
                raw = msg
            )
        } catch (t: Throwable) {
            Result(false, -1, error = t.javaClass.simpleName)
        }
    }

    private fun tcpQuery(addr: InetAddress, id: Int, query: ByteArray, timeoutMs: Int): ByteArray? {
        return try {
            Socket().use { s ->
                s.connect(InetSocketAddress(addr, 53), timeoutMs)
                s.soTimeout = timeoutMs
                val out = s.getOutputStream()
                out.write((query.size shr 8) and 0xFF)
                out.write(query.size and 0xFF)
                out.write(query)
                out.flush()
                val inp = s.getInputStream()
                val lenHi = inp.read()
                val lenLo = inp.read()
                if (lenHi < 0 || lenLo < 0) return null
                val len = (lenHi shl 8) or lenLo
                val buf = ByteArray(len)
                var read = 0
                while (read < len) {
                    val n = inp.read(buf, read, len - read)
                    if (n < 0) break
                    read += n
                }
                if (read == len) buf else null
            }
        } catch (t: Throwable) {
            null
        }
    }

    /** Run [queries] against one resolver and report loss + average of the successes. */
    suspend fun probe(
        server: String,
        name: String = "www.google.com",
        rounds: Int = 3,
        timeoutMs: Int = 2000,
        parallel: Boolean = false
    ): ProbeStats = coroutineScope {
        val results: List<Result> = if (parallel) {
            (0 until rounds).map { async { query(server, name, TYPE_A, timeoutMs) } }.awaitAll()
        } else {
            (0 until rounds).map { query(server, name, TYPE_A, timeoutMs) }
        }
        val ok = results.filter { it.ok && it.ms >= 0 }
        val times = ok.map { it.ms }
        val avg = if (times.isEmpty()) -1 else times.average().toInt()
        var jitter = 0
        if (times.size > 1) {
            var sum = 0
            for (i in 1 until times.size) sum += kotlin.math.abs(times[i] - times[i - 1])
            jitter = sum / (times.size - 1)
        }
        ProbeStats(
            server = server,
            attempts = results.size,
            success = ok.size,
            avgMs = avg,
            jitterMs = jitter,
            lossPct = if (results.isEmpty()) 0.0 else (results.size - ok.size) * 100.0 / results.size,
            times = times
        )
    }

    data class ProbeStats(
        val server: String,
        val attempts: Int,
        val success: Int,
        val avgMs: Int,
        val jitterMs: Int,
        val lossPct: Double,
        val times: List<Int>
    )

    /** Which resolvers the system is currently configured to use (LinkProperties DNS). */
    fun isPrivate(server: String): Boolean = try {
        val parts = server.split('.')
        if (parts.size != 4) false
        else {
            val a = parts[0].toInt(); val b = parts[1].toInt()
            a == 10 || (a == 192 && b == 168) || (a == 172 && b in 16..31) ||
                    (a == 127) || (a == 169 && b == 254)
        }
    } catch (t: Throwable) {
        false
    }

    fun max(a: Int, b: Int): Int = max(a, b)
}
