package com.netpilot.mobile.ui.screens

import android.content.Intent
import android.provider.Settings
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.netpilot.mobile.data.AppUsage
import com.netpilot.mobile.data.L
import com.netpilot.mobile.data.Repo
import com.netpilot.mobile.data.formatBytes
import com.netpilot.mobile.data.formatRate
import com.netpilot.mobile.net.AppUsageLoader
import com.netpilot.mobile.ui.components.EmptyState
import com.netpilot.mobile.ui.components.Gap
import com.netpilot.mobile.ui.components.HBar
import com.netpilot.mobile.ui.components.GhostButton
import com.netpilot.mobile.ui.components.NpButton
import com.netpilot.mobile.ui.components.NpCard
import com.netpilot.mobile.ui.components.NpChip
import com.netpilot.mobile.ui.components.SectionTitle
import com.netpilot.mobile.ui.components.SearchField
import com.netpilot.mobile.ui.theme.NpColors
import com.netpilot.mobile.ui.Routes
import com.netpilot.mobile.vpn.NetPilotVpnService
import com.netpilot.mobile.vpn.VpnState
import kotlinx.coroutines.delay

private enum class AppSort { TOTAL, RATE, NAME }

/**
 * Per-App Usage — real bytes per application read from Android's own accounting.
 * Live rates are derived from two counter snapshots taken one second apart.
 */
@Composable
fun PerAppScreen() {
    val ctx = LocalContext.current

    var rows by remember { mutableStateOf<List<AppUsage>>(emptyList()) }
    var source by remember { mutableStateOf("none") }
    var loading by remember { mutableStateOf(true) }
    var query by remember { mutableStateOf("") }
    var sort by remember { mutableStateOf(AppSort.TOTAL) }
    var hasAccess by remember { mutableStateOf(AppUsageLoader.hasUsageAccess(ctx)) }
    var reloadKey by remember { mutableStateOf(0) }

    val rules by Repo.rules.collectAsState()
    val mode by VpnState.mode.collectAsState()

    // Initial load, refresh on permission change and on the manual refresh button.
    LaunchedEffect(hasAccess, reloadKey) {
        loading = true
        val snap = AppUsageLoader.load(ctx)
        rows = snap.rows
        source = snap.source
        loading = false
    }

    // Live rates: sample the same uids twice, one second apart.
    LaunchedEffect(rows.size) {
        if (rows.isEmpty()) return@LaunchedEffect
        // The counters TrafficStats hands back are cumulative, so a rate is the difference
        // between two *consecutive* samples. Re-deriving the baseline from `rows` gave the
        // same constant snapshot every pass, which made the "Live speed" column grow with
        // the time the screen had been open instead of with the traffic.
        var prev = rows.associate { it.uid to longArrayOf(it.rxTotal, it.txTotal) }
        while (true) {
            delay(1000)
            val uids = rows.map { it.uid }
            val now = AppUsageLoader.rates(ctx, uids)
            val next = HashMap<Int, LongArray>(uids.size)
            rows = rows.map { r ->
                val b = prev[r.uid] ?: return@map r
                val n = now[r.uid] ?: return@map r
                next[r.uid] = n
                r.apply {
                    rxRate = (n[0] - b[0]).coerceAtLeast(0L)
                    txRate = (n[1] - b[1]).coerceAtLeast(0L)
                }
            }
            prev = next
        }
    }

    val filtered = remember(rows, query, sort) {
        val q = query.trim()
        rows.filter { q.isEmpty() || it.label.contains(q, true) || it.pkg.contains(q, true) }
            .sortedWith(
                when (sort) {
                    AppSort.NAME -> compareBy { it.label.lowercase() }
                    AppSort.RATE -> compareByDescending { it.rxRate + it.txRate }
                    AppSort.TOTAL -> compareByDescending { it.total }
                }
            )
    }

    Column(
        Modifier
            .fillMaxSize()
            .background(NpColors.Bg)
            .statusBarsPadding()
            .padding(horizontal = 12.dp, vertical = 10.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp)
    ) {

        // ---------------------------------------------------------- permission gate
        if (source == "none" && !loading && !hasAccess) {
            NpCard {
                SectionTitle(L("apps_grant"))
                Text(L("apps_grant_desc"), color = NpColors.Muted, fontSize = 12.sp)
                Gap(10)
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    NpButton(
                        text = L("apps_grant_action"),
                        modifier = Modifier.weight(1f)
                    ) {
                        runCatching {
                            ctx.startActivity(Intent(Settings.ACTION_USAGE_ACCESS_SETTINGS))
                        }.onFailure {
                            ctx.startActivity(Intent(Settings.ACTION_SETTINGS))
                        }
                    }
                    GhostButton(
                        text = L("refresh"),
                        modifier = Modifier.weight(1f)
                    ) { hasAccess = AppUsageLoader.hasUsageAccess(ctx) }
                }
            }
        }

        // ---------------------------------------------------------- summary
        NpCard {
            SectionTitle(L("apps_title"))
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                StatTriple(
                    label = L("apps_total"),
                    value = formatBytes(rows.sumOf { it.total }),
                    modifier = Modifier.weight(1f)
                )
                StatTriple(
                    label = L("apps_down"),
                    value = formatBytes(rows.sumOf { it.rxTotal }),
                    modifier = Modifier.weight(1f)
                )
                StatTriple(
                    label = L("apps_up"),
                    value = formatBytes(rows.sumOf { it.txTotal }),
                    modifier = Modifier.weight(1f)
                )
            }
            Gap(8)
            SearchField(query, { query = it }, L("search"))
            Gap(8)
            Row(horizontalArrangement = Arrangement.spacedBy(7.dp)) {
                NpChip(L("apps_sort_total"), sort == AppSort.TOTAL, Modifier.weight(1f)) {
                    sort = AppSort.TOTAL
                }
                NpChip(L("apps_sort_rate"), sort == AppSort.RATE, Modifier.weight(1f)) {
                    sort = AppSort.RATE
                }
                NpChip(L("apps_sort_name"), sort == AppSort.NAME, Modifier.weight(1f)) {
                    sort = AppSort.NAME
                }
            }
            Gap(8)
            Row(
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(8.dp)
            ) {
                Text(
                    if (loading) L("loading") else "${filtered.size} · ${L("apps_process")}",
                    color = NpColors.Muted,
                    fontSize = 10.sp,
                    modifier = Modifier.weight(1f)
                )
                GhostButton(L("refresh")) { reloadKey++ }
            }
        }

        // ---------------------------------------------------------- rows
        if (!loading && filtered.isEmpty()) {
            NpCard { EmptyState(L("apps_no_data")) }
        } else {
            Column(verticalArrangement = Arrangement.spacedBy(9.dp)) {
                val topShare = filtered.firstOrNull()?.total?.coerceAtLeast(1L) ?: 1L
                filtered.take(LIST_LIMIT).forEach { app ->
                    AppRow(
                        app = app,
                        share = app.total.toFloat() / topShare.toFloat(),
                        rule = rules.firstOrNull { it.pkg == app.pkg },
                        modeOn = mode != VpnState.Mode.OFF
                    )
                }
                if (filtered.size > LIST_LIMIT) {
                    Text(
                        "${LIST_LIMIT} / ${filtered.size}",
                        color = NpColors.Muted,
                        fontSize = 10.sp
                    )
                }
            }
        }

        Gap(4)
    }
}

