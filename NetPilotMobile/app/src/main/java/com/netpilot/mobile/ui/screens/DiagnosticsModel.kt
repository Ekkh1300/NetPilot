package com.netpilot.mobile.ui.screens

import com.netpilot.mobile.core.log.LogEntry
import com.netpilot.mobile.core.log.LogLevel
import com.netpilot.mobile.core.log.NpLog

/**
 * Everything the Diagnostics screen decides, with no Compose and no Android in it.
 *
 * Kept deliberately plain. An earlier version of this held its state as
 * `by mutableStateOf`, which is the natural thing to write and it made the model untestable:
 * Compose's state delegate calls `State.getValue()`, which needs an initialised composition, so
 * every test threw a NullPointerException before reaching a single assertion. Extracting the
 * logic in order to test it, and then putting a framework type back into it, is worse than not
 * having extracted anything - the tests looked like they covered this and covered nothing.
 *
 * So the model holds plain properties and plain functions, and the screen copies them into
 * Compose state. What actually makes the list live is not state ownership here - it is the poll
 * in [DiagnosticsScreen] that calls [refresh] every couple of seconds, which is what picks up
 * entries written by the tunnel threads while nobody is touching the screen.
 */
class DiagnosticsModel(
    /**
     * Reads the log and its counters. Injected rather than reaching for [NpLog] so the filtering,
     * ordering and problem-detection can be tested against entries and counters supplied on
     * purpose.
     *
     * Counters are behind this seam too, and that is not only tidiness. They used to be read from
     * the global logger, and the test for "the screen admits a log has holes in it" had to
     * provoke a genuine queue overrun to reach that branch. After the writer was made fast enough
     * to keep up with a 6000-entry burst, a 20,000-entry burst stopped overrunning it - so the
     * test could no longer reach the one path it existed for, and failed. The seam means the
     * condition is supplied directly, which is the only honest way to assert on it.
     */
    private val source: LogSource = LogSource.Default,
) {

    /** One row, flattened for the list. */
    data class Row(
        val stamp: String,
        val tag: String,
        val text: String,
        val detail: String?,
        val level: LogLevel,
    )

    data class Counters(
        val written: Long,
        val dropped: Long,
        val queued: Int,
        val writeFailures: Long,
    )

    interface LogSource {
        fun recent(max: Int, level: LogLevel, text: String?): List<LogEntry>

        fun counters(): NpLog.Counters

        companion object {
            val Default = object : LogSource {
                override fun recent(max: Int, level: LogLevel, text: String?) =
                    NpLog.recent(max, level, null, text)

                override fun counters() = NpLog.counters()
            }
        }
    }

    var level: LogLevel = LogLevel.TRACE
        private set

    var filter: String = ""
        private set

    /** The entries currently on screen, newest first. */
    var rows: List<Row> = emptyList()
        private set

    var counters: Counters = Counters(0, 0, 0, 0)
        private set

    /**
     * Whether the log can be believed.
     *
     * A bounded queue drops entries rather than blocking the caller, and either way a dropped
     * entry is a hole. So the screen does not present a short log as a complete one - it says
     * what is missing and how much. Without this the user reads a clean-looking log and concludes
     * the app is working fine, which is the one conclusion the log cannot support.
     */
    var hasProblems: Boolean = false
        private set

    /** Human sentence, or null when there is nothing to report. */
    var problemText: String? = null
        private set

    init {
        // Read once here rather than starting blank. An empty model reads as "nothing was
        // logged" - a real and wrong thing to tell someone who opened this screen because
        // something broke.
        refresh()
    }

    fun chooseLevel(newLevel: LogLevel) {
        if (newLevel == level) return
        level = newLevel
        refresh()
    }

    /**
     * @param text a blank string means no filter, not an empty result. Passing one through as a
     * real search term would empty the screen the moment the user cleared the box.
     */
    fun typeFilter(text: String) {
        val clean = text ?: ""
        if (clean == filter) return
        filter = clean
        refresh()
    }

    fun refresh() {
        rows = source
            .recent(MAX_ROWS, level, filter.ifBlank { null })
            .map(::toRow)
        val c = source.counters()
        counters = Counters(c.written, c.dropped, c.queued, c.writeFailures)
        hasProblems = c.dropped > 0 || c.writeFailures > 0
        problemText = if (!hasProblems) null else
            "${c.dropped} entries were dropped and ${c.writeFailures} write errors " +
                "occurred. The log below has gaps, which is why the counters are on this screen."
    }

    /**
     * The text for the clipboard, not a path.
     *
     * The log file lives in the app's private storage, which no file manager on the device can
     * reach, so a screen that only displayed a path would give the person holding the phone
     * nothing they could act on. This is the whole reason the screen has a button.
     */
    fun reportText(): String = NpLog.diagnosticsReport()

    private fun toRow(e: LogEntry) = Row(
        // The trailing Z is dropped because the column is too narrow to show it readably, but
        // every entry is UTC - so the header states that, rather than the reader having to trust
        // a timestamp with no marker on it.
        stamp = LogEntry.stamp(e.timestamp).removeSuffix("Z"),
        tag = e.level.tag,
        // The stack trace stays out of the message: concatenated, it makes the message stop
        // being a scannable one-liner and takes the trace's own line breaks with it.
        text = e.message,
        detail = e.detail,
        level = e.level,
    )

    companion object {
        /**
         * 400 rows, matching the desktop page so a bug report from either app shows the same
         * amount of context. A display cap rather than a memory one: the logger's buffer is
         * bounded at 1000 independently.
         */
        const val MAX_ROWS = 400
    }
}