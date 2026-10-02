package com.netpilot.mobile.ui.screens

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.ChevronRight
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.netpilot.mobile.core.AppGraph
import com.netpilot.mobile.data.L
import com.netpilot.mobile.data.Repo
import com.netpilot.mobile.data.formatBytes
import com.netpilot.mobile.data.formatPercent
import com.netpilot.mobile.data.formatRate
import com.netpilot.mobile.net.Monitor
import com.netpilot.mobile.ui.Routes
import com.netpilot.mobile.ui.components.BarChart
import com.netpilot.mobile.ui.components.Gap
import com.netpilot.mobile.ui.components.GhostButton
import com.netpilot.mobile.ui.components.LiveOrb
import com.netpilot.mobile.ui.components.NpCard
import com.netpilot.mobile.ui.components.NpChip
import com.netpilot.mobile.ui.components.OrbState
import com.netpilot.mobile.ui.components.ProgressRing
import com.netpilot.mobile.ui.components.SectionTitle
import com.netpilot.mobile.ui.components.StatTile
import com.netpilot.mobile.ui.components.TrafficChart
import com.netpilot.mobile.ui.theme.NpColors
import com.netpilot.mobile.vpn.NetPilotVpnService
import com.netpilot.mobile.vpn.VpnState
import java.text.SimpleDateFormat
import java.util.Calendar
import java.util.Date
import java.util.Locale

/**
 * Dashboard: health score, live orb, live traffic chart, quick DNS switcher and a
 * history preview. Every figure is a real measurement from [Monitor] or [VpnState].
 */