private const val LIST_LIMIT = 60

@Composable
private fun StatTriple(label: String, value: String, modifier: Modifier = Modifier) {
    Column(
        modifier = modifier
            .clip(RoundedCornerShape(12.dp))
            .background(NpColors.CardAlt)
            .padding(horizontal = 10.dp, vertical = 8.dp)
    ) {
        Text(label, color = NpColors.Muted, fontSize = 10.sp)
        Text(value, color = NpColors.Accent, fontSize = 14.sp, fontWeight = FontWeight.Bold)
    }
}

@Composable
private fun AppRow(
    app: AppUsage,
    share: Float,
    rule: com.netpilot.mobile.data.LimitRule?,
    modeOn: Boolean
) {
    val blocked = rule?.mode == com.netpilot.mobile.data.RuleMode.BLOCK
    val limited = rule?.mode == com.netpilot.mobile.data.RuleMode.LIMIT

    NpCard(
        borderColor = when {
            blocked && modeOn -> NpColors.Bad.copy(alpha = 0.6f)
            limited && modeOn -> NpColors.Warn.copy(alpha = 0.6f)
            else -> NpColors.Stroke
        }
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text(
                    app.label,
                    color = NpColors.Text,
                    fontSize = 13.sp,
                    fontWeight = FontWeight.SemiBold,
                    maxLines = 1
                )
                Text(
                    app.pkg + when {
                        blocked -> "  · " + L("lim_block")
                        limited -> "  · " + L("lim_limit")
                        else -> ""
                    },
                    color = when {
                        blocked && modeOn -> NpColors.Bad
                        limited && modeOn -> NpColors.Warn
                        else -> NpColors.Muted
                    },
                    fontSize = 9.sp,
                    maxLines = 1
                )
                Gap(6)
                HBar(
                    fraction = share,
                    color = when {
                        blocked && modeOn -> NpColors.Bad
                        limited && modeOn -> NpColors.Warn
                        else -> NpColors.Accent
                    }
                )
            }
            Column(horizontalAlignment = Alignment.End) {
                Text(
                    formatBytes(app.total),
                    color = NpColors.Text,
                    fontSize = 13.sp,
                    fontWeight = FontWeight.Bold
                )
                Text(
                    "↓ ${formatRate(app.rxRate)}",
                    color = NpColors.Accent,
                    fontSize = 10.sp
                )
                Text(
                    "↑ ${formatRate(app.txRate)}",
                    color = NpColors.Chart2,
                    fontSize = 10.sp
                )
            }
        }
    }
}
