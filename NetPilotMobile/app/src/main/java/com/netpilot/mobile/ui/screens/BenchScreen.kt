package com.netpilot.mobile.ui.screens

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.netpilot.mobile.core.AppGraph
import com.netpilot.mobile.data.DnsBenchResult
import com.netpilot.mobile.data.L
import com.netpilot.mobile.data.Repo
import com.netpilot.mobile.ui.components.EmptyState
import com.netpilot.mobile.ui.components.Gap
import com.netpilot.mobile.ui.components.HBar
import com.netpilot.mobile.ui.components.NpButton
import com.netpilot.mobile.ui.components.NpCard
import com.netpilot.mobile.ui.components.NpChip
import com.netpilot.mobile.ui.components.SectionTitle
import com.netpilot.mobile.ui.theme.NpColors
import com.netpilot.mobile.vpn.NetPilotVpnService
import com.netpilot.mobile.vpn.VpnState
import kotlinx.coroutines.launch

/**
 * DNS Benchmark: real queries against every provider, N rounds each, ranked by average
 * latency with a packet-loss penalty. Runs on the IO dispatcher so the UI never freezes.
 */
@Composable
fun BenchScreen() {
    val ctx = LocalContext.current
    val scope = rememberCoroutineScope()

    val progress by AppGraph.benchmark.progress.collectAsState()
    val results by AppGraph.benchmark.results.collectAsState()
    val activeDns by VpnState.dnsId.collectAsState()

    var rounds by remember { mutableIntStateOf(3) }
    var onlyFavs by remember { mutableStateOf(false) }

    val running = progress.running

    Column(
        Modifier
            .fillMaxSize()
            .background(NpColors.Bg)
            .statusBarsPadding()
            .verticalScroll(rememberScrollState())
            .padding(horizontal = 12.dp, vertical = 10.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp)
    ) {

        // -------------------------------------------------- controls
        NpCard {
            SectionTitle(L("bench_title"))
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(L("bench_round"), color = NpColors.Muted, fontSize = 12.sp)
                Spacer(Modifier.size(10.dp))
                listOf(3, 5, 10).forEach { r ->
                    NpChip(
                        text = "$r",
                        selected = rounds == r,
                        onClick = { if (!running) rounds = r },
                        modifier = Modifier.padding(end = 6.dp)
                    )
                }
                Spacer(Modifier.weight(1f))
                NpChip(
                    text = L("bench_all_resolvers"),
                    selected = !onlyFavs,
                    onClick = { if (!running) onlyFavs = false }
                )
            }
            Gap(8)
            Row(verticalAlignment = Alignment.CenterVertically) {
                NpChip(
                    text = L("bench_selected"),
                    selected = onlyFavs,
                    onClick = { if (!running) onlyFavs = true },
                    modifier = Modifier.weight(1f)
                )
                Spacer(Modifier.size(10.dp))
                NpButton(
                    text = if (running) L("bench_running") else L("bench_run"),
                    enabled = !running,
                    onClick = {
                        val catalog = AppGraph.dnsCatalog
                        val entries = if (onlyFavs) {
                            val favs = catalog.favorites()
                            val ids = favs + catalog.selectedId
                            catalog.all().filter { it.id in ids }
                        } else {
                            catalog.all()
                        }
                        scope.launch {
                            AppGraph.benchmark.run(entries, rounds = rounds)
                            Repo.log("evt_dns_changed", com.netpilot.mobile.data.Strings.raw("bench_finish"))
                        }
                    }
                )
            }

            if (running) {
                Gap(10)
                val frac = if (progress.total > 0) progress.done.toFloat() / progress.total else 0f
                LinearProgressIndicator(
                    progress = { frac },
                    modifier = Modifier
                        .fillMaxWidth()
                        .clip(RoundedCornerShape(8.dp)),
                    color = NpColors.Accent,
                    trackColor = NpColors.CardAlt
                )
                Gap(6)
                Text(
                    "${progress.done}/${progress.total} · ${progress.current}",
                    color = NpColors.Muted,
                    fontSize = 11.sp
                )
            }
        }

        // -------------------------------------------------- results
        NpCard {
            Row(verticalAlignment = Alignment.CenterVertically) {
                SectionTitle(L("bench_finish"), Modifier.weight(1f))
                if (results.isNotEmpty() && !running) {
                    NpButton(
                        text = L("bench_apply_best"),
                        enabled = results.first().avgMs > 0 && activeDns != results.first().entry.id,
                        onClick = {
                            NetPilotVpnService.applyDns(ctx, results.first().entry.id)
                        }
                    )
                }
            }

            if (results.isEmpty()) {
                EmptyState(L("bench_no_results"))
                return@NpCard
            }

            val best = results.firstOrNull { it.avgMs > 0 }?.avgMs ?: 0
            val worst = results.maxOfOrNull { it.avgMs } ?: 0
            val span = (worst - best).coerceAtLeast(1)

            results.forEach { r ->
                BenchmarkRow(r, best, span, activeDns) {
                    NetPilotVpnService.applyDns(ctx, r.entry.id)
                }
            }
        }

        Gap(4)
    }
}

