package com.netpilot.mobile.ui.screens

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.OutlinedTextFieldDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.netpilot.mobile.core.AppGraph
import com.netpilot.mobile.data.DnsEntry
import com.netpilot.mobile.data.L
import com.netpilot.mobile.data.Repo
import com.netpilot.mobile.data.Strings
import com.netpilot.mobile.ui.Routes
import com.netpilot.mobile.ui.components.EmptyState
import com.netpilot.mobile.ui.components.Gap
import com.netpilot.mobile.ui.components.GhostButton
import com.netpilot.mobile.ui.components.NpButton
import com.netpilot.mobile.ui.components.NpCard
import com.netpilot.mobile.ui.components.NpChip
import com.netpilot.mobile.ui.components.SectionTitle
import com.netpilot.mobile.ui.components.SwitchRow
import com.netpilot.mobile.ui.theme.NpColors
import com.netpilot.mobile.vpn.NetPilotVpnService
import com.netpilot.mobile.vpn.VpnState

private enum class DnsTab { PROVIDERS, FAVORITES, CUSTOM }

/**
 * DNS Manager — pick, favourite, apply or restore a resolver.
 * Applying runs through the local tunnel ([NetPilotVpnService.applyDns]), so no root and
 * no system setting is touched; "Restore" brings the system DNS back.
 */
