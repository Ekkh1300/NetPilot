package com.netpilot.mobile.ui.screens

import com.netpilot.mobile.core.log.LogEntry
import com.netpilot.mobile.core.log.LogLevel
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The decisions the Diagnostics screen makes, tested without Android or Compose.
 *
 * A screen can only be checked by looking at it, and looking is exactly what missed the desktop's
 * diagnostics page shipping with a broken resource key and thirteen untranslated strings. The
 * logic is here instead, and the screen is a thin render of it.
 */
class DiagnosticsModelTest {

    /**
     * A log source the test fully controls, so each case is about one decision and not about
     * whether a real burst happened to cross a threshold.
     *
     * The counters are settable for the same reason: provoking a genuine queue overrun stopped
     * being possible once the writer was fixed, and a test that cannot reach the condition it
     * exists to check is a test that quietly stops checking anything.
     */
    private class FakeSource(
        private val entries: List<LogEntry>,
        var written: Long = entries.size.toLong(),
        var dropped: Long = 0,
        var queued: Int = 0,
        var writeFailures: Long = 0,
    ) : DiagnosticsModel.LogSource {
        var lastMax = -1
        var lastLevel: LogLevel? = null
        var lastText: String? = null
        var calls = 0

        override fun recent(max: Int, level: LogLevel, text: String?): List<LogEntry> {
            calls++
            lastMax = max
            lastLevel = level
            lastText = text
            return entries
                .filter { it.level.order >= level.order }
                .filter { text == null || it.message.contains(text, ignoreCase = true) }
                .take(max)
        }

        override fun counters() =
            com.netpilot.mobile.core.log.NpLog.Counters(written, dropped, queued, writeFailures)
    }

    private fun entry(
        message: String,
        level: LogLevel = LogLevel.INFO,
        detail: String? = null,
    ) = LogEntry(1_700_000_000_000L, level, "test", message, detail)

    // ---------------- what gets shown ----------------

    @Test
    fun rowsAreNewestFirst() {
        val source = FakeSource(listOf(entry("third"), entry("second"), entry("first")))
        val model = DiagnosticsModel(source)

        // The order the source hands back is the order the screen shows: the log hands back
        // newest first, and reversing it here would make the most recent failure the hardest to
        // find on a phone.
        assertEquals(listOf("third", "second", "first"), model.rows.map { it.text })
    }

    @Test
    fun theLevelIsPassedThroughToTheSource() {
        val source = FakeSource(emptyList())
        val model = DiagnosticsModel(source)

        model.chooseLevel(LogLevel.WARN)

        assertEquals(LogLevel.WARN, model.level)
        assertEquals(LogLevel.WARN, source.lastLevel)
    }

    @Test
    fun aBlankFilterIsNoFilter() {
        val source = FakeSource(emptyList())
        val model = DiagnosticsModel(source)

        model.typeFilter("")
        assertNull("an empty string means no filter, not an empty result", source.lastText)

        model.typeFilter("tunnel")
        assertEquals("tunnel", source.lastText)
    }

    /**
     * Not a style question. Typing then deleting has to land back on the full list, and a
     * filter of "" that is sent through as a real search string matches nothing - the screen
     * would empty itself the moment the user cleared the box and look like a crash.
     */
    @Test
    fun clearingTheFilterRestoresEverything() {
        val source = FakeSource(listOf(entry("tunnel started"), entry("dns answered")))
        val model = DiagnosticsModel(source)

        model.typeFilter("tunnel")
        model.typeFilter("")
        model.refresh()

        assertEquals(2, model.rows.size)
    }

    @Test
    fun theRowCapIsBounded() {
        // Without a cap the screen would build a Compose node per entry in the log's buffer, and
        // the buffer is 1000 on a phone.
        val source = FakeSource((0 until 5000).map { entry("entry $it") })
        DiagnosticsModel(source).refresh()

        assertEquals(DiagnosticsModel.MAX_ROWS, source.lastMax)
        assertTrue(DiagnosticsModel.MAX_ROWS <= 1000)
    }

    // ---------------- the detail must not be flattened into the message ----------------

    /**
     * A stack trace concatenated onto the message makes both harder to read: the message stops
     * being a scannable one-liner, and the trace loses its own line breaks. This is why the row
     * has a separate field for it.
     */
    @Test
    fun aDetailStaysOnItsOwnLine() {
        val model = DiagnosticsModel(FakeSource(listOf(
            entry("protect() failed", LogLevel.WARN, "java.lang.IllegalStateException\n\tat Foo.bar")
        )))
        model.refresh()

        val row = model.rows.single()
        assertEquals("protect() failed", row.text)
        assertNotNull("the trace was lost", row.detail)
        assertTrue(row.detail!!.contains("IllegalStateException"))
        assertFalse("the trace leaked into the message", row.text.contains("IllegalStateException"))
    }

    // ---------------- the stamp ----------------

