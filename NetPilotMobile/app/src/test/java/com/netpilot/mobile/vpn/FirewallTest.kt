package com.netpilot.mobile.vpn

import com.netpilot.mobile.data.LimitRule
import com.netpilot.mobile.data.RuleMode
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

/**
 * Rule enforcement and the attribution circuit breaker.
 *
 * The two platform lookups are injected, so "the OS told us the package" and "the OS
 * refused" are both reproducible here instead of needing a handset.
 */
class FirewallTest {

    @Before
    fun clearCounters() {
        FirewallStats.reset()
    }

    private fun rule(pkg: String, mode: RuleMode, down: Long = 0, up: Long = 0) =
        LimitRule(pkg = pkg, label = pkg, mode = mode, downBps = down, upBps = up)

    private fun firewall(
        rules: List<LimitRule>,
        uid: Int = 4242,
        pkg: String? = "com.example.app"
    ) = Firewall(
        ruleSource = { rules.associateBy { it.pkg } },
        ownerUid = { _, _, _, _, _ -> uid },
        pkgOf = { pkg }
    )

    // ------------------------------------------------------------------ rules

    @Test
    fun blockRuleBlocksThatPackageOnly() {
        val blocked = firewall(listOf(rule("com.example.app", RuleMode.BLOCK)))
        val outcome = blocked.decide(Packet.TCP, 1, 40000, 2, 443)

        assertTrue(outcome.decision is Decision.Block)
        assertTrue("the platform answered", outcome.identified)

        // A different application is untouched — a block rule is not an outage.
        val other = firewall(
            listOf(rule("com.example.app", RuleMode.BLOCK)),
            pkg = "com.other.app"
        )
        val pass = other.decide(Packet.TCP, 1, 40000, 2, 443)
        assertTrue(pass.decision is Decision.Pass)
    }

    @Test
    fun noRulesMeansEverythingPasses() {
        val fw = firewall(emptyList())
        assertTrue(fw.decide(Packet.TCP, 1, 1, 2, 80).decision is Decision.Pass)
        assertTrue(fw.decide(Packet.UDP, 1, 1, 2, 53).decision is Decision.Pass)
        assertTrue(fw.decide(Packet.ICMP, 1, 1, 2, 0).decision is Decision.Pass)
    }

    @Test
    fun limitRuleShapesOnlyTheCappedDirection() {
        val fw = firewall(listOf(rule("com.example.app", RuleMode.LIMIT, down = 4096, up = 1024)))

        val outcome = fw.decide(Packet.TCP, 1, 40000, 2, 443)
        assertTrue(outcome.decision is Decision.Shape)
        assertEquals("com.example.app", (outcome.decision as Decision.Shape).pkg)

        // Capped directions get a real, bounded byte budget (the bucket floor is 4 KiB,
        // so the first second may spend that much regardless of the rate).
        val down = fw.available("com.example.app", up = false)
        val up = fw.available("com.example.app", up = true)
        assertTrue("down direction is metered", down in 1 until Long.MAX_VALUE)
        assertTrue("up direction is metered", up in 1 until Long.MAX_VALUE)
        assertTrue("burst is bounded", down <= 4096L)

        // ...and a package with no rule is never metered.
        assertEquals(Long.MAX_VALUE, fw.available("com.other.app", up = false))
        assertTrue(fw.spend("com.other.app", up = true, 10_000_000L))
    }

    @Test
    fun aLimitWithBothDirectionsAtZeroIsNotASilentBlackhole() {
        // A rule saved with 0/0 must not pass through the shape path and starve the app.
        val fw = firewall(listOf(rule("com.example.app", RuleMode.LIMIT, down = 0, up = 0)))
        assertTrue(fw.decide(Packet.TCP, 1, 1, 2, 80).decision is Decision.Pass)
        assertEquals(Long.MAX_VALUE, fw.available("com.example.app", up = false))
    }

    // ------------------------------------------------------------------ attribution

    @Test
    fun unidentifiedFlowsFailClosedWhileABlockRuleExists() {
        val fw = firewall(listOf(rule("com.example.app", RuleMode.BLOCK)), uid = -1)
        val outcome = fw.decide(Packet.TCP, 1, 40000, 2, 443)

        assertTrue("refuse rather than leak", outcome.decision is Decision.Block)
        assertFalse("the OS never told us the owner", outcome.identified)
        assertFalse("the breaker has not fired yet", FirewallStats.attributionBroken)
    }

    @Test
    fun unidentifiedFlowsPassWhenThereIsNothingToProtect() {
        // No block rules: an unattributable flow is forwarded, not black-holed.
        val fw = firewall(listOf(rule("com.example.app", RuleMode.LIMIT, down = 1024)), uid = -1)
        assertTrue(fw.decide(Packet.TCP, 1, 1, 2, 80).decision is Decision.Pass)
        assertFalse(FirewallStats.attributionBroken)
    }

