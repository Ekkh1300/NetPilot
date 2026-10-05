package com.netpilot.mobile.core.log

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import java.io.File

/**
 * The logger's two guarantees that matter when something has already gone wrong: it never
 * throws, and it never writes a secret.
 *
 * These are the same guarantees NetPilot.Core.Logging makes on the desktop, tested for the same
 * reasons. A logger that throws takes the app down, and the moment you need the log is exactly
 * the moment the disk is full. A logger that leaks the pairing token hands the PC's network to
 * whoever can read the thread it was pasted into.
 */
class NpLogTest {

    private lateinit var dir: File

    @Before
    fun setUp() {
        NpLog.shutdown(1000)
        // The buffer is static and shared across the whole JVM, so anything that counts
        // entries has to measure the change over its own window. An absolute count would be
        // asserting something about test ordering, and would fail depending on what ran first.'
        dir = File(System.getProperty("java.io.tmpdir"), "np-log-${System.nanoTime()}")
        dir.mkdirs()
        NpLog.configure(dir, LogLevel.TRACE)
    }

    private fun logFile() = File(dir, "netpilot.log")

    private fun waitForFile(marker: String, timeoutMs: Long = 6000) {
        val deadline = System.currentTimeMillis() + timeoutMs
        while (System.currentTimeMillis() < deadline) {
            try {
                if (logFile().exists() && logFile().readText().contains(marker)) return
            } catch (_: Throwable) {}
            Thread.sleep(25)
        }
        throw AssertionError(
            "the log file never contained: $marker\nwritten=${NpLog.writtenCount} " +
                "dropped=${NpLog.droppedCount} errors=${NpLog.writeFailureCount}"
        )
    }

    private fun text() = logFile().readText()

    // ---------------- it never throws ----------------

    @Test
    fun writingWithoutConfigurationDoesNotThrow() {
        NpLog.shutdown(500)
        // A call from a unit test or a tool, before anything configured the logger.
        NpLog.info("test", "no configuration yet")
        NpLog.error("test", "and an error too", IllegalStateException("boom"))
    }

    @Test
    fun anUnwritableDirectoryDegradesInsteadOfThrowing() {
        // A path that cannot be created: logging must not be the thing that breaks the app.
        NpLog.configure(File("/proc/definitely/not/writable/at/all"), LogLevel.TRACE)
        NpLog.info("test", "still fine")
        NpLog.error("test", "and errors too", RuntimeException("nope"))
    }

    @Test
    fun concurrentWritersAllLand() {
        val threads = 4
        val each = 150
        val before = NpLog.writtenCount
        val ts = (0 until threads).map { t ->
            Thread {
                repeat(each) { i -> NpLog.info("test", "thread $t entry $i") }
            }.apply { start() }
        }
        ts.forEach { it.join() }
        waitForFile("entry 149", 15000)
        Thread.sleep(600)
        assertEquals("nothing should have been dropped", 0, NpLog.droppedCount)
        assertEquals((threads * each).toLong(), NpLog.writtenCount - before)
    }

    @Test
    fun entriesReachTheFile() {
        NpLog.info("vpn", "a distinctive marker line")
        waitForFile("a distinctive marker line")
        assertTrue(text().contains("a distinctive marker line"))
    }

    // ---------------- levels ----------------

    @Test
    fun theThresholdIsRespected() {
        NpLog.configure(dir, LogLevel.WARN)
        NpLog.debug("test", "this should not appear")
        NpLog.info("test", "nor this")
        NpLog.warn("test", "but this should")
        waitForFile("but this should")
        val t = text()
        assertFalse(t.contains("this should not appear"))
        assertFalse(t.contains("nor this"))
        assertTrue(t.contains("but this should"))
    }

    @Test
    fun oneCategoryCanBeTracedWhileTheRestStaysQuiet() {
        NpLog.configure(dir, LogLevel.INFO)
        NpLog.setCategoryLevel("tunnel", LogLevel.TRACE)
        NpLog.trace("tunnel", "chatter from the tunnel")
        NpLog.trace("ui", "chatter from the ui")
        NpLog.info("tunnel", "something worth knowing")
        waitForFile("chatter from the tunnel")
        val t = text()
        assertTrue(t.contains("chatter from the tunnel"))
        assertFalse(t.contains("chatter from the ui"))
    }

