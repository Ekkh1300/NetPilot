package com.netpilot.mobile.ui.screens

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalClipboardManager
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.netpilot.mobile.core.log.LogLevel
import com.netpilot.mobile.data.L
import com.netpilot.mobile.data.Strings
import com.netpilot.mobile.ui.components.EmptyState
import com.netpilot.mobile.ui.components.Gap
import com.netpilot.mobile.ui.components.GhostButton
import com.netpilot.mobile.ui.components.NpCard
import com.netpilot.mobile.ui.components.NpChip
import com.netpilot.mobile.ui.components.SearchField
import com.netpilot.mobile.ui.components.SectionTitle
import com.netpilot.mobile.ui.theme.NpColors
import kotlinx.coroutines.delay

/**
 * Diagnostics: the log, the counters that say whether it can be trusted, and one button that
 * turns the whole thing into something a person can paste into a bug report.
 *
 * The counters are at the top, above the entries, not buried at the bottom or in a file. A log
 * with a hole in it looks complete - the entries are all real, they are just not all of them -
 * so the dropped count belongs where it is read first. This is the reason the page exists and it
 * is the first thing on it.
 *
 * The whole point is that the report can leave the phone. The log file lives in the app's
 * private storage, which no file manager on the device can reach, so a screen that only showed
 * a path would be useless to the person holding the phone. The copy button puts the text -
 * counters, device, and the recent entries - on the clipboard, where it can be pasted anywhere.
 */
