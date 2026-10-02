package com.netpilot.mobile.ui.screens

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.netpilot.mobile.data.DayUsage
import com.netpilot.mobile.data.L
import com.netpilot.mobile.data.Repo
import com.netpilot.mobile.data.formatBytes
import com.netpilot.mobile.ui.components.BarChart
import com.netpilot.mobile.ui.components.EmptyState
import com.netpilot.mobile.ui.components.Gap
import com.netpilot.mobile.ui.components.NpCard
import com.netpilot.mobile.ui.components.NpChip
import com.netpilot.mobile.ui.components.SectionTitle
import com.netpilot.mobile.ui.components.StatTile
import com.netpilot.mobile.ui.theme.NpColors
import java.text.SimpleDateFormat
import java.util.Calendar
import java.util.Date
import java.util.Locale

private enum class Range { DAILY, WEEKLY, MONTHLY }

/**
 * Network History — daily totals recorded from the device-wide counters
 * ([Repo.recordUsage]) aggregated per day / week / month.
 */
@Composable
fun HistoryScreen() {
    val days by Repo.days.collectAsState()
    var range by remember { mutableStateOf(Range.DAILY) }

    val fmt = remember { SimpleDateFormat("MM-dd", Locale.US) }

    // Bucket the stored days according to the selected range.
    val buckets = remember(days, range) {
        when (range) {
            Range.DAILY -> lastNDays(14).map { key ->
                Bucket(label = fmt.format(parse(key)), days = listOf(days[key]))
            }

            Range.WEEKLY -> lastNDays(56).chunked(7).map { week ->
                val list = week.mapNotNull { days[it] }
                Bucket(
                    label = week.lastOrNull()?.let { fmt.format(parse(it)) } ?: "",
                    days = list
                )
            }

            Range.MONTHLY -> {
                val grouped = days.entries
                    .sortedBy { it.key }
                    .groupBy { it.key.take(7) }
                    .toSortedMap()
                grouped.map { (month, entries) ->
                    Bucket(label = month.take(7), days = entries.map { it.value })
                }.takeLast(12)
            }
        }
    }

    val filled = buckets.filter { b -> b.days.any { it != null } }
    val totals = filled.flatMap { b -> b.days.filterNotNull() }

    Column(
        Modifier
            .fillMaxSize()
            .background(NpColors.Bg)
            .statusBarsPadding()
            .verticalScroll(rememberScrollState())
            .padding(horizontal = 12.dp, vertical = 10.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp)
    ) {

        // ------------------------------------------------------------- range + chart
        NpCard {
            Row(verticalAlignment = Alignment.CenterVertically) {
                SectionTitle(L("his_title"), Modifier.weight(1f))
                Text(L("his_range"), color = NpColors.Muted, fontSize = 10.sp)
            }
            Row(horizontalArrangement = Arrangement.spacedBy(7.dp)) {
                NpChip(L("his_daily"), range == Range.DAILY, Modifier.weight(1f)) {
                    range = Range.DAILY
                }
                NpChip(L("his_weekly"), range == Range.WEEKLY, Modifier.weight(1f)) {
                    range = Range.WEEKLY
                }
                NpChip(L("his_monthly"), range == Range.MONTHLY, Modifier.weight(1f)) {
                    range = Range.MONTHLY
                }
            }
            Gap(10)

            if (filled.isEmpty()) {
                EmptyState(L("his_no_data"))
            } else {
                val list = filled.takeLast(14)
                BarChart(
                    values = list.map { b ->
                        b.days.filterNotNull().sumOf { it.total }.toFloat()
                    },
                    labels = list.map { it.label },
                    height = 150.dp
                )
            }
        }

        // ------------------------------------------------------------- summary
        if (totals.isNotEmpty()) {
            val totalBytes = totals.sumOf { it.total }
            val down = totals.sumOf { it.rx }
            val up = totals.sumOf { it.tx }
            val top = totals.filter { it.topPkg.isNotBlank() }
                .maxByOrNull { it.topRx + it.topTx }

            NpCard {
                SectionTitle(L("his_combined"))
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    StatTile(
                        label = L("his_combined"),
                        value = formatBytes(totalBytes),
                        accent = NpColors.Accent,
                        modifier = Modifier.weight(1f)
                    )
                    StatTile(
                        label = L("apps_down"),
                        value = formatBytes(down),
                        accent = NpColors.Accent,
                        modifier = Modifier.weight(1f)
                    )
                    StatTile(
                        label = L("apps_up"),
                        value = formatBytes(up),
                        accent = NpColors.Chart2,
                        modifier = Modifier.weight(1f)
                    )
                }
                if (top != null) {
                    Gap(8)
                    Text(L("his_top_app"), color = NpColors.Muted, fontSize = 10.sp)
                    Text(
                        top.topPkg + " · " + formatBytes(top.topRx + top.topTx),
                        color = NpColors.Text,
                        fontSize = 13.sp,
                        fontWeight = FontWeight.SemiBold
                    )
                }
            }

            // ---------------------------------------------------------- per-day list
            NpCard {
                SectionTitle(L("his_daily"))
                val rows = days.entries.sortedByDescending { it.key }.take(30)
                val peak = rows.maxOfOrNull { it.value.total }?.coerceAtLeast(1L) ?: 1L
                rows.forEach { (key, d) ->
                    DayRow(key, d, fraction = d.total.toFloat() / peak.toFloat())
                }
            }
        }

        Gap(4)
    }
}

private data class Bucket(val label: String, val days: List<DayUsage?>)

@Composable
private fun DayRow(key: String, d: DayUsage, fraction: Float) {
    Column(Modifier.fillMaxWidth().padding(vertical = 6.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(key, color = NpColors.Text, fontSize = 12.sp, modifier = Modifier.weight(1f))
            Text(
                "↓ ${formatBytes(d.rx)}  ↑ ${formatBytes(d.tx)}",
                color = NpColors.Muted,
                fontSize = 11.sp
            )
        }
        Gap(4)
        com.netpilot.mobile.ui.components.HBar(
            fraction = fraction.coerceIn(0.02f, 1f),
            color = NpColors.Accent
        )
        if (d.topPkg.isNotBlank()) {
            Gap(3)
            Text(
                "${L("his_top_app")}: ${d.topPkg} · ${formatBytes(d.topRx + d.topTx)}",
                color = NpColors.Muted,
                fontSize = 9.sp
            )
        }
    }
}

// ---------------------------------------------------------------------------- date helpers

private val KEY_FMT = SimpleDateFormat("yyyy-MM-dd", Locale.US)

private fun parse(key: String): Date = runCatching { KEY_FMT.parse(key) ?: Date() }
    .getOrDefault(Date())

private fun lastNDays(n: Int): List<String> {
    val cal = Calendar.getInstance()
    val out = ArrayList<String>(n)
    repeat(n) {
        out.add(0, KEY_FMT.format(Date(cal.timeInMillis)))
        cal.add(Calendar.DAY_OF_MONTH, -1)
    }
    return out
}
