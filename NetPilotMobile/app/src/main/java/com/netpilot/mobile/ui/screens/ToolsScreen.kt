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
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.OutlinedTextFieldDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.netpilot.mobile.data.L
import com.netpilot.mobile.data.Repo
import com.netpilot.mobile.data.Strings
import com.netpilot.mobile.net.DnsQuery
import com.netpilot.mobile.net.Monitor
import com.netpilot.mobile.ui.components.Gap
import com.netpilot.mobile.ui.components.NpButton
import com.netpilot.mobile.ui.components.NpCard
import com.netpilot.mobile.ui.components.NpChip
import com.netpilot.mobile.ui.components.SectionTitle
import com.netpilot.mobile.ui.theme.NpColors
import com.netpilot.mobile.vpn.NetPilotVpnService
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.net.InetSocketAddress
import java.net.Socket

/**
 * Network Tools — everything here performs a real operation:
 *
 *  - **Ping**: N TCP connects to the host (443 then 80), reporting min/avg/max/loss.
 *    Plain ICMP needs raw sockets which Android does not grant to normal apps, so the
 *    TCP-probe fallback is used deliberately (and labelled as such).
 *  - **NSLookup**: a genuine DNS query through [DnsQuery] against the resolver in use.
 *  - **Flush DNS**: clears the local forwarder cache.
 *  - **Reset network**: resets the monitor's session counters, ping window and link state.
 */
@Composable
fun ToolsScreen() {
    val ctx = LocalContext.current
    val scope = rememberCoroutineScope()

    var host by remember { mutableStateOf("google.com") }
    var output by remember { mutableStateOf("") }
    var running by remember { mutableStateOf(false) }

    fun run(block: suspend () -> String) {
        if (running) return
        running = true
        output = ""
        scope.launch {
            val text = try {
                block()
            } catch (t: Throwable) {
                "${Strings.raw("err_unknown")}: ${t.javaClass.simpleName}"
            }
            output = text
            running = false
        }
    }

    Column(
        Modifier
            .fillMaxSize()
            .background(NpColors.Bg)
            .statusBarsPadding()
            .verticalScroll(rememberScrollState())
            .padding(horizontal = 12.dp, vertical = 10.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp)
    ) {

        // ------------------------------------------------------------------ host
        NpCard {
            SectionTitle(L("tools_title"))
            OutlinedTextField(
                value = host,
                onValueChange = { host = it.trim() },
                modifier = Modifier.fillMaxWidth(),
                singleLine = true,
                label = { Text(L("tools_host"), color = NpColors.Muted, fontSize = 11.sp) },
                textStyle = androidx.compose.material3.LocalTextStyle.current.copy(
                    color = NpColors.Text, fontSize = 14.sp
                ),
                colors = OutlinedTextFieldDefaults.colors(
                    focusedBorderColor = NpColors.Accent,
                    unfocusedBorderColor = NpColors.Stroke,
                    focusedContainerColor = NpColors.BgElevated,
                    unfocusedContainerColor = NpColors.BgElevated,
                    cursorColor = NpColors.Accent
                )
            )
            Gap(10)
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                NpButton(
                    text = L("tools_ping"),
                    enabled = !running,
                    modifier = Modifier.weight(1f)
                ) {
                    val target = host.ifBlank { "snapp.ir" }
                    run { pingText(target) }
                }
                NpButton(
                    text = L("tools_nslookup"),
                    enabled = !running,
                    modifier = Modifier.weight(1f)
                ) {
                    val target = host.ifBlank { "www.google.com" }
                    run { nslookupText(target) }
                }
            }
            Gap(8)
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                GhostAction(L("tools_flush"), enabled = !running) {
                    NetPilotVpnService.flushCache(ctx)
                    Repo.log("evt_net_reset", Strings.raw("tools_flushed"))
                    output = Strings.raw("tools_flushed")
                }
                GhostAction(L("tools_reset_net"), enabled = !running) {
                    Monitor.resetSession()
                    Monitor.refreshLink()
                    Repo.log("evt_net_reset")
                    output = Strings.raw("tools_renewed")
                }
            }
        }

        // ------------------------------------------------------------------ output
        NpCard {
            Row(verticalAlignment = Alignment.CenterVertically) {
                SectionTitle(L("tools_output"), Modifier.weight(1f))
                Text(
                    if (running) L("tools_running") else L("tools_done"),
                    color = if (running) NpColors.Warn else NpColors.Ok,
                    fontSize = 10.sp
                )
            }
            if (output.isBlank()) {
                Text(L("tools_empty"), color = NpColors.Muted, fontSize = 12.sp)
            } else {
                Text(
                    output,
                    color = NpColors.Text,
                    fontSize = 11.sp,
                    fontFamily = FontFamily.Monospace,
                    lineHeight = 16.sp,
                    modifier = Modifier
                        .fillMaxWidth()
                        .clip(RoundedCornerShape(10.dp))
                        .background(NpColors.BgElevated)
                        .padding(10.dp)
                )
            }
        }

        Gap(4)
    }
}