    @Test
    fun aCategoryOverrideReplacesTheGlobalThresholdInBothDirections() {
        NpLog.configure(dir, LogLevel.INFO)
        NpLog.setCategoryLevel("tunnel", LogLevel.TRACE)
        assertTrue(NpLog.isEnabled(LogLevel.TRACE, "tunnel"))
        assertFalse(NpLog.isEnabled(LogLevel.TRACE, "ui"))

        NpLog.configure(dir, LogLevel.TRACE)
        NpLog.setCategoryLevel("noisy", LogLevel.ERROR)
        assertTrue(NpLog.isEnabled(LogLevel.TRACE, "other"))
        assertFalse(NpLog.isEnabled(LogLevel.INFO, "noisy"))
        assertTrue(NpLog.isEnabled(LogLevel.ERROR, "noisy"))
    }

    @Test
    fun levelsCompareInTheRightOrder() {
        assertEquals("TRC", LogLevel.TRACE.tag)
        assertEquals("DBG", LogLevel.DEBUG.tag)
        assertEquals("INF", LogLevel.INFO.tag)
        assertEquals("WRN", LogLevel.WARN.tag)
        assertEquals("ERR", LogLevel.ERROR.tag)
        assertEquals("FTL", LogLevel.FATAL.tag)
        assertTrue(LogLevel.INFO.order < LogLevel.ERROR.order)
    }

    // ---------------- redaction: the part that must not be wrong ----------------

    @Test
    fun aBearerTokenNeverReachesTheFile() {
        NpLog.info("bridge", "auth failed for Authorization: Bearer abc123def456ghi789jkl")
        waitForFile(Redactor.MASK)
        assertFalse(text().contains("abc123def456ghi789jkl"))
    }

    @Test
    fun aTokenInJsonIsRemovedWhateverTheKeyCasing() {
        for (key in listOf("token", "Token", "pairingCode", "pairing_code", "secret", "password", "apiKey")) {
            val out = Redactor.apply("""{"$key":"hunter2value"}""")
            assertFalse("key $key leaked", out.contains("hunter2value"))
        }
    }

    @Test
    fun aBareSixDigitPairingCodeIsRemoved() {
        val out = Redactor.apply("pairing code 482913 accepted")
        assertFalse(out.contains("482913"))
        assertTrue(out.contains(Redactor.MASK))
    }

    @Test
    fun aMacAddressIsRemoved() {
        val out = Redactor.apply("adapter AA:BB:CC:DD:EE:FF came up")
        assertFalse(out.contains("AA:BB:CC:DD:EE:FF"))
    }

    /** The other direction, and the one that matters more: over-redaction destroys the file's
     *  only reason to exist. Addresses and byte counts must survive. */
    @Test
    fun diagnosticDetailIsNotDestroyed() {
        val line = "link wlan0 up 192.168.1.100 gw 192.168.1.1 rx 123456789 tx 987654321 rate 4096"
        assertEquals(line, Redactor.apply(line))
        assertTrue(Redactor.apply("ip 192.168.1.100").contains("192.168.1.100"))
        assertTrue(Redactor.apply("rx 1234567890").contains("1234567890"))
        assertTrue(Redactor.apply("dns 178.22.122.100").contains("178.22.122.100"))
    }

    @Test
    fun anExceptionChainIsRedactedThroughout() {
        val inner = IllegalArgumentException("inner token: \"abcdef123456\"")
        val outer = IllegalStateException("outer Bearer abc123def456", inner)
        val out = Redactor.apply(outer)
        assertFalse(out.contains("abc123def456"))
        assertFalse(out.contains("abcdef123456"))
        // The shape survives, which is the point: a redacted stack trace is still useful.
        assertTrue(out.contains("IllegalStateException"))
        assertTrue(out.contains("IllegalArgumentException"))
    }

    @Test
    fun redactingSomethingWithNothingToHideChangesNothing() {
        val line = "the tunnel started on wlan0"
        assertEquals(line, Redactor.apply(line))
        assertEquals(0, Redactor.countRedactions(line))
        assertTrue(Redactor.countRedactions("code 123456") > 0)
    }

    // ---------------- rotation ----------------

    @Test
    fun theFileIsCappedAndOldOnesAreDropped() {
        // Enough entries, with a small cap, to force several rotations without writing
        // megabytes: this is about the bound holding, not about throughput.
        val droppedBefore = NpLog.droppedCount
        repeat(6000) { NpLog.info("test", "line $it padded out so the file actually grows") }
        waitForFile("line 5999", 60000)
        Thread.sleep(1500)
        // A burst may legitimately overflow a bounded queue, but it must be counted and it
        // must be small. Losing half of it would mean the sink, not the producer, is the
        // bottleneck - which is a bug in the writer, not an acceptable cost.
        val lost = NpLog.droppedCount - droppedBefore
        assertTrue("lost $lost of 6000 entries, so the sink is too slow", lost < 60)

        val files = dir.listFiles()?.filter { it.name.startsWith("netpilot") } ?: emptyList()
        assertTrue("expected at most 4 files, found ${files.size}", files.size <= 4)
        val total = files.sumOf { it.length() }
        // Every file is capped and there are at most 4, so the total is bounded. This is the
        // property that stops a phone left plugged in for a week from filling its storage.
        assertTrue("log directory holds $total bytes, which is not bounded", total < 64L * 1024 * 1024)
    }

