package com.netpilot.mobile.vpn

import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * VpnService.Builder routes both address strings through InetAddress.parseNumericAddress(),
 * which rejects an octet over 255 — and Builder.addDnsServer() additionally refuses loopback
 * and 0.0.0.0. The constant used to read 10.111.222.333, so addDnsServer() threw, startTunnel()
 * caught it and stopped, establish() was never reached and "Apply DNS" did nothing at all.
 *
 * These tests are the guard against that class of typo: the addresses are checked against the
 * same rules the framework applies, without needing a device.
 */
class VpnAddrTest {

    /** Strict dotted-quad parse, mirroring InetAddress.parseNumericAddress(). */
    private fun ipv4(value: String): IntArray {
        val parts = value.split(".")
        assertTrue("$value is not four octets", parts.size == 4)
        return IntArray(4) { i ->
            val part = parts[i]
            assertTrue("$value has an empty octet", part.isNotEmpty())
            assertTrue("$value octet '$part' is not decimal digits", part.all { it in '0'..'9' })
            assertTrue("$value octet '$part' is longer than three digits", part.length <= 3)
            val parsed = part.toInt()
            assertTrue("$value octet $parsed is over 255", parsed <= 255)
            parsed
        }
    }

    @Test
    fun fakeDnsIsAValidIpv4Literal() {
        ipv4(NetPilotVpnService.FAKE_DNS)
    }

    @Test
    fun localAddressIsAValidIpv4Literal() {
        ipv4(NetPilotVpnService.LOCAL_ADDR)
    }

    @Test
    fun fakeDnsIsAcceptedByAddDnsServer() {
        // addDnsServer(): "if (address.isLoopbackAddress() || address.isAnyLocalAddress()) throw"
        val addr = ipv4(NetPilotVpnService.FAKE_DNS)
        assertTrue("loopback is refused by addDnsServer()", addr[0] != 127)
        assertTrue("0.0.0.0 is refused by addDnsServer()", addr.any { it != 0 })
    }

    @Test
    fun fakeDnsAndLocalAddressAreDifferentHosts() {
        // The /32 route must not point at the interface address itself.
        assertTrue(
            "FAKE_DNS must differ from LOCAL_ADDR",
            NetPilotVpnService.FAKE_DNS != NetPilotVpnService.LOCAL_ADDR
        )
    }

    @Test
    fun fakeDnsKeepsTheAddressFamilyAlive() {
        // addAddress()/addDnsServer() are what allow IPv4 traffic; both must stay IPv4.
        assertTrue(NetPilotVpnService.FAKE_DNS.contains('.'))
        assertTrue(NetPilotVpnService.LOCAL_ADDR.contains('.'))
    }
}