    @Test
    fun breakerStopsFailingClosedAfterRepeatedMisses() {
        val fw = firewall(listOf(rule("com.example.app", RuleMode.BLOCK)), uid = -1)

        // Eleven misses in a row (the breaker fires on the twelfth): still refusing.
        var last = fw.decide(Packet.TCP, 1, 1, 2, 80)          // miss 1
        for (miss in 2..Firewall.BREAKER_AFTER - 1) {
            last = fw.decide(Packet.TCP, 1, 1, 2, 80)          // miss 2..11
        }
        assertTrue("still fail-closed before the threshold", last.decision is Decision.Block)
        assertFalse(FirewallStats.attributionBroken)

        // The next miss crosses the threshold: the engine stops black-holing a device it
        // cannot attribute instead of leaving the user with no connectivity at all.
        last = fw.decide(Packet.TCP, 1, 1, 2, 80)              // miss 12
        assertTrue(last.decision is Decision.Pass)
        assertTrue("the UI must be able to say so", FirewallStats.attributionBroken)
    }

    @Test
    fun oneSuccessfulIdentificationKeepsTheBreakerArmed() {
        // A build that answers *sometimes* must not trip the breaker on a lucky streak of
        // misses right after a hit.
        var identified = true
        val fw = Firewall(
            ruleSource = { mapOf("com.example.app" to rule("com.example.app", RuleMode.BLOCK)) },
            ownerUid = { _, _, _, _, _ -> if (identified) 4242 else Firewall.INVALID_UID },
            pkgOf = { "com.example.app" }
        )

        assertTrue(fw.decide(Packet.TCP, 1, 1, 2, 80).decision is Decision.Block)
        identified = false
        repeat(Firewall.BREAKER_AFTER * 2) {
            val out = fw.decide(Packet.TCP, 1, 1, 2, 80)
            assertTrue("a single hit before the misses keeps failing closed",
                out.decision is Decision.Block)
        }
        assertFalse(FirewallStats.attributionBroken)
    }

    @Test
    fun platformWithoutAPackageStillPassesThrough() {
        // The daemon runs under a shared UID with no package name to match a rule against.
        val fw = firewall(listOf(rule("com.example.app", RuleMode.BLOCK)), pkg = null)
        val outcome = fw.decide(Packet.TCP, 1, 1, 2, 80)
        assertTrue(outcome.decision is Decision.Pass)
        assertTrue("the platform *did* answer — this is not a miss", outcome.identified)
        assertEquals(null, (outcome.decision as Decision.Pass).pkg)
    }

    // ------------------------------------------------------------------ live rule edits

    @Test
    fun refreshPicksUpAnEditedRuleImmediately() {
        var rules = listOf(rule("com.example.app", RuleMode.ALLOW))
        val fw = Firewall(
            ruleSource = { rules.associateBy { it.pkg } },
            ownerUid = { _, _, _, _, _ -> 4242 },
            pkgOf = { "com.example.app" }
        )
        assertTrue(fw.decide(Packet.TCP, 1, 1, 2, 80).decision is Decision.Pass)

        rules = listOf(rule("com.example.app", RuleMode.BLOCK))
        fw.refresh()

        // The cached decision is the relay's problem; the policy itself must flip.
        assertTrue(fw.decidePkg("com.example.app") is Decision.Block)
    }

    @Test
    fun refreshRebuildsBucketsWhenTheRateChanges() {
        // The bucket remembers the rate it was born with. Editing 512 KB/s down to 128 KB/s
        // has to drop it, otherwise the tunnel keeps enforcing the old speed (the limiter
        // screen showed the new number while the relay still paced at the old one).
        var rules = listOf(rule("com.example.app", RuleMode.LIMIT, down = 40_000))
        val fw = Firewall(
            ruleSource = { rules.associateBy { it.pkg } },
            ownerUid = { _, _, _, _, _ -> 4242 },
            pkgOf = { "com.example.app" }
        )

        // A fresh bucket's ceiling is burst = rate/4 (floored at 4 KB).
        val bornBurst = fw.available("com.example.app", up = false)
        assertEquals(40_000L / 4, bornBurst)

        rules = listOf(rule("com.example.app", RuleMode.LIMIT, down = 20_000))
        fw.refresh()

        val newBurst = fw.available("com.example.app", up = false)
        assertEquals("old rate still enforced", 20_000L / 4, newBurst)
        assertTrue(newBurst < bornBurst)
    }