    // ---------------- the diagnostics screen's view ----------------

    @Test
    fun theInMemoryBufferIsNewestFirstAndFilterable() {
        NpLog.info("alpha", "first")
        NpLog.warn("beta", "second")
        NpLog.error("alpha", "third")
        waitForFile("third")

        val recent = NpLog.recent(100)
        assertEquals("third", recent[0].message)
        assertEquals("second", recent[1].message)

        val alpha = NpLog.recent(1000, LogLevel.TRACE, "alpha").map { it.message }
        assertEquals(listOf("third", "first"), alpha)
        assertEquals(1, NpLog.recent(1000, LogLevel.TRACE, null, "second").size)
        // A floor of Info hides the trace entries; the warn and above survive.
        assertTrue(NpLog.recent(1000, LogLevel.WARN).none { it.message == "first" })
    }

    @Test
    fun theBufferIsBounded() {
        // Deliberately under the queue's capacity, so this measures the buffer's bound rather
        // than the overload behaviour - which is a separate test below. Writing 5000 here and
        // asserting on the last entry asked the logger to do something it deliberately does
        // not do, and the test failed for that reason rather than for a fault.
        repeat(1500) { NpLog.info("test", "flood $it") }
        waitForFile("flood 1499", 30000)
        Thread.sleep(600)
        // Bounded: without the cap this is 1500 live objects for the life of the process.
        assertTrue("buffer held ${NpLog.recent(100000).size}", NpLog.recent(100000).size <= 1000)
    }

    /**
     * A burst larger than the queue loses entries, and says so.
     *
     * Dropping is the design: blocking the caller until the disk catches up would freeze the UI,
     * which the user feels. The part that must not be optional is the count - a log with a hole
     * in it that looks complete is worse than one that admits it, and this is the test that
     * keeps the count honest.
     */
    @Test
    fun aBurstLargerThanTheQueueDropsAndCounts() {
        NpLog.configure(dir, LogLevel.TRACE)
        val droppedBefore = NpLog.droppedCount
        val writtenBefore = NpLog.writtenCount

        repeat(6000) { NpLog.info("test", "burst $it padded so the queue really is overrun") }
        Thread.sleep(2500)

        val dropped = NpLog.droppedCount - droppedBefore
        val written = NpLog.writtenCount - writtenBefore
        assertTrue("nothing was written at all", written > 1000)
        assertEquals("every written entry must be counted, and every dropped one too",
            6000L, written + dropped)
    }

    /** The sink has to be fast enough that ordinary logging never drops. 6000 entries with
     *  nothing dropped is the bar; the previous version lost half of them, which is why the
     *  file size is tracked in memory instead of asked for, and why the flush is not per line. */
    @Test
    fun theSinkKeepsUpWithARealBurst() {
        NpLog.configure(dir, LogLevel.TRACE)
        val droppedBefore = NpLog.droppedCount
        repeat(6000) { NpLog.info("test", "line $it padded out so the file actually grows") }
        waitForFile("line 5999", 60000)
        Thread.sleep(1500)

        val lost = NpLog.droppedCount - droppedBefore
        assertTrue("lost $lost of 6000 entries, so the sink is too slow", lost < 60)
    }

    @Test
    fun theDiagnosticsReportCarriesTheCountersAndNoSecrets() {
        NpLog.info("bridge", "pairing code 918273 issued")
        NpLog.error("bridge", "auth failed Bearer abc123def456ghi789")
        waitForFile("pairing code")

        val report = NpLog.diagnosticsReport()
        assertTrue(report.contains("NetPilot Android diagnostics"))
        assertTrue(report.contains("dropped"))
        assertTrue(report.contains("write errs"))
        assertFalse(report.contains("918273"))
        assertFalse(report.contains("abc123def456ghi789"))
    }

    // ---------------- formatting ----------------

    @Test
    fun timestampsAreUtcAndIso() {
        val stamp = LogEntry.stamp(0L)
        assertTrue("stamp should end in Z: $stamp", stamp.endsWith("Z"))
        assertTrue(stamp.matches(Regex("""^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}Z$""")))
    }

    @Test
    fun aMultiLineMessageStaysOnOneLine() {
        // A stack trace embedded in a message would otherwise break "one line per entry",
        // which is the whole reason the file is greppable.
        val e = LogEntry(0L, LogLevel.INFO, "cat", "first\nsecond\rthird")
        assertFalse(e.toLine().contains("\n"))
    }
}