@Composable
fun DashboardScreen(onOpen: (String) -> Unit) {
    val ctx = LocalContext.current
    val link by Monitor.link.collectAsState()
    val traffic by Monitor.traffic.collectAsState()
    val ping by Monitor.ping.collectAsState()
    val health by Monitor.health.collectAsState()

    val mode by VpnState.mode.collectAsState()
    val dnsId by VpnState.dnsId.collectAsState()
    val dnsName by VpnState.dnsName.collectAsState()
    val dnsStats by VpnState.stats.collectAsState()
    val vpnError by VpnState.error.collectAsState()

    val healthColor = scoreColor(health.score)
    val orb = OrbState(
        connected = link.connected,
        vpnActive = mode != VpnState.Mode.OFF,
        rxRate = traffic.rxRate,
        txRate = traffic.txRate
    )

    Column(
        Modifier
            .fillMaxSize()
            .background(NpColors.Bg)
            .statusBarsPadding()
            .verticalScroll(rememberScrollState())
            .padding(horizontal = 12.dp, vertical = 10.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp)
    ) {

        // -------------------------------------------------- health score
        NpCard {
            Row(verticalAlignment = Alignment.CenterVertically) {
                ProgressRing(
                    progress = health.score / 100f,
                    color = healthColor,
                    label = scoreText(health.score),
                    sub = L("dash_network_health"),
                    size = 124.dp
                )
                Spacer(Modifier.size(14.dp))
                Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(7.dp)) {
                    HealthRow(
                        name = L("hlt_dns"),
                        value = if (health.dnsMs < 0) "—" else "${health.dnsMs} ms",
                        state = stateText(health.dnsLabel),
                        color = NpColors.Accent
                    )
                    HealthRow(
                        name = L("hlt_latency"),
                        value = if (health.latencyMs < 0) "—" else "${health.latencyMs} ms",
                        state = stateText(health.latencyLabel),
                        color = NpColors.Chart2
                    )
                    HealthRow(
                        name = L("hlt_loss"),
                        value = formatPercent(health.lossPct),
                        state = stateText(health.lossLabel),
                        color = NpColors.Warn
                    )
                    HealthRow(
                        name = L("hlt_connection"),
                        value = link.typeName.ifBlank { L("mon_none") },
                        state = connectionText(health.stableLabel),
                        color = healthColor
                    )
                }
            }
        }

        // -------------------------------------------------- orb + live speed
        NpCard {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Box(contentAlignment = Alignment.Center) {
                    LiveOrb(state = orb, size = 154.dp)
                    Column(horizontalAlignment = Alignment.CenterHorizontally) {
                        Text(
                            if (link.connected) L("dash_connected") else L("dash_disconnected"),
                            color = if (link.connected) NpColors.Text else NpColors.Bad,
                            fontSize = 11.sp,
                            fontWeight = FontWeight.SemiBold
                        )
                        Text(
                            orbCaption(traffic.rxRate, traffic.txRate, link.connected),
                            color = NpColors.Muted,
                            fontSize = 9.sp
                        )
                    }
                }
                Spacer(Modifier.size(12.dp))
                Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    StatTile(
                        label = "↓ " + L("dash_download"),
                        value = formatRate(traffic.rxRate),
                        accent = NpColors.Accent,
                        modifier = Modifier.fillMaxWidth()
                    )
                    StatTile(
                        label = "↑ " + L("dash_upload"),
                        value = formatRate(traffic.txRate),
                        accent = NpColors.Chart2,
                        modifier = Modifier.fillMaxWidth()
                    )
                    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        StatTile(
                            label = L("dash_ping"),
                            value = if (ping.avgMs < 0) "—" else "${ping.avgMs} ms",
                            accent = NpColors.Ok,
                            modifier = Modifier.weight(1f)
                        )
                        StatTile(
                            label = L("dash_packet_loss"),
                            value = formatPercent(ping.lossPct),
                            accent = if (ping.lossPct > 5) NpColors.Bad else NpColors.Warn,
                            modifier = Modifier.weight(1f)
                        )
                    }
                }
            }
        }

        // -------------------------------------------------- live chart
        NpCard {
            Row(verticalAlignment = Alignment.CenterVertically) {
                SectionTitle(L("dash_traffic_live"), Modifier.weight(1f))
                Text(
                    "${L("mon_session")}: ${formatRate(traffic.rxRate)} / ${formatRate(traffic.txRate)}",
                    color = NpColors.Muted,
                    fontSize = 10.sp
                )
            }
            TrafficChart(traffic.history)
            Gap(8)
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                StatTile(
                    label = L("mon_total_down"),
                    value = formatBytes(traffic.sessionRx),
                    accent = NpColors.Accent,
                    modifier = Modifier.weight(1f)
                )
                StatTile(
                    label = L("mon_total_up"),
                    value = formatBytes(traffic.sessionTx),
                    accent = NpColors.Chart2,
                    modifier = Modifier.weight(1f)
                )
                StatTile(
                    label = L("dash_active_dns"),
                    value = dnsName.ifBlank { L("dash_dns_auto") },
                    accent = if (mode != VpnState.Mode.OFF) NpColors.Ok else NpColors.Muted,
                    modifier = Modifier.weight(1f)
                )
            }
        }

        // -------------------------------------------------- quick DNS switcher
        NpCard {
            Row(verticalAlignment = Alignment.CenterVertically) {
                SectionTitle(L("dash_quick_dns"), Modifier.weight(1f))
                if (mode != VpnState.Mode.OFF) {
                    Text(
                        "${L("dash_vpn_on")} · ${dnsStats.avgMs} ms",
                        color = NpColors.Ok,
                        fontSize = 10.sp
                    )
                } else {
                    Text(L("dash_vpn_off"), color = NpColors.Muted, fontSize = 10.sp)
                }
            }

            val systemDns = link.dnsServers.firstOrNull() ?: "—"
            Text("${L("dash_system_dns")}: $systemDns", color = NpColors.Muted, fontSize = 11.sp)
            Gap(6)

            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                NpChip("Cloudflare", dnsId == "cloudflare") {
                    NetPilotVpnService.applyDns(ctx, "cloudflare")
                }
                NpChip("Google", dnsId == "google") {
                    NetPilotVpnService.applyDns(ctx, "google")
                }
                NpChip("Quad9", dnsId == "quad9") {
                    NetPilotVpnService.applyDns(ctx, "quad9")
                }
                NpChip(
                    text = L("restore"),
                    selected = mode == VpnState.Mode.OFF
                ) { NetPilotVpnService.restoreSystemDns(ctx) }
            }

            Gap(6)
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(
                    text = if (mode != VpnState.Mode.OFF && dnsName.isNotBlank()) {
                        "${L("dns_servers")}: " + (AppGraph.dnsCatalog.find(dnsId)
                            ?.servers?.joinToString(" · ") ?: dnsName)
                    } else {
                        "${L("dns_current")}: $systemDns"
                    },
                    color = NpColors.Muted,
                    fontSize = 11.sp,
                    modifier = Modifier.weight(1f)
                )
                Icon(
                    Icons.Outlined.ChevronRight,
                    contentDescription = L("details"),
                    tint = NpColors.Accent,
                    modifier = Modifier
                        .clip(CircleShape)
                        .background(NpColors.CardAlt)
                        .clickable { onOpen(Routes.DNS) }
                        .padding(7.dp)
                )
            }

            if (vpnError.isNotBlank()) {
                Gap(6)
                Text(vpnError, color = NpColors.Bad, fontSize = 11.sp)
            }
            if (dnsStats.queries > 0) {
                Gap(4)
                Text(
                    text = "${L("sample_docs")}: ${dnsStats.queries} · " +
                            "${L("bench_avg")}: ${dnsStats.avgMs} ms · " +
                            "cache hit: ${dnsStats.cacheHits}",
                    color = NpColors.Muted,
                    fontSize = 10.sp
                )
            }
        }

        // -------------------------------------------------- history preview
        HistoryPreview(onOpen)

        Gap(4)
    }
}