@Composable
private fun BenchmarkRow(
    r: DnsBenchResult,
    best: Int,
    span: Int,
    activeDns: String,
    onApply: () -> Unit
) {
    val usable = r.avgMs > 0
    val frac = if (!usable) 1f else (1f - (r.avgMs - best).toFloat() / span).coerceIn(0.05f, 1f)
    val rowColor = when {
        !usable -> NpColors.Bad
        r.rank == 1 -> NpColors.Ok
        r.lossPct > 0 -> NpColors.Warn
        else -> NpColors.Accent
    }

    Column(
        Modifier
            .fillMaxWidth()
            .padding(vertical = 7.dp)
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Box2(r.rank, rowColor)
            Spacer(Modifier.size(9.dp))
            Column(Modifier.weight(1f)) {
                Text(
                    r.entry.name + if (r.entry.id == activeDns) "  ●" else "",
                    color = if (r.entry.id == activeDns) NpColors.Ok else NpColors.Text,
                    fontSize = 13.sp,
                    fontWeight = FontWeight.SemiBold
                )
                Text(
                    r.entry.servers.joinToString(" · "),
                    color = NpColors.Muted,
                    fontSize = 10.sp
                )
            }
            Column(horizontalAlignment = Alignment.End) {
                Text(
                    if (usable) "${r.avgMs} ms" else "—",
                    color = rowColor,
                    fontSize = 14.sp,
                    fontWeight = FontWeight.Bold
                )
                Text(
                    "${L("bench_loss")} ${formatLoss(r.lossPct)} · " +
                            "${L("bench_jitter")} ${r.jitterMs} ms",
                    color = NpColors.Muted,
                    fontSize = 9.sp
                )
            }
        }
        Gap(5)
        Row(verticalAlignment = Alignment.CenterVertically) {
            HBar(
                fraction = frac,
                color = rowColor,
                modifier = Modifier.weight(1f)
            )
            if (r.entry.id != activeDns) {
                Spacer(Modifier.size(9.dp))
                NpChip(text = L("apply"), selected = false, onClick = onApply)
            }
        }
    }
}

@Composable
private fun Box2(rank: Int, color: androidx.compose.ui.graphics.Color) {
    androidx.compose.foundation.layout.Box(
        modifier = Modifier
            .size(26.dp)
            .clip(RoundedCornerShape(8.dp))
            .background(color.copy(alpha = 0.16f)),
        contentAlignment = Alignment.Center
    ) {
        Text(
            if (rank in 1..99) "$rank" else "·",
            color = color,
            fontSize = 12.sp,
            fontWeight = FontWeight.Bold
        )
    }
}

private fun formatLoss(v: Double): String =
    if (v <= 0.0) "0%" else String.format(java.util.Locale.US, "%.1f%%", v)