@Composable
fun DiagnosticsScreen() {
    val model = remember { DiagnosticsModel() }

    // Compose state lives here, not on the model.
    //
    // The obvious design - hold the state on the model with `by mutableStateOf` - makes the model
    // untestable: the delegate calls State.getValue(), which needs an initialised composition, so
    // every unit test throws a NullPointerException before reaching an assertion. So the model is
    // plain Kotlin and this screen mirrors it into Compose state.
    //
    // What keeps the list live is the poll below, not state ownership: entries written by the
    // tunnel threads change with nobody touching the screen, and that is precisely the moment
    // someone opens this screen.
    var rows by remember { mutableStateOf(model.rows) }
    var counters by remember { mutableStateOf(model.counters) }
    var problem by remember { mutableStateOf(model.problemText) }
    var level by remember { mutableStateOf(model.level) }
    var filter by remember { mutableStateOf(model.filter) }

    LaunchedEffect(Unit) {
        while (true) {
            model.refresh()
            rows = model.rows
            counters = model.counters
            problem = model.problemText
            delay(2_000)
        }
    }

    val clipboard = LocalClipboardManager.current

    Column(
        Modifier
            .fillMaxSize()
            .background(NpColors.Bg)
            .statusBarsPadding()
    ) {

        // ---- header: what this is, and the one button that makes it useful ----
        NpCard(modifier = Modifier.padding(horizontal = 12.dp, vertical = 8.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                SectionTitle(L("diag_title"), Modifier.weight(1f))
                GhostButton(L("diag_copy")) {
                    clipboard.setText(AnnotatedString(model.reportText()))
                }
            }
            Text(
                Strings.raw("diag_path") + " " + NpLog_path(),
                color = NpColors.Muted,
                fontSize = 10.sp,
                maxLines = 2,
                fontFamily = FontFamily.Monospace
            )
        }

        // ---- the counters ----
        NpCard(modifier = Modifier.padding(horizontal = 12.dp)) {
            Row(
                Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(8.dp)
            ) {
                Counter(L("diag_written"), counters.written.toString(), NpColors.Ok, Modifier.weight(1f))
                Counter(L("diag_dropped"), counters.dropped.toString(),
                    if (counters.dropped > 0) NpColors.Warn else NpColors.Muted,
                    Modifier.weight(1f))
                Counter(L("diag_queued"), counters.queued.toString(), NpColors.Muted,
                    Modifier.weight(1f))
                Counter(L("diag_errors"), counters.writeFailures.toString(),
                    if (counters.writeFailures > 0) NpColors.Bad else NpColors.Muted,
                    Modifier.weight(1f))
            }

            // Only when something was actually lost. A permanent warning the user cannot act on
            // is noise, and it trains people to ignore the one line that matters.
            val note = problem
            if (note != null) {
                Gap(10)
                Row(
                    Modifier
                        .fillMaxWidth()
                        .clip(RoundedCornerShape(10.dp))
                        .background(NpColors.Warn.copy(alpha = 0.12f))
                        .padding(11.dp),
                    verticalAlignment = Alignment.Top
                ) {
                    Text(note, color = NpColors.Warn, fontSize = 10.sp, lineHeight = 15.sp)
                }
            }
        }

        // ---- filters ----
        Row(
            Modifier
                .fillMaxWidth()
                .padding(horizontal = 12.dp, vertical = 8.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(6.dp)
        ) {
            // A horizontal scroll rather than a dropdown: with five levels the chips stay
            // tappable, and a phone screen has no room for a spinner plus a search field.
            Row(
                Modifier
                    .weight(1f)
                    .horizontalScroll(rememberScrollState()),
                horizontalArrangement = Arrangement.spacedBy(6.dp)
            ) {
                // Error and up are included: on a phone the reason to open this screen is
                // usually that something broke, and making them scroll to find it is a barrier
                // at the exact moment it costs most.
                listOf(
                    LogLevel.TRACE, LogLevel.DEBUG, LogLevel.INFO,
                    LogLevel.WARN, LogLevel.ERROR
                ).forEach { lv ->
                    NpChip(lv.tag, selected = lv == model.level) { model.chooseLevel(lv) }
                }
            }
        }

        SearchField(
            value = model.filter,
            onValueChange = { model.typeFilter(it) },
            placeholder = Strings.raw("diag_filter_hint")
        )

        // ---- the entries ----
        if (rows.isEmpty()) {
            NpCard(modifier = Modifier.padding(horizontal = 12.dp)) {
                EmptyState(L("diag_empty"))
            }
        } else {
            LazyColumn(
                modifier = Modifier.fillMaxSize(),
                contentPadding = androidx.compose.foundation.layout.PaddingValues(
                    start = 12.dp, end = 12.dp, bottom = 20.dp
                ),
                verticalArrangement = Arrangement.spacedBy(5.dp)
            ) {
                items(rows, key = { it.stamp + it.tag + it.hashCode() }) { row ->
                    EntryRow(row)
                }
            }
        }
    }
}

@Composable
private fun Counter(
    label: String,
    value: String,
    color: androidx.compose.ui.graphics.Color,
    modifier: Modifier,
) {
    Column(modifier, horizontalAlignment = Alignment.CenterHorizontally) {
        Text(label, color = NpColors.Muted, fontSize = 9.sp, textAlign = TextAlign.Center)
        Gap(2)
        // Monospace: these are counters that get compared against each other and against a
        // support reply, and proportional digits make that harder than it needs to be.
        Text(value, color = color, fontSize = 15.sp, fontWeight = FontWeight.SemiBold)
    }
}

@Composable
private fun EntryRow(row: DiagnosticsModel.Row) {
    val accent = when (row.level) {
        LogLevel.ERROR, LogLevel.FATAL -> NpColors.Bad
        LogLevel.WARN -> NpColors.Warn
        LogLevel.DEBUG, LogLevel.TRACE -> NpColors.Muted
        else -> NpColors.Text
    }

    NpCard(borderColor = accent.copy(alpha = 0.22f)) {
        Row(verticalAlignment = Alignment.Top) {
            Box(
                Modifier
                    .padding(top = 4.dp)
                    .size(7.dp)
                    .clip(RoundedCornerShape(50))
                    .background(accent)
            )
            Column(Modifier.weight(1f).padding(start = 9.dp)) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(
                        row.stamp, color = NpColors.Muted, fontSize = 9.sp,
                        fontFamily = FontFamily.Monospace
                    )
                    Gap(6)
                    Text(
                        row.tag, color = accent, fontSize = 9.sp,
                        fontWeight = FontWeight.Bold
                    )
                }
                Gap(3)
                Text(row.text, color = NpColors.Text, fontSize = 11.sp, lineHeight = 16.sp)
                // The stack trace, on its own lines and scrollable horizontally. Wrapped traces
                // are unreadable because the frame text has no line breaks of its own to wrap at.
                val detail = row.detail
                if (!detail.isNullOrEmpty()) {
                    Gap(3)
                    Text(
                        detail,
                        color = NpColors.Muted,
                        fontSize = 9.sp,
                        lineHeight = 13.sp,
                        fontFamily = FontFamily.Monospace,
                        modifier = Modifier.horizontalScroll(rememberScrollState())
                    )
                }
            }
        }
    }
}

/** The log file's path, or the honest alternative when there is none. */
private fun NpLog_path(): String =
    com.netpilot.mobile.core.log.NpLog.filePath ?: Strings.raw("diag_path_none")
