package com.netpilot.mobile.ui.screens

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
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
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.netpilot.mobile.core.AppGraph
import com.netpilot.mobile.data.L
import com.netpilot.mobile.data.Repo
import com.netpilot.mobile.net.Monitor
import com.netpilot.mobile.ui.components.Gap
import com.netpilot.mobile.ui.components.GhostButton
import com.netpilot.mobile.ui.components.NpButton
import com.netpilot.mobile.ui.components.NpCard
import com.netpilot.mobile.ui.components.SectionTitle
import com.netpilot.mobile.ui.components.StatTile
import com.netpilot.mobile.ui.theme.NpColors
import com.netpilot.mobile.vpn.NetPilotVpnService
import com.netpilot.mobile.vpn.VpnState
import kotlinx.coroutines.launch

/**
 * Smart DNS — resolves what the network is actually using today and proposes the
 * fastest resolver from the last real benchmark. Both figures are measured, not guessed.
 */
@Composable
fun SmartDnsScreen() {
    val ctx = LocalContext.current
    val catalog = AppGraph.dnsCatalog

    val link by Monitor.link.collectAsState()
    val dnsMs by Monitor.dnsMs.collectAsState()
    val results by AppGraph.benchmark.results.collectAsState()
    // Observed, not read: comparing against VpnState.dnsId.value would only be evaluated
    // whenever *this* screen recomposes, so "Apply" could linger after a switch elsewhere.
    val activeDnsId by VpnState.dnsId.collectAsState()

    // What the network handed out: match the system servers against the catalog.
    val detected = link.dnsServers.firstNotNullOfOrNull { catalog.byServer(it) }
    val detectedServer = link.dnsServers.firstOrNull() ?: "—"

    // Best measured resolver on this network (loss-free entries only).
    val best = results.filter { it.avgMs > 0 && it.lossPct < 100.0 }.minByOrNull { it.avgMs }
    val currentEntry = catalog.find(catalog.selectedId)
    val currentResult = currentEntry?.let { c -> results.firstOrNull { it.entry.id == c.id } }
    val gain = if (currentResult != null && best != null)
        currentResult.avgMs - best.avgMs else null

    Column(
        Modifier
            .fillMaxSize()
            .background(NpColors.Bg)
            .statusBarsPadding()
            .verticalScroll(rememberScrollState())
            .padding(horizontal = 12.dp, vertical = 10.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp)
    ) {

        // ------------------------------------------------------------ detection
        NpCard {
            SectionTitle(L("smart_detect"))
            Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                StatTile(
                    label = L("adp_dns"),
                    value = detected?.name ?: detectedServer,
                    sub = detectedServer,
                    accent = if (detected != null) NpColors.Ok else NpColors.Text,
                    modifier = Modifier.weight(1f)
                )
                StatTile(
                    label = L("hlt_dns"),
                    value = if (dnsMs < 0) "—" else "$dnsMs ms",
                    sub = L("mon_live"),
                    accent = NpColors.Accent,
                    modifier = Modifier.weight(1f)
                )
            }
            Gap(8)
            if (detected == null && link.dnsServers.isNotEmpty()) {
                Text(
                    L("smart_no_data"),
                    color = NpColors.Muted,
                    fontSize = 11.sp
                )
                Gap(6)
            }
            GhostButton(L("refresh")) { Monitor.refreshLink() }
        }

        // ------------------------------------------------------------ suggestion
        NpCard {
            SectionTitle(L("smart_suggest"))
            if (best == null) {
                Text(L("smart_no_data"), color = NpColors.Muted, fontSize = 13.sp)
                Gap(8)
                NpButton(text = L("bench_run")) {
                    // A benchmark is the only honest way to suggest a resolver.
                    val entries = catalog.all()
                    kotlinx.coroutines.CoroutineScope(kotlinx.coroutines.Dispatchers.IO)
                        .launch {
                            AppGraph.benchmark.run(entries, rounds = 3)
                        }
                }
            } else {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Column(Modifier.weight(1f)) {
                        Text(
                            best.entry.name,
                            color = NpColors.Ok,
                            fontSize = 17.sp,
                            fontWeight = FontWeight.Bold
                        )
                        Gap(3)
                        Text(
                            best.entry.servers.joinToString(" · "),
                            color = NpColors.Muted,
                            fontSize = 11.sp
                        )
                        Gap(4)
                        Text(
                            "${L("bench_avg")}: ${best.avgMs} ms · " +
                                    "${L("bench_jitter")}: ${best.jitterMs} ms · " +
                                    "${L("bench_loss")}: ${"%.0f".format(best.lossPct)}%",
                            color = NpColors.Muted,
                            fontSize = 10.sp
                        )
                    }
                    if (best.entry.id != activeDnsId) {
                        NpButton(text = L("apply")) {
                            NetPilotVpnService.applyDns(ctx, best.entry.id)
                            Repo.log("evt_dns_changed", best.entry.name)
                        }
                    }
                }

                Gap(8)
                Text(L("smart_suggestion_hint"), color = NpColors.Muted, fontSize = 11.sp)

                if (gain != null && gain > 0) {
                    Gap(8)
                    Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                        StatTile(
                            label = "Δ",
                            value = "$gain ms",
                            accent = NpColors.Ok,
                            modifier = Modifier.weight(1f)
                        )
                        StatTile(
                            label = L("bench_avg"),
                            value = "${best.avgMs} ms",
                            accent = NpColors.Accent,
                            modifier = Modifier.weight(1f)
                        )
                    }
                }
            }
        }

        Gap(4)
    }
}
