package com.netpilot.mobile.pc

import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * Address parsing, the input nobody controls.
 *
 * This exists because a whole connection attempt died on it: the desktop prints
 * `http://pc:8787`, the user pasted that into the phone, and the app then asked for
 * `http://http://pc:8787:8787/ping`. The host parsed as `http`, DNS failed with
 * UnknownHostException, and the user was told "Wrong address, or the two devices are on
 * different networks" — a message that could not be acted on. Every shape a human can
 * reasonably type is pinned here.
 */
class TargetTest {

    @Test
    fun pastedWindowsAddressKeepsItsRealPort() {
        assertEquals("pc" to 8787, normalizeTarget("http://pc:8787", 9999))
    }

    @Test
    fun pastedIpWithSchemeAndPort() {
        assertEquals("192.168.1.100" to 8787, normalizeTarget("http://192.168.1.100:8787", 9999))
    }

    @Test
    fun pastedAddressWithPathIsCutAtTheSlash() {
        assertEquals("192.168.1.100" to 8787, normalizeTarget("http://192.168.1.100:8787/api/v1", 9999))
        assertEquals("pc" to 8787, normalizeTarget("pc:8787/ping", 9999))
    }

    @Test
    fun bareHostOrIpUsesTheConfiguredPort() {
        assertEquals("192.168.1.100" to 8787, normalizeTarget("192.168.1.100", 8787))
        assertEquals("pc" to 8787, normalizeTarget("pc", 8787))
    }

    @Test
    fun surroundingWhitespaceAndTrailingColonAreDropped() {
        assertEquals("pc" to 8787, normalizeTarget("   pc   ", 8787))
        assertEquals("pc" to 8787, normalizeTarget("pc:", 8787))
        assertEquals("192.168.1.5" to 8787, normalizeTarget("HTTP://192.168.1.5/", 8787))
    }

    @Test
    fun emptyInputProducesAnEmptyHostNotGarbage() {
        assertEquals("" to 8787, normalizeTarget("", 8787))
        assertEquals("" to 8787, normalizeTarget("   ", 8787))
        assertEquals("" to 8787, normalizeTarget("http://", 8787))
    }

    @Test
    fun portsOutsideTheValidRangeFallBack() {
        assertEquals("pc" to 8787, normalizeTarget("pc:0", 8787))
        assertEquals("pc" to 8787, normalizeTarget("pc:70000", 8787))
        assertEquals("pc" to 8787, normalizeTarget("pc:abc", 8787))
    }

    @Test
    fun configuredPortIsClampedToTheLegalRange() {
        assertEquals("pc" to 1, normalizeTarget("pc", 0))
        assertEquals("pc" to 65535, normalizeTarget("pc", 99999))
    }

    @Test
    fun ipv6LiteralsAreNeverSplitOnTheirColons() {
        assertEquals("2001:db8::1" to 8787, normalizeTarget("2001:db8::1", 8787))
        assertEquals("::1" to 8787, normalizeTarget("::1", 8787))
        assertEquals("::1" to 8787, normalizeTarget("[::1]", 8787))
    }

    @Test
    fun bracketedIpv6StillCarriesItsPort() {
        assertEquals("::1" to 8787, normalizeTarget("[::1]:8787", 9999))
        assertEquals("2001:db8::1" to 9999, normalizeTarget("[2001:db8::1]", 9999))
    }
}
