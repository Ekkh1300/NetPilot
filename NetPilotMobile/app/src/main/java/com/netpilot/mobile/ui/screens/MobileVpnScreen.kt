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
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.OutlinedTextFieldDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.netpilot.mobile.data.L
import com.netpilot.mobile.data.Repo
import com.netpilot.mobile.data.Strings
import com.netpilot.mobile.net.Monitor
import com.netpilot.mobile.pc.PcBridge
import com.netpilot.mobile.pc.ProxyServer
import com.netpilot.mobile.ui.components.Gap
import com.netpilot.mobile.ui.components.GhostButton
import com.netpilot.mobile.ui.components.NpButton
import com.netpilot.mobile.ui.components.NpCard
import com.netpilot.mobile.ui.components.SectionTitle
import com.netpilot.mobile.ui.components.StatTile
import com.netpilot.mobile.ui.components.SwitchRow
import com.netpilot.mobile.ui.theme.NpColors
import com.netpilot.mobile.vpn.VpnState
import kotlinx.coroutines.launch

/**
 * Mobile VPN → PC — pair with NetPilot on Windows (API v1, port 8787) and optionally
 * publish this phone's local HTTP proxy so the PC can ride the phone's tunnel.
 */
@Composable
fun MobileVpnScreen() {
    val ctx = LocalContext.current
    val scope = rememberCoroutineScope()

    val pc by PcBridge.state.collectAsState()
    val vpnMode by VpnState.mode.collectAsState()
    val traffic by Monitor.traffic.collectAsState()

    var host by remember(pc.host) { mutableStateOf(pc.host) }
    var port by remember(pc.port) { mutableStateOf(pc.port.toString()) }
    var code by remember { mutableStateOf("") }
    var statusText by remember { mutableStateOf("") }
    var proxyOn by remember { mutableStateOf(ProxyServer.isRunning) }

    // The link is kept alive by [com.netpilot.mobile.App], which watches `paired` for the
    // whole process — a screen-level effect would stop reporting the moment you navigate away.

    Column(
        Modifier
            .fillMaxSize()
            .background(NpColors.Bg)
            .statusBarsPadding()
            .verticalScroll(rememberScrollState())
            .padding(horizontal = 12.dp, vertical = 10.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp)
    ) {

        // ------------------------------------------------------------------ about
        NpCard {
            SectionTitle(L("mv_title"))
            Text(L("mv_desc"), color = NpColors.Muted, fontSize = 12.sp, lineHeight = 17.sp)
            Gap(8)
            Text(L("mv_how"), color = NpColors.Text, fontSize = 12.sp, fontWeight = FontWeight.Bold)
            Gap(4)
            listOf("mv_step1", "mv_step2", "mv_step3").forEach {
                Text(L(it), color = NpColors.Muted, fontSize = 11.sp, lineHeight = 16.sp)
            }
            Gap(6)
            Text(L("mv_hint_code"), color = NpColors.Accent, fontSize = 11.sp)
        }

        // ------------------------------------------------------------------ target
        NpCard {
            SectionTitle(L("mv_pc_host"))
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                OutlinedField(
                    L("mv_pc_host"),
                    host,
                    { host = it },
                    Modifier.weight(2f)
                )
                OutlinedField(
                    L("mv_pc_port"),
                    port,
                    { port = it.filter { c -> c.isDigit() } },
                    Modifier.weight(1f)
                )
            }
            Gap(8)
            // The path most people take: let the phone find the PC instead of asking for
            // an address nobody knows by heart.
            NpButton(
                text = L("mv_find"),
                modifier = Modifier.fillMaxWidth(),
                enabled = !pc.busy
            ) {
                PcBridge.setTarget(host, port.toIntOrNull() ?: PcBridge.DEFAULT_PORT)
                scope.launch {
                    statusText = Strings.raw("mv_finding")
                    val found = PcBridge.discover()
                    host = PcBridge.state.value.host
                    statusText = if (found != null) {
                        Strings.raw("mv_found").replace("%1\$s", found)
                    } else {
                        PcBridge.state.value.lastError.ifBlank { Strings.raw("mv_not_found") }
                    }
                }
            }
            Gap(8)
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                NpButton(
                    text = L("mv_connect"),
                    modifier = Modifier.weight(1f),
                    enabled = !pc.busy
                ) {
                    PcBridge.setTarget(host, port.toIntOrNull() ?: PcBridge.DEFAULT_PORT)
                    scope.launch {
                        statusText = if (PcBridge.ping()) Strings.raw("adp_active") else Strings.raw("mv_error")
                    }
                }
                GhostButton(
                    text = L("mv_report"),
                    modifier = Modifier.weight(1f)
                ) {
                    PcBridge.setTarget(host, port.toIntOrNull() ?: PcBridge.DEFAULT_PORT)
                    scope.launch { PcBridge.status() }
                }
            }
            if (statusText.isNotBlank()) {
                Gap(6)
                Text(statusText, color = NpColors.Muted, fontSize = 11.sp)
            }
        }

        // ------------------------------------------------------------------ pairing
        NpCard {
            Row(verticalAlignment = Alignment.CenterVertically) {
                SectionTitle(L("mv_pair_code"), Modifier.weight(1f))
                Text(
                    if (pc.paired) L("mv_paired") else L("mv_not_paired"),
                    color = if (pc.paired) NpColors.Ok else NpColors.Muted,
                    fontSize = 11.sp
                )
            }

            if (pc.paired) {
                Text(
                    pc.deviceName.ifBlank { pc.host },
                    color = NpColors.Text,
                    fontSize = 13.sp,
                    fontWeight = FontWeight.SemiBold
                )
                Gap(4)
                Text(
                    "${L("mv_pc_host")}: ${pc.host}:${pc.port}",
                    color = NpColors.Muted,
                    fontSize = 11.sp
                )
                Gap(8)
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    GhostButton(
                        text = L("mv_unpair"),
                        modifier = Modifier.weight(1f),
                        accent = NpColors.Bad
                    ) { scope.launch { PcBridge.unpair() } }
                    NpButton(
                        text = L("mv_report"),
                        modifier = Modifier.weight(1f)
                    ) { scope.launch { PcBridge.status() } }
                }
            } else {
                OutlinedField(L("mv_pair_code"), code, { code = it.uppercase() })
                Gap(8)
                NpButton(
                    text = L("mv_pair"),
                    modifier = Modifier.fillMaxWidth(),
                    enabled = !pc.busy && code.isNotBlank()
                ) {
                    scope.launch {
                        PcBridge.setTarget(host, port.toIntOrNull() ?: PcBridge.DEFAULT_PORT)
                        val ok = PcBridge.pair(code)
                        if (ok) Repo.log("evt_pc_pair", pc.host)
                        statusText = if (ok) Strings.raw("mv_paired") else Strings.raw("mv_error")
                    }
                }
            }

            if (pc.lastError.isNotBlank()) {
                Gap(6)
                Text(pc.lastError, color = NpColors.Bad, fontSize = 11.sp, lineHeight = 16.sp)
                // "Unreachable" is the one error the user cannot act on from this screen, so
                // spell out the three things that actually cause it - and keep the port right
                // here, because 8788 (this phone's own proxy) is the wrong one for the bridge.
                if (pc.lastError == Strings.raw("err_unknown") && !pc.reachable) {
                    Gap(6)
                    Text(
                        L("mv_troubleshoot"),
                        color = NpColors.Muted, fontSize = 11.sp, fontWeight = FontWeight.SemiBold
                    )
                    listOf("mv_ts1", "mv_ts2", "mv_ts3").forEach {
                        Text(
                            L(it),
                            color = NpColors.Muted, fontSize = 10.sp, lineHeight = 15.sp
                        )
                    }
                }
            }
        }

        // ------------------------------------------------------------------ live status
        if (pc.paired) {
            NpCard {
                SectionTitle(L("adp_status"))
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    StatTile(
                        label = L("adp_status"),
                        value = if (pc.reachable) L("adp_active") else L("adp_inactive"),
                        accent = if (pc.reachable) NpColors.Ok else NpColors.Bad,
                        modifier = Modifier.weight(1f)
                    )
                    StatTile(
                        label = L("adp_vpn"),
                        value = if (pc.pcVpnActive) L("yes") else L("no"),
                        accent = if (pc.pcVpnActive) NpColors.Warn else NpColors.Muted,
                        modifier = Modifier.weight(1f)
                    )
                }
                Gap(8)
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    StatTile(
                        label = "↓",
                        value = com.netpilot.mobile.data.formatRate(pc.rateDown),
                        accent = NpColors.Accent,
                        modifier = Modifier.weight(1f)
                    )
                    StatTile(
                        label = "↑",
                        value = com.netpilot.mobile.data.formatRate(pc.rateUp),
                        accent = NpColors.Chart2,
                        modifier = Modifier.weight(1f)
                    )
                }
                if (pc.links.isNotBlank()) {
                    Gap(8)
                    Text(pc.links, color = NpColors.Muted, fontSize = 10.sp)
                }

                Gap(10)
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    GhostButton(
                        text = L("backup_short"),
                        modifier = Modifier.weight(1f)
                    ) { scope.launch { PcBridge.backup() } }
                    GhostButton(
                        text = L("restore_short"),
                        modifier = Modifier.weight(1f)
                    ) { scope.launch { PcBridge.restore() } }
                }
                Gap(6)
                Text(L("mv_snapshot_hint"), color = NpColors.Muted, fontSize = 10.sp)
                Gap(3)
                Text(L("mv_no_consent"), color = NpColors.Muted, fontSize = 10.sp)
                Gap(3)
                Text(L("mv_link_note"), color = NpColors.Muted, fontSize = 10.sp, lineHeight = 14.sp)
            }
        }

        // ------------------------------------------------------------------ proxy share
        // Turning the local proxy on is only half the job: the desktop merely *stores* the
        // address a /report carries, so it has to be told to point WinHTTP at us with an
        // explicit /share — otherwise nothing at all changes on the PC.
        val beginShare: () -> Unit = {
            val ok = ProxyServer.start(ProxyServer.DEFAULT_PORT)
            proxyOn = ok
            if (ok) {
                scope.launch {
                    if (PcBridge.state.value.paired) {
                        val shared = PcBridge.share(ProxyServer.endpoint())
                        statusText = if (shared) Strings.raw("mv_shared")
                        else PcBridge.state.value.lastError
                            .ifBlank { Strings.raw("mv_share_failed") }
                    } else {
                        PcBridge.report("")
                    }
                }
            } else {
                statusText = Strings.raw("err_unknown")
            }
        }
        val endShare: () -> Unit = {
            ProxyServer.stop()
            proxyOn = false
            scope.launch {
                if (PcBridge.state.value.paired) PcBridge.stopShare()
                PcBridge.report("")
            }
        }

        NpCard {
            SectionTitle(L("mv_proxy"))
            SwitchRow(
                label = if (proxyOn) L("mv_stop_share") else L("mv_share"),
                checked = proxyOn,
                onCheckedChange = { want -> if (want) beginShare() else endShare() },
                sub = if (proxyOn)
                    "${L("mv_proxy")}: ${ProxyServer.endpoint().ifBlank { "…" }}"
                else L("mv_waiting")
            )
            if (proxyOn) {
                Gap(8)
                val vpnActive = vpnMode != VpnState.Mode.OFF
                Text(
                    "${L("dash_vpn_on")}: ${if (vpnActive) L("yes") else L("no")} · " +
                            "${L("mon_session")}: ${traffic.sessionRx / 1024} KB",
                    color = if (vpnActive) NpColors.Ok else NpColors.Muted,
                    fontSize = 11.sp
                )
                Gap(6)
                Text(
                    "proxy://127.0.0.1:${ProxyServer.port}  ·  " +
                            "LAN: ${ProxyServer.endpoint()}",
                    color = NpColors.Accent,
                    fontSize = 11.sp
                )
                Gap(8)
                GhostButton(
                    text = L("mv_stop_share"),
                    modifier = Modifier.fillMaxWidth(),
                    accent = NpColors.Bad
                ) { endShare() }
            } else if (pc.paired) {
                Gap(8)
                NpButton(text = L("mv_share"), modifier = Modifier.fillMaxWidth()) {
                    beginShare()
                }
            }
        }

        Gap(4)
    }
}

@Composable
private fun OutlinedField(
    label: String,
    value: String,
    onChange: (String) -> Unit,
    modifier: Modifier = Modifier
) {
    OutlinedTextField(
        value = value,
        onValueChange = onChange,
        modifier = modifier.fillMaxWidth(),
        singleLine = true,
        label = { Text(label, color = NpColors.Muted, fontSize = 11.sp) },
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
}
