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
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.netpilot.mobile.data.L
import com.netpilot.mobile.data.Repo
import com.netpilot.mobile.data.formatBytes
import com.netpilot.mobile.data.formatMs
import com.netpilot.mobile.data.formatPercent
import com.netpilot.mobile.data.formatRate
import com.netpilot.mobile.net.Monitor
import com.netpilot.mobile.ui.components.BarChart
import com.netpilot.mobile.ui.components.Gap
import com.netpilot.mobile.ui.components.GhostButton
import com.netpilot.mobile.ui.components.NpCard
import com.netpilot.mobile.ui.components.ProgressRing
import com.netpilot.mobile.ui.components.SectionTitle
import com.netpilot.mobile.ui.components.StatTile
import com.netpilot.mobile.ui.components.TrafficChart
import com.netpilot.mobile.ui.theme.NpColors
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * Network Monitor — live throughput, latency, loss, DNS timing and link facts.
 * Every number comes from [Monitor]; nothing here is simulated.
 */
@Composable
fun MonitorScreen() {
    val link by Monitor.link.collectAsState()
    val traffic by Monitor.traffic.collectAsState()
    val ping by Monitor.ping.collectAsState()
    val dnsMs by Monitor.dnsMs.collectAsState()
    val health by Monitor.health.collectAsState()

    Column(
        Modifier
            .fillMaxSize()
            .background(NpColors.Bg)
            .statusBarsPadding()
            .verticalScroll(rememberScrollState())
            .padding(horizontal = 12.dp, vertical = 10.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp)
    ) {

        // ------------------------------------------------------------------ link
        NpCard {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    SectionTitle(L("mon_title"))
                    Text(
                        link.typeName.ifBlank { L("mon_none") },
                        color = if (link.connected) NpColors.Ok else NpColors.Bad,
                        fontSize = 18.sp,
                        fontWeight = FontWeight.Bold
                    )
                    Gap(3)
                    Text(
                        listOfNotNull(
                            link.localIp.ifBlank { null },
                            link.gateway.ifBlank { null }.let {
                                if (it == null) null else "${L("adp_gateway")}: $it"
                            }
                        ).joinToString("  ·  ").ifBlank { "—" },
                        color = NpColors.Muted,
                        fontSize = 11.sp
                    )
                }
                ProgressRing(
                    progress = health.score / 100f,
                    color = when {
                        health.score >= 85 -> NpColors.Ok
                        health.score >= 70 -> NpColors.Accent
                        health.score >= 50 -> NpColors.Warn
                        else -> NpColors.Bad
                    },
                    size = 84.dp,
                    stroke = 9.dp
                )
            }
        }

        // ------------------------------------------------------------------ live chart
        NpCard {
            Row(verticalAlignment = Alignment.CenterVertically) {
                SectionTitle(L("mon_speed"), Modifier.weight(1f))
                Text(
                    "${L("mon_live")} · ${Repo.monitorInterval()} ms",
                    color = NpColors.Muted,
                    fontSize = 10.sp
                )
            }
            TrafficChart(traffic.history)
            Gap(10)
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                StatTile(
                    label = "↓ ${L("dash_download")}",
                    value = formatRate(traffic.rxRate),
                    accent = NpColors.Accent,
                    modifier = Modifier.weight(1f)
                )
                StatTile(
                    label = "↑ ${L("dash_upload")}",
                    value = formatRate(traffic.txRate),
                    accent = NpColors.Chart2,
                    modifier = Modifier.weight(1f)
                )
            }
            Gap(8)
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                StatTile(
                    label = L("mon_total_down"),
                    value = formatBytes(traffic.sessionRx),
                    sub = formatBytes(traffic.totalRx),
                    accent = NpColors.Accent,
                    modifier = Modifier.weight(1f)
                )
                StatTile(
                    label = L("mon_total_up"),
                    value = formatBytes(traffic.sessionTx),
                    sub = formatBytes(traffic.totalTx),
                    accent = NpColors.Chart2,
                    modifier = Modifier.weight(1f)
                )
            }
            Gap(6)
            Text(
                "${L("mon_since")}: " + TIME_FMT.format(Date(Monitor.sessionStartedAt())),
                color = NpColors.Muted,
                fontSize = 10.sp
            )
        }

        // ------------------------------------------------------------------ latency
        NpCard {
            SectionTitle(L("mon_latency"))
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                StatTile(
                    label = L("dash_ping"),
                    value = formatMs(ping.ms),
                    sub = ping.target,
                    accent = NpColors.Ok,
                    modifier = Modifier.weight(1f)
                )
                StatTile(
                    label = L("bench_avg"),
                    value = formatMs(ping.avgMs),
                    accent = NpColors.Accent,
                    modifier = Modifier.weight(1f)
                )
                StatTile(
                    label = L("dash_packet_loss"),
                    value = formatPercent(ping.lossPct),
                    accent = if (ping.lossPct > 5) NpColors.Bad else NpColors.Warn,
                    modifier = Modifier.weight(1f)
                )
            }
            if (ping.samples.isNotEmpty()) {
                Gap(10)
                BarChart(
                    values = ping.samples.map { it.coerceAtLeast(0).toFloat() },
                    labels = ping.samples.map { "$it" },
                    height = 92.dp,
                    color = NpColors.Ok
                )
            }
        }

        // ------------------------------------------------------------------ dns + health
        NpCard {
            SectionTitle(L("hlt_dns"))
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                StatTile(
                    label = L("hlt_dns"),
                    value = formatMs(dnsMs),
                    accent = NpColors.Accent,
                    modifier = Modifier.weight(1f)
                )
                StatTile(
                    label = L("sample_docs"),
                    value = "${health.sampleCount}",
                    accent = NpColors.Muted,
                    modifier = Modifier.weight(1f)
                )
                StatTile(
                    label = L("dash_network_health"),
                    value = "${health.score}%",
                    accent = when {
                        health.score >= 85 -> NpColors.Ok
                        health.score >= 50 -> NpColors.Warn
                        else -> NpColors.Bad
                    },
                    modifier = Modifier.weight(1f)
                )
            }
            Gap(10)
            GhostButton(L("refresh")) {
                Monitor.refreshLink()
                Monitor.recomputeHealth()
            }
        }

        Gap(4)
    }
}

private val TIME_FMT = SimpleDateFormat("HH:mm:ss", Locale.US)
