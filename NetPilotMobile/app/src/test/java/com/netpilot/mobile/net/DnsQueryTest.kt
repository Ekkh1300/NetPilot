package com.netpilot.mobile.net

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.ByteArrayOutputStream

/**
 * Regression tests for the DNS wire codec.
 *
 * The header used to be read from offset 4 instead of 2, which meant QDCOUNT was
 * decoded as the flags word. Every normal answer (QDCOUNT=1) therefore reported
 * SERVFAIL, so the health probe, the benchmark and the tools screen all failed
 * even though the packet was perfectly valid.
 */
class DnsQueryTest {

    private fun qname(name: String): ByteArray {
        val out = ByteArrayOutputStream(64)
        for (label in name.split('.')) {
            val b = label.toByteArray(Charsets.US_ASCII)
            out.write(b.size)
            out.write(b)
        }
        out.write(0)
        return out.toByteArray()
    }

    private fun header(flags: Int, qd: Int, an: Int): ByteArray {
        val out = ByteArrayOutputStream(12)
        out.write(0x12); out.write(0x34)                       // ID
        out.write((flags shr 8) and 0xFF); out.write(flags and 0xFF)
        out.write((qd shr 8) and 0xFF); out.write(qd and 0xFF)
        out.write((an shr 8) and 0xFF); out.write(an and 0xFF)
        out.write(0); out.write(0)                             // NSCOUNT
        out.write(0); out.write(0)                             // ARCOUNT
        return out.toByteArray()
    }

    /** A standard response: one question, one compressed A answer (or none on error). */
    private fun aResponse(name: String, ip: String, ttl: Int, flags: Int = 0x8180): ByteArray {
        val rcode = flags and 0x0F
        val out = ByteArrayOutputStream(256)
        out.write(header(flags, 1, if (rcode == 0) 1 else 0))
        out.write(qname(name))
        out.write(byteArrayOf(0, 1, 0, 1))                     // QTYPE A, QCLASS IN
        if (rcode != 0) return out.toByteArray()

        out.write(byteArrayOf(0xC0.toByte(), 0x0C))            // owner: pointer to offset 12
        out.write(byteArrayOf(0, 1, 0, 1))                     // TYPE A, CLASS IN
        out.write(
            byteArrayOf(
                ((ttl ushr 24) and 0xFF).toByte(), ((ttl ushr 16) and 0xFF).toByte(),
                ((ttl ushr 8) and 0xFF).toByte(), (ttl and 0xFF).toByte()
            )
        )
        out.write(byteArrayOf(0, 4))                           // RDLENGTH
        val parts = ip.split('.').map { it.toInt() }
        out.write(byteArrayOf(parts[0].toByte(), parts[1].toByte(), parts[2].toByte(), parts[3].toByte()))
        return out.toByteArray()
    }

    @Test
    fun `noerror answer is not misread as servfail`() {
        val p = DnsQuery.parse(aResponse("example.com", "93.184.216.34", 60))
        assertEquals("rcode must come from the flags word", 0, p.rcode)
        assertEquals(1, p.answers.size)
        assertEquals("example.com", p.answers[0].name)
        assertEquals(DnsQuery.TYPE_A, p.answers[0].type)
        assertEquals("93.184.216.34", p.answers[0].data)
        assertEquals(60, p.answers[0].ttl)
        assertFalse(p.truncated)
    }

    @Test
    fun `nxdomain rcode is reported`() {
        val p = DnsQuery.parse(aResponse("nope.example.com", "0.0.0.0", 0, flags = 0x8183))
        assertEquals(3, p.rcode)
        assertTrue(p.answers.isEmpty())
    }

    @Test
    fun `error rcodes are reported from the flags word`() {
        // rcode lives in the low nibble of the flags word: 1=FORMERR, 2=SERVFAIL, 3=NXDOMAIN.
        assertEquals(1, DnsQuery.parse(aResponse("example.com", "1.2.3.4", 0, flags = 0x8101)).rcode)
        assertEquals(2, DnsQuery.parse(aResponse("example.com", "1.2.3.4", 0, flags = 0x8102)).rcode)
        assertEquals(5, DnsQuery.parse(aResponse("example.com", "1.2.3.4", 0, flags = 0x8105)).rcode)
    }

    @Test
    fun `ttl larger than 65535 decodes correctly`() {
        val p = DnsQuery.parse(aResponse("example.com", "10.0.0.1", 86400))
        assertEquals(86400, p.answers[0].ttl)
    }

    @Test
    fun `truncated flag is detected`() {
        val p = DnsQuery.parse(aResponse("example.com", "10.0.0.1", 60, flags = 0x8380))
        assertTrue(p.truncated)
        assertEquals(0, p.rcode)
    }

    @Test
    fun `null and short messages are rejected without throwing`() {
        assertEquals(-1, DnsQuery.parse(null).rcode)
        assertEquals(-1, DnsQuery.parse(ByteArray(8)).rcode)
    }

    @Test
    fun `question section is skipped before the answers`() {
        val p = DnsQuery.parse(aResponse("www.sub.example.com", "8.8.4.4", 300))
        assertEquals(0, p.rcode)
        assertEquals("8.8.4.4", p.answers[0].data)
        assertEquals(300, p.answers[0].ttl)
    }

    @Test
    fun `query without edns declares zero additional records`() {
        val q = DnsQuery.buildQuery(0x1234, "example.com", DnsQuery.TYPE_A, wantDnssec = false)
        assertEquals(0, ((q[10].toInt() and 0xFF) shl 8) or (q[11].toInt() and 0xFF))
        assertEquals(12 + qname("example.com").size + 4, q.size)
        assertEquals(1, ((q[4].toInt() and 0xFF) shl 8) or (q[5].toInt() and 0xFF))
    }

    @Test
    fun `query with edns carries a complete 11 byte opt record`() {
        val q = DnsQuery.buildQuery(0x1234, "example.com", DnsQuery.TYPE_A, wantDnssec = true)
        assertEquals("ARCOUNT was never applied to the header", 1, ((q[10].toInt() and 0xFF) shl 8) or (q[11].toInt() and 0xFF))

        val opt = 12 + qname("example.com").size + 4
        assertEquals("OPT record must be 11 bytes", opt + 11, q.size)

        fun at(i: Int) = q[i].toInt() and 0xFF
        assertEquals(0x00, at(opt))                      // NAME = root label
        assertEquals(0x00, at(opt + 1))                  // TYPE = OPT (0x0029)
        assertEquals(0x29, at(opt + 2))
        assertEquals(0x04, at(opt + 3))                  // CLASS = UDP payload 1232
        assertEquals(0xD0, at(opt + 4))
        assertEquals(0x00, at(opt + 5))                  // extended RCODE
        assertEquals(0x00, at(opt + 6))                  // version
        assertEquals(0x00, at(opt + 9))                  // RDLENGTH = 0
        assertEquals(0x00, at(opt + 10))
    }
}