    /**
     * The trailing Z is stripped because the column is narrow, but every entry is UTC. So the
     * stamp has to keep the time, and it must not silently become local time - a log read in
     * another time zone is the case the ISO stamp exists for.
     */
    @Test
    fun theStampIsReadableUtc() {
        val model = DiagnosticsModel(FakeSource(listOf(entry("hello"))))
        model.refresh()

        val stamp = model.rows.single().stamp
        assertFalse("the Z is the only proof the time is UTC", stamp.endsWith("Z"))
        assertTrue("should still be a full date-time, got: $stamp",
            stamp.matches(Regex("""\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}""")))
    }

    // ---------------- honesty about a log with holes in it ----------------

    /**
     * The single most important thing this screen does.
     *
     * Every entry on screen is a real entry. That is what makes the omission dangerous: a log
     * missing half its lines looks exactly like a complete one, so the user concludes the app is
     * working. The dropped count has to be on the same screen, above the entries, for exactly
     * that reason.
     */
    @Test
    fun aCompleteLogClaimsNothing() {
        val model = DiagnosticsModel(FakeSource(listOf(entry("fine"))))
        model.refresh()

        assertFalse("nothing was lost, so there is nothing to say", model.hasProblems)
        assertNull(model.problemText)
    }

    @Test
    fun droppedEntriesAreAdmittedNotHidden() {
        val source = FakeSource(listOf(entry("fine")), dropped = 447)
        val model = DiagnosticsModel(source)

        assertTrue(model.hasProblems)
        val text = model.problemText
        assertNotNull("a log with a hole in it must say so", text)
        assertTrue("the count has to be in the sentence: $text", text!!.contains("447"))
    }

    @Test
    fun writeErrorsAlsoCountAsAProblem() {
        // A disk that refuses writes loses entries just as surely as an overrun, and the user
        // cannot see that from the entries alone either.
        val model = DiagnosticsModel(FakeSource(listOf(entry("fine")), writeFailures = 3))

        assertTrue(model.hasProblems)
        assertTrue(model.problemText!!.contains("3"))
    }

    /**
     * The condition is supplied rather than provoked on purpose, and that is the whole point of
     * putting the counters behind the same seam as the log.
     *
     * This test used to overrun the real queue on purpose to reach the branch. After the writer
     * was made fast enough to keep up with a 6000-entry burst, a 20,000-entry burst no longer
     * overran it - so the test stopped reaching the branch it was written for and failed, which
     * is the correct outcome. Reaching a real overload just to assert on it would also have made
     * the test depend on how fast the disk is, on the machine, that day.
     */
    @Test
    fun aProblemIsReportedEvenWhenEveryVisibleEntryIsFine() {
        val model = DiagnosticsModel(FakeSource(listOf(entry("all good")), dropped = 1))

        assertEquals("every row on screen is a real entry", "all good", model.rows.single().text)
        assertTrue("...and the log is still incomplete", model.hasProblems)
    }

    @Test
    fun theCountersAreARealSnapshot() {
        val model = DiagnosticsModel(FakeSource(listOf(entry("a"))))
        model.refresh()

        val c = model.counters
        assertTrue("something was written, so written must be positive", c.written > 0)
        assertTrue("queued is never negative", c.queued >= 0)
        assertTrue("write failures are never negative", c.writeFailures >= 0)
    }

    // ---------------- the report ----------------

    /**
     * The report is the only thing the user can actually get off the phone: the log file is in
     * private storage, which no file manager can reach. So the report has to carry what a support
     * reply will ask for, and it must not carry the pairing token.
     */
    @Test
    fun theReportIsSelfContainedAndClean() {
        val report = DiagnosticsModel(FakeSource(emptyList())).reportText()

        assertTrue(report.contains("NetPilot Android diagnostics"))
        assertTrue("a support reply asks for the version", report.contains("android"))
        assertTrue(report.contains("dropped"))
        assertTrue(report.contains("write errs"))
        assertFalse(report.contains("api_key"))
        assertFalse(report.contains("Bearer "))
    }

    // ---------------- idempotence ----------------

    /**
     * Tapping the same chip again must not re-read the log. The screen polls every two seconds,
     * so a redundant read here is a redundant read the user can trigger by tapping a level
     * chip, and the source is not free.
     */
    @Test
    fun choosingTheSameLevelTwiceReadsNothingExtra() {
        val source = FakeSource(listOf(entry("a")))
        val model = DiagnosticsModel(source)

        model.chooseLevel(LogLevel.WARN)
        val after = source.calls
        model.chooseLevel(LogLevel.WARN)
        assertEquals("a no-op change should not re-read", after, source.calls)
    }

    @Test
    fun typingTheSameFilterTwiceReadsNothingExtra() {
        val source = FakeSource(listOf(entry("a")))
        val model = DiagnosticsModel(source)

        model.typeFilter("x")
        val after = source.calls
        model.typeFilter("x")
        assertEquals("a no-op change should not re-read", after, source.calls)
    }
}