    @Test
    fun removedRulesStopBlocking() {
        val fw = firewall(listOf(rule("com.example.app", RuleMode.BLOCK)))
        assertTrue(fw.decidePkg("com.example.app") is Decision.Block)

        val empty = Firewall(
            ruleSource = { emptyMap() },
            ownerUid = { _, _, _, _, _ -> 4242 },
            pkgOf = { "com.example.app" }
        )
        assertTrue(empty.decidePkg("com.example.app") is Decision.Pass)
    }

    // ------------------------------------------------------------------ token bucket

    @Test
    fun tokenBucketSpendsBurstThenRefillsAtTheConfiguredRate() {
        val bucket = TokenBucket(ratePerSec = 1000, burstPerSec = 4096)
        val t0 = 1_000_000_000L

        assertEquals(4096L, bucket.available(t0))
        assertTrue(bucket.spend(4096, t0))
        assertFalse("burst is spent", bucket.spend(1, t0))
        assertEquals(0L, bucket.available(t0))

        // Half a second at 1000 B/s.
        assertEquals(500L, bucket.available(t0 + 500_000_000L))
        // An hour of idling still cannot exceed the burst — no long-download free pass.
        assertEquals(4096L, bucket.available(t0 + 3_600_000_000_000L))
    }

    @Test
    fun tokenBucketRefusesWhatItCannotCover() {
        val bucket = TokenBucket(ratePerSec = 100, burstPerSec = 4096)
        val t0 = 2_000_000_000L
        assertTrue(bucket.spend(4096, t0))
        assertFalse(bucket.spend(2000, t0 + 1_000_000_000L))
        assertTrue(bucket.spend(99, t0 + 1_000_000_000L))
    }

    @Test
    fun unconfiguredDirectionIsNeverCapped() {
        val fw = firewall(listOf(rule("com.example.app", RuleMode.LIMIT, down = 0, up = 512)))
        assertEquals(Long.MAX_VALUE, fw.available("com.example.app", up = false))
        assertTrue(fw.spend("com.example.app", up = false, 50_000_000L))
    }

    // ------------------------------------------------------------------ accounting

    /**
     * The bug this guards: the relay asked the bucket how much it *could* send and then sent
     * that much, without ever paying for it. The bucket refilled on every call, so each call
     * saw a full burst and a 16 KB/s rule actually moved ~800 KB/s (burst x calls/second).
     */
    @Test
    fun reservePaysForTheBytesSoAThousandCallsCannotOutrunTheRate() {
        val bucket = TokenBucket(ratePerSec = 16_384, burstPerSec = 4_096)
        val t0 = 5_000_000_000L

        var granted = 0L
        repeat(1000) { granted += bucket.reserve(t0, 4_096) }

        assertEquals("a thousand peeks must not become a thousand bursts", 4_096L, granted)
    }

    @Test
    fun pacedTrafficOverTimeTotalsTheConfiguredRate() {
        val rate = 16_384L
        val bucket = TokenBucket(ratePerSec = rate, burstPerSec = 4_096)
        val t0 = 7_000_000_000L
        val step = 10_000_000L            // 10 ms
        val seconds = 10

        var sent = 0L
        for (i in 0 until seconds * 100) {
            val now = t0 + i * step
            var want = 1_460L              // a full TCP segment, as the relay asks for
            while (want > 0) {
                val got = bucket.reserve(now, want)
                if (got == 0L) break
                sent += got
                want -= got
            }
        }

        val expected = rate * seconds
        assertTrue("paced total $sent should be near $expected", sent in (expected * 0.9).toLong()..(expected * 1.15).toLong())
    }

    @Test
    fun refundHandsUnsentBytesBackToTheBudget() {
        val bucket = TokenBucket(ratePerSec = 1_000, burstPerSec = 4_096)
        val t0 = 9_000_000_000L

        assertEquals(4_096L, bucket.reserve(t0, 4_096))
        assertEquals(0L, bucket.reserve(t0, 1))
        bucket.refund(t0, 1_000)
        assertEquals(1_000L, bucket.reserve(t0, 1_000))
    }

    @Test
    fun reserveIsTransparentForUncappedTraffic() {
        val fw = firewall(listOf(rule("com.example.app", RuleMode.LIMIT, down = 16_384, up = 0)))
        // No package (nothing to attribute) and a direction with no cap: full speed.
        assertEquals(64_000L, fw.reserve(null, up = false, 64_000L))
        assertEquals(64_000L, fw.reserve("com.example.app", up = true, 64_000L))
        fw.refund("com.example.app", up = true, 10)
        // The capped direction is not.
        assertEquals(0L, fw.reserve("com.example.app", up = false, 9_000_000L))
    }
}