@Composable
fun DnsScreen(onOpen: (String) -> Unit) {
    val ctx = LocalContext.current
    val catalog = AppGraph.dnsCatalog

    val mode by VpnState.mode.collectAsState()
    val activeId by VpnState.dnsId.collectAsState()
    val link by com.netpilot.mobile.net.Monitor.link.collectAsState()

    var tab by remember { mutableStateOf(DnsTab.PROVIDERS) }
    var query by remember { mutableStateOf("") }
    var favs by remember { mutableStateOf(catalog.favorites()) }
    var showAdd by remember { mutableStateOf(false) }

    val customs = remember(showAdd, catalog.custom().size, query) { catalog.custom() }

    Column(
        Modifier
            .fillMaxSize()
            .background(NpColors.Bg)
            .statusBarsPadding()
            .verticalScroll(rememberScrollState())
            .padding(horizontal = 12.dp, vertical = 10.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp)
    ) {

        // ------------------------------------------------------------------ current
        NpCard {
            SectionTitle(L("dns_current"))
            val active = catalog.find(activeId)
            Row(verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text(
                        active?.name ?: L("dash_dns_auto"),
                        color = if (mode != VpnState.Mode.OFF) NpColors.Ok else NpColors.Text,
                        fontSize = 16.sp,
                        fontWeight = FontWeight.Bold
                    )
                    Gap(2)
                    Text(
                        when {
                            active != null -> active.servers.joinToString(" · ")
                            link.dnsServers.isNotEmpty() -> link.dnsServers.joinToString(" · ")
                            else -> "—"
                        },
                        color = NpColors.Muted,
                        fontSize = 11.sp
                    )
                }
                if (mode != VpnState.Mode.OFF) {
                    GhostButton(L("stop"), danger = true) {
                        NetPilotVpnService.restoreSystemDns(ctx)
                        Repo.log("evt_dns_restored")
                    }
                }
            }
            Gap(8)
            Text(L("dns_tunnel_hint"), color = NpColors.Muted, fontSize = 11.sp)
            Gap(6)
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                NpButton(
                    text = L("menu_benchmark"),
                    modifier = Modifier.weight(1f),
                    onClick = { onOpen(Routes.BENCH) }
                )
                GhostButton(
                    text = L("menu_smart_dns"),
                    modifier = Modifier.weight(1f)
                ) { onOpen(Routes.SMART) }
            }
        }

        // ------------------------------------------------------------------ search + tabs
        OutlinedTextField(
            value = query,
            onValueChange = { query = it },
            modifier = Modifier.fillMaxWidth(),
            singleLine = true,
            placeholder = { Text(L("search"), color = NpColors.Muted, fontSize = 13.sp) },
            textStyle = androidx.compose.material3.LocalTextStyle.current.copy(
                color = NpColors.Text, fontSize = 13.sp
            ),
            colors = OutlinedTextFieldDefaults.colors(
                focusedBorderColor = NpColors.Accent,
                unfocusedBorderColor = NpColors.Stroke,
                focusedContainerColor = NpColors.Card,
                unfocusedContainerColor = NpColors.Card,
                cursorColor = NpColors.Accent
            ),
            shape = RoundedCornerShape(14.dp)
        )

        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            NpChip(L("dns_presets"), tab == DnsTab.PROVIDERS, Modifier.weight(1f)) {
                tab = DnsTab.PROVIDERS
            }
            NpChip(L("dns_favorites"), tab == DnsTab.FAVORITES, Modifier.weight(1f)) {
                tab = DnsTab.FAVORITES
            }
            NpChip(L("dns_custom"), tab == DnsTab.CUSTOM, Modifier.weight(1f)) {
                tab = DnsTab.CUSTOM
            }
        }

        // ------------------------------------------------------------------ lists
        when (tab) {
            DnsTab.PROVIDERS -> {
                val list = catalog.presets.filter { matches(it, query) }
                if (list.isEmpty()) EmptyState(L("search"))
                list.forEach { e ->
                    DnsRow(
                        entry = e,
                        active = e.id == activeId,
                        favorite = e.id in favs,
                        onFavorite = {
                            catalog.toggleFavorite(e.id)
                            favs = catalog.favorites()
                        },
                        onApply = {
                            NetPilotVpnService.applyDns(ctx, e.id)
                            Repo.log("evt_dns_changed", e.name)
                        }
                    )
                }
            }

            DnsTab.FAVORITES -> {
                val list = catalog.all().filter { it.id in favs && matches(it, query) }
                if (list.isEmpty()) {
                    EmptyState(L("dns_no_favorites"))
                } else {
                    list.forEach { e ->
                        DnsRow(
                            entry = e,
                            active = e.id == activeId,
                            favorite = true,
                            onFavorite = {
                                catalog.toggleFavorite(e.id)
                                favs = catalog.favorites()
                            },
                            onApply = {
                                NetPilotVpnService.applyDns(ctx, e.id)
                                Repo.log("evt_dns_changed", e.name)
                            }
                        )
                    }
                }
            }

            DnsTab.CUSTOM -> {
                val list = customs.filter { matches(it, query) }
                if (list.isEmpty()) EmptyState(L("dns_custom"))
                list.forEach { e ->
                    DnsRow(
                        entry = e,
                        active = e.id == activeId,
                        favorite = e.id in favs,
                        onFavorite = {
                            catalog.toggleFavorite(e.id)
                            favs = catalog.favorites()
                        },
                        onApply = {
                            NetPilotVpnService.applyDns(ctx, e.id)
                            Repo.log("evt_dns_changed", e.name)
                        },
                        onDelete = { catalog.removeCustom(e.id) }
                    )
                }
                NpButton(text = L("dns_add_custom"), onClick = { showAdd = true })
            }
        }

        Gap(4)
    }

    if (showAdd) {
        AddCustomDnsDialog(
            onDismiss = { showAdd = false },
            onSaved = { name, p, s ->
                catalog.addCustom(name, p, s)
                showAdd = false
            }
        )
    }
}

private fun matches(e: DnsEntry, query: String): Boolean {
    if (query.isBlank()) return true
    val q = query.trim()
    return e.name.contains(q, true) ||
            e.primary.contains(q, true) ||
            e.secondary.contains(q, true) ||
            e.tagEn.contains(q, true) ||
            e.tagFa.contains(q, true)
}

// ---------------------------------------------------------------------------- row

