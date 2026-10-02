package com.netpilot.mobile.net

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The health score, which is only as good as the numbers it is willing to show.
 *
 * It used to read ~50% on an ordinary connection, for two avoidable reasons: a component
 * nobody had measured yet was scored 60 instead of being left out, and the curves put
 * "excellent" out of reach for values that are completely normal (a 150 ms DNS lookup, a
 * 90 ms ping behind a VPN). These tests pin the behaviour that was asked for — strict
 * where the network is genuinely bad, fair where it is merely ordinary — plus the
 * properties that keep it that way: monotone in latency and loss, no confident number
 * before there is enough evidence for one.
 */
class HealthTest {

    // ------------------------------------------------------------ what a user expects

    @Test
    fun healthyHomeWifiReadsExcellent() {
        val s = scoreHealth(true, dnsMs = 60, avgMs = 30, lossPct = 0.0, recentLinkChanges = 0)
        assertTrue("healthy Wi-Fi should be excellent, was ${s.score}", s.score >= 90)
    }

    @Test
    fun ordinaryMobileOrVpnConnectionReadsAtLeastGood() {
        val s = scoreHealth(true, dnsMs = 150, avgMs = 95, lossPct = 0.0, recentLinkChanges = 0)
        assertTrue("95 ms behind a VPN is ordinary, was ${s.score}", s.score >= 80)
    }

    @Test
    fun theMeasurementShapeThatUsedToReadFiftyIsNowJudgedFairly() {
        // The old curves turned exactly this (180 ms DNS, 120 ms ping, 4% loss) into ~56.
        val s = scoreHealth(true, dnsMs = 180, avgMs = 120, lossPct = 4.0, recentLinkChanges = 0)
        assertTrue("was ${s.score}", s.score >= 75)
    }

    @Test
    fun aGenuinelyBadConnectionStillReadsPoor() {
        val s = scoreHealth(true, dnsMs = 900, avgMs = 500, lossPct = 25.0, recentLinkChanges = 0)
        assertTrue("a broken network must not read healthy, was ${s.score}", s.score <= 40)
    }

    @Test
    fun noLinkAtAllIsZero() {
        val s = scoreHealth(false, dnsMs = 60, avgMs = 30, lossPct = 0.0, recentLinkChanges = 0)
        assertEquals(0, s.score)
    }

    // ------------------------------------------------------- not enough evidence yet

    @Test
    fun coldStartIsCappedInsteadOfClaimingAHundred() {
        // Nothing measured except "the link has not flapped": one component, all perfect.
        val s = scoreHealth(true, dnsMs = -1, avgMs = -1, lossPct = Double.NaN, recentLinkChanges = 0)
        assertEquals(1, s.known)
        assertEquals(PARTIAL_CAP, s.score)
    }

    @Test
    fun twoKnownComponentsStayCappedEvenWhenTheyArePerfect() {
        // Resolver answers, link is steady, but no probe window exists yet (ICMP/TCP to
        // the targets is filtered): honest "not enough to judge", not 100%.
        val s = scoreHealth(true, dnsMs = 40, avgMs = -1, lossPct = Double.NaN, recentLinkChanges = 0)
        assertEquals(2, s.known)
        assertEquals(PARTIAL_CAP, s.score)
    }

    @Test
    fun threeKnownComponentsAreEnoughForAConfidentNumber() {
        val s = scoreHealth(true, dnsMs = 60, avgMs = 30, lossPct = Double.NaN, recentLinkChanges = 0)
        assertEquals(3, s.known)
        assertTrue("must leave the capped range, was ${s.score}", s.score > PARTIAL_CAP)
    }

    @Test
    fun noProbeWindowIsNotZeroPercentLoss() {
        assertEquals(NO_SAMPLE, rateLoss(Double.NaN))
        assertEquals(NO_SAMPLE, rateDns(-1))
        assertEquals(NO_SAMPLE, rateLatency(-1))
    }

    @Test
    fun unknownComponentsAreExcludedRatherThanPenalised() {
        val unknown = scoreHealth(true, dnsMs = -1, avgMs = 30, lossPct = 0.0, recentLinkChanges = 0)
        val measuredGood = scoreHealth(true, dnsMs = 40, avgMs = 30, lossPct = 0.0, recentLinkChanges = 0)
        val measuredPoor = scoreHealth(true, dnsMs = 400, avgMs = 30, lossPct = 0.0, recentLinkChanges = 0)

        assertTrue("not knowing DNS must not cost more than a measured 400 ms lookup " +
            "(${unknown.score} vs ${measuredPoor.score})", unknown.score >= measuredPoor.score)
        assertTrue("excluding a component must not inflate it either " +
            "(${unknown.score} vs ${measuredGood.score})", unknown.score <= measuredGood.score)
    }

    // ------------------------------------------------------------- stability, kindly

    @Test
    fun oneFlapInAMinuteCostsAlmostNothing() {
        val steady = scoreHealth(true, 60, 30, 0.0, 0)
        val flapped = scoreHealth(true, 60, 30, 0.0, 1)
        assertTrue("a single blip used to cost ~8 points, now ${steady.score - flapped.score}",
            steady.score - flapped.score <= 8)
        assertTrue(flapped.score >= 90)
    }

    @Test
    fun churnKeepsReducingTheScore() {
        val scores = (0..6).map { scoreHealth(true, 60, 30, 0.0, it).score }
        assertEquals("must never improve with more flaps", scores, scores.sortedDescending())
        assertTrue("six flaps in a minute must be visible, was ${scores.last()}", scores.last() < scores.first())
    }

    // ------------------------------------------------------------- monotone in damage

    @Test
    fun worseLatencyNeverScoresHigher() {
        val latencies = listOf(10, 30, 60, 100, 150, 250, 400, 700)
        val scores = latencies.map { scoreHealth(true, 60, it, 0.0, 0).score }
        assertEquals(scores, scores.sortedDescending())
    }

    @Test
    fun worseLossNeverScoresHigher() {
        val losses = listOf(0.0, 0.5, 1.0, 3.0, 6.0, 12.0, 25.0, 60.0)
        val scores = losses.map { scoreHealth(true, 60, 30, it, 0).score }
        assertEquals(scores, scores.sortedDescending())
    }

    @Test
    fun oneFailedProbeInTwentyIsABlipNotADisaster() {
        // PING_WINDOW is 20, so a single timeout is 5% loss.
        val s = scoreHealth(true, dnsMs = 60, avgMs = 30, lossPct = 5.0, recentLinkChanges = 0)
        assertTrue("one hiccup in twenty probes should stay green, was ${s.score}", s.score >= 85)
    }
}
