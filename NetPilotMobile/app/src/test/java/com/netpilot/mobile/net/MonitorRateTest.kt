package com.netpilot.mobile.net

import com.netpilot.mobile.data.formatRate
import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * The throughput loop used to divide the byte delta by a *millisecond* count, so every
 * reading was 1000x too small: a real 7 KB/s transfer rendered as "7 B/s" and the
 * dashboard sat on "0 B/s" while the device was actively transferring.
 */
class MonitorRateTest {

    @Test
    fun `rate is bytes per second not per millisecond`() {
        assertEquals(7_000L, Monitor.perSecond(7_000, 1_000))
        assertEquals(1_500_000L, Monitor.perSecond(1_500_000, 1_000))
    }

    @Test
    fun `rate scales with a window other than one second`() {
        assertEquals(2_500L, Monitor.perSecond(5_000, 2_000))
        assertEquals(1_000L, Monitor.perSecond(10, 10))
    }

    @Test
    fun `idle and impossible windows report zero`() {
        assertEquals(0L, Monitor.perSecond(0, 1_000))
        assertEquals(0L, Monitor.perSecond(-5, 1_000))
        assertEquals(0L, Monitor.perSecond(100, 0))
        assertEquals(0L, Monitor.perSecond(100, -1))
    }

    @Test
    fun `a real transfer is formatted as traffic, not as idle`() {
        // 5 packets of 1400 bytes in the sampling window -> ~7 KB/s.
        val rate = Monitor.perSecond(5 * 1_400, 1_000)
        assertEquals("6.8 KB/s", formatRate(rate))
    }

    @Test
    fun `rate formatting uses the documented units`() {
        assertEquals("0 B/s", formatRate(0))
        assertEquals("500 B/s", formatRate(500))
        assertEquals("1.4 MB/s", formatRate(1_500_000))
        assertEquals("2.0 KB/s", formatRate(2_048))
    }
}