@Composable
private fun DnsRow(
    entry: DnsEntry,
    active: Boolean,
    favorite: Boolean,
    onFavorite: () -> Unit,
    onApply: () -> Unit,
    onDelete: (() -> Unit)? = null
) {
    val accent = if (active) NpColors.Ok else NpColors.Accent
    NpCard(
        borderColor = if (active) NpColors.Ok.copy(alpha = 0.6f) else NpColors.Stroke
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(
                        entry.name,
                        color = if (active) NpColors.Ok else NpColors.Text,
                        fontSize = 14.sp,
                        fontWeight = FontWeight.Bold
                    )
                    if (entry.custom) {
                        Gap(6)
                        Text(
                            L("dns_custom"),
                            color = NpColors.Muted,
                            fontSize = 9.sp,
                            modifier = Modifier
                                .clip(RoundedCornerShape(50))
                                .background(NpColors.CardAlt)
                                .padding(horizontal = 6.dp, vertical = 2.dp)
                        )
                    }
                }
                Gap(3)
                Text(entry.servers.joinToString(" · "), color = NpColors.Muted, fontSize = 11.sp)
                val tag = if (com.netpilot.mobile.data.Strings.lang == "fa") entry.tagFa else entry.tagEn
                if (tag.isNotBlank()) {
                    Gap(2)
                    Text(tag, color = accent, fontSize = 10.sp)
                }
            }

            Column(horizontalAlignment = Alignment.End) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(
                        if (favorite) "★" else "☆",
                        color = if (favorite) NpColors.Warn else NpColors.Muted,
                        fontSize = 20.sp,
                        modifier = Modifier
                            .clip(RoundedCornerShape(50))
                            .clickable(onClick = onFavorite)
                            .padding(horizontal = 4.dp)
                    )
                    if (onDelete != null) {
                        Text(
                            "✕",
                            color = NpColors.Bad,
                            fontSize = 16.sp,
                            modifier = Modifier
                                .clip(RoundedCornerShape(50))
                                .clickable(onClick = onDelete)
                                .padding(horizontal = 6.dp, vertical = 2.dp)
                        )
                    }
                }
                Gap(5)
                if (active) {
                    Text(L("dash_active_dns"), color = NpColors.Ok, fontSize = 11.sp)
                } else {
                    NpChip(L("apply"), selected = false, accent = accent, onClick = onApply)
                }
            }
        }
    }
}

// ---------------------------------------------------------------------------- add dialog

@Composable
private fun AddCustomDnsDialog(
    onDismiss: () -> Unit,
    onSaved: (name: String, primary: String, secondary: String) -> Unit
) {
    var name by remember { mutableStateOf("") }
    var primary by remember { mutableStateOf("") }
    var secondary by remember { mutableStateOf("") }
    var error by remember { mutableStateOf("") }

    androidx.compose.material3.AlertDialog(
        onDismissRequest = onDismiss,
        containerColor = NpColors.Card,
        title = { Text(L("dns_add_custom"), color = NpColors.Text) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                DialogField(L("dns_name"), name, { name = it })
                DialogField(L("dns_primary"), primary, { primary = it })
                DialogField(L("dns_secondary"), secondary, { secondary = it })
                if (error.isNotBlank()) Text(error, color = NpColors.Bad, fontSize = 11.sp)
            }
        },
        confirmButton = {
            NpChip(
                L("save"),
                selected = true,
                onClick = {
                    val p = primary.trim()
                    if (!isIp(p)) {
                        error = Strings.raw("dns_invalid_ip")
                        return@NpChip
                    }
                    val s = secondary.trim()
                    if (s.isNotEmpty() && !isIp(s)) {
                        error = Strings.raw("dns_invalid_ip")
                        return@NpChip
                    }
                    onSaved(name.trim().ifBlank { p }, p, s)
                }
            )
        },
        dismissButton = {
            NpChip(L("cancel"), selected = false, onClick = onDismiss)
        }
    )
}

@Composable
private fun DialogField(label: String, value: String, onChange: (String) -> Unit) {
    OutlinedTextField(
        value = value,
        onValueChange = onChange,
        modifier = Modifier.fillMaxWidth(),
        singleLine = true,
        label = { Text(label, color = NpColors.Muted, fontSize = 12.sp) },
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

/** IPv4 or a plain IPv6 literal — deliberately permissive but never garbage. */
internal fun isIp(v: String): Boolean {
    if (v.isBlank()) return false
    if (v.contains(':')) {
        return v.isNotEmpty() && v.all { it.isDigit() || it in "abcdefABCDEF.:" } &&
                v.contains(':')
    }
    val parts = v.split('.')
    if (parts.size != 4) return false
    return parts.all { p ->
        p.isNotEmpty() && p.length <= 3 && p.all { it.isDigit() } &&
                (p.toIntOrNull() ?: -1) in 0..255
    }
}