// ---------------------------------------------------------------------------- history preview

@Composable
private fun HistoryPreview(onOpen: (String) -> Unit) {
    val days by Repo.days.collectAsState()

    val keys = lastNDays(7)
    val values = keys.map { k -> ((days[k]?.rx ?: 0L) + (days[k]?.tx ?: 0L)).toFloat() }
    val labels = keys.map { it.takeLast(2) }
    val hasData = values.any { it > 0f }
    val topApp = keys.lastOrNull()?.let { days[it]?.topPkg }.orEmpty()

    NpCard {
        Row(verticalAlignment = Alignment.CenterVertically) {
            SectionTitle(L("his_title"), Modifier.weight(1f))
            Icon(
                Icons.Outlined.ChevronRight,
                contentDescription = L("details"),
                tint = NpColors.Muted,
                modifier = Modifier
                    .clip(CircleShape)
                    .clickable { onOpen(Routes.HISTORY) }
                    .padding(4.dp)
            )
        }
        if (hasData) {
            BarChart(values = values, labels = labels, height = 110.dp)
            Gap(8)
            Row(verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text(L("his_top_app"), color = NpColors.Muted, fontSize = 10.sp)
                    Text(
                        topApp.ifBlank { L("unknown") },
                        color = NpColors.Text,
                        fontSize = 13.sp,
                        fontWeight = FontWeight.SemiBold
                    )
                }
                GhostButton(L("details")) { onOpen(Routes.HISTORY) }
            }
        } else {
            Text(L("his_no_data"), color = NpColors.Muted, fontSize = 12.sp)
            Gap(8)
            GhostButton(L("details")) { onOpen(Routes.HISTORY) }
        }
    }
}

private fun lastNDays(n: Int): List<String> {
    val fmt = SimpleDateFormat("yyyy-MM-dd", Locale.US)
    val cal = Calendar.getInstance()
    val out = ArrayList<String>(n)
    repeat(n) {
        out.add(0, fmt.format(Date(cal.timeInMillis)))
        cal.add(Calendar.DAY_OF_MONTH, -1)
    }
    return out
}

// ---------------------------------------------------------------------------- labels

private fun scoreColor(score: Int): Color = when {
    score >= 85 -> NpColors.Ok
    score >= 70 -> NpColors.Accent
    score >= 50 -> NpColors.Warn
    else -> NpColors.Bad
}

@Composable
private fun scoreText(score: Int): String = when {
    score >= 85 -> L("hlt_excellent")
    score >= 70 -> L("hlt_good")
    score >= 50 -> L("hlt_fair")
    else -> L("hlt_poor")
}

@Composable
private fun stateText(code: String): String = when (code) {
    "NA" -> L("hlt_na")
    "EXCELLENT" -> L("hlt_excellent")
    "GOOD" -> L("hlt_good")
    "FAIR" -> L("hlt_fair")
    "STABLE" -> L("hlt_stable")
    "UNSTABLE" -> L("hlt_unstable")
    else -> L("hlt_poor")
}

@Composable
private fun connectionText(code: String): String = when (code) {
    "STABLE" -> L("hlt_stable")
    "UNSTABLE" -> L("hlt_unstable")
    else -> L("dash_disconnected")
}

@Composable
private fun orbCaption(rx: Long, tx: Long, connected: Boolean): String = when {
    !connected -> L("orb_off")
    rx > 0 && tx > 0 -> L("orb_mixed")
    rx > 0 -> L("orb_down")
    tx > 0 -> L("orb_up")
    else -> L("orb_idle")
}

// ---------------------------------------------------------------------------- health row

@Composable
private fun HealthRow(name: String, value: String, state: String, color: Color) {
    Row(verticalAlignment = Alignment.CenterVertically) {
        Text(name, color = NpColors.Muted, fontSize = 11.sp, modifier = Modifier.weight(1f))
        Text(value, color = NpColors.Text, fontSize = 12.sp, fontWeight = FontWeight.Bold)
        Spacer(Modifier.size(8.dp))
        Box(
            Modifier
                .clip(RoundedCornerShape(50))
                .background(color.copy(alpha = 0.16f))
                .padding(horizontal = 8.dp, vertical = 3.dp)
        ) {
            Text(state, color = color, fontSize = 9.sp, fontWeight = FontWeight.Bold)
        }
    }
}