@Composable
private fun androidx.compose.foundation.layout.RowScope.GhostAction(
    text: String,
    enabled: Boolean = true,
    onClick: () -> Unit
) {
    com.netpilot.mobile.ui.components.GhostButton(
        text = text,
        modifier = Modifier.weight(1f),
        accent = if (enabled) NpColors.Accent else NpColors.Muted,
        onClick = { if (enabled) onClick() }
    )
}

// ---------------------------------------------------------------------------- ping

private suspend fun pingText(target: String): String = withContext(Dispatchers.IO) {
    val ports = intArrayOf(443, 80, 53)
    val times = ArrayList<Int>(6)
    val sb = StringBuilder()
    sb.append("TCP ping → $target\n")

    repeat(6) { i ->
        var ok = -1
        var usedPort = 0
        for (p in ports) {
            val start = System.nanoTime()
            try {
                Socket().use { s ->
                    s.connect(InetSocketAddress(target, p), 1500)
                }
                ok = ((System.nanoTime() - start) / 1_000_000L).toInt()
                usedPort = p
                break
            } catch (t: Throwable) {
                // try the next port
            }
        }
        if (ok >= 0) times.add(ok)
        sb.append(
            "seq=${i + 1} " +
                    (if (ok >= 0) "time=${ok}ms port=$usedPort" else "timeout") + "\n"
        )
    }

    if (times.isEmpty()) {
        sb.append("\n0% received — host unreachable or blocked")
    } else {
        val min = times.min()
        val max = times.max()
        val avg = times.average().toInt()
        val loss = (6 - times.size) * 100 / 6
        sb.append("\nmin=${min}ms avg=${avg}ms max=${max}ms loss=${loss}%")
    }
    sb.toString()
}

// ---------------------------------------------------------------------------- nslookup

private suspend fun nslookupText(target: String): String = withContext(Dispatchers.IO) {
    val label = com.netpilot.mobile.vpn.VpnState.dnsName.value.ifBlank { "system" }
    val entry = com.netpilot.mobile.core.AppGraph.dnsCatalog
        .find(com.netpilot.mobile.vpn.VpnState.dnsId.value)

    // Candidates, best first. The system resolvers are filtered to IPv4: an emulator (and a
    // few real networks) hands out a link-local fec0::/10 resolver with no route to it, and
    // querying that produced a bare "timeout" while the label still claimed another server.
    val candidates = LinkedHashSet<String>()
    entry?.primary?.let { candidates.add(it) }
    entry?.secondary?.takeIf { it.isNotBlank() }?.let { candidates.add(it) }
    com.netpilot.mobile.net.Monitor.link.value.dnsServers
        .filter { it.isNotBlank() && ':' !in it }
        .sortedBy { if (com.netpilot.mobile.net.DnsQuery.isPrivate(it)) 1 else 0 }
        .forEach { candidates.add(it) }
    candidates.add("1.1.1.1")

    // Probe once to pick a server that actually answers, then report against that one.
    val probe = candidates.firstNotNullOfOrNull { srv ->
        val r = DnsQuery.query(srv, target, DnsQuery.TYPE_A, 2000)
        if (r.ok || r.answerCount > 0 || r.rcode == 0) srv to r else null
    }
    val server = probe?.first ?: candidates.first()

    val sb = StringBuilder()
    sb.append("Server : $server ($label)\n")

    for ((name, type) in listOf(
        "A" to DnsQuery.TYPE_A,
        "AAAA" to DnsQuery.TYPE_AAAA,
        "MX" to DnsQuery.TYPE_MX,
        "NS" to DnsQuery.TYPE_NS
    )) {
        val r = DnsQuery.query(server, target, type, 2500)
        sb.append("\n$name : ")
        sb.append(
            when {
                !r.ok && r.error.isNotBlank() -> "error ${r.error}"
                r.rcode != 0 -> "rcode ${r.rcode}"
                r.answerCount == 0 -> "(no records)"
                else -> {
                    val parsed = DnsQuery.parse(r.raw)
                    parsed.answers.filter { it.type == type }
                        .joinToString("\n        ") { "${it.data}  ttl=${it.ttl}" }
                }
            }
        )
        sb.append("   [${r.ms} ms]")
    }
    sb.toString()
}
