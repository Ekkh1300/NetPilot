package com.netpilot.mobile.ui.screens

import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
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
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.key
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
import com.netpilot.mobile.data.L
import com.netpilot.mobile.data.LimitRule
import com.netpilot.mobile.data.Repo
import com.netpilot.mobile.data.RuleMode
import com.netpilot.mobile.data.Strings
import com.netpilot.mobile.net.Monitor
import com.netpilot.mobile.ui.components.EmptyState
import com.netpilot.mobile.ui.components.Gap
import com.netpilot.mobile.ui.components.GhostButton
import com.netpilot.mobile.ui.components.NpButton
import com.netpilot.mobile.ui.components.NpCard
import com.netpilot.mobile.ui.components.NpChip
import com.netpilot.mobile.ui.components.SearchField
import com.netpilot.mobile.ui.components.SectionTitle
import com.netpilot.mobile.ui.components.SwitchRow
import com.netpilot.mobile.ui.theme.NpColors
import com.netpilot.mobile.vpn.FirewallStats
import com.netpilot.mobile.vpn.NetPilotVpnService
import com.netpilot.mobile.vpn.VpnState
import kotlinx.coroutines.delay
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * Net Limiter — per-app ALLOW / LIMIT / BLOCK rules with real byte caps.
 * The rules live in [Repo]; the enforcement engine reads them through
 * [Repo.effectiveRules] (schedules included) and applies them inside the tunnel.
 */
@Composable
fun LimiterScreen() {
    val ctx = LocalContext.current

    val rules by Repo.rules.collectAsState()
    val mode by VpnState.mode.collectAsState()
    val appRates by Monitor.appRates.collectAsState()

    var query by remember { mutableStateOf("") }
    var editing by remember { mutableStateOf<LimitRule?>(null) }
    var showPicker by remember { mutableStateOf(false) }

    val engineOn = mode != VpnState.Mode.OFF
    val firewallOn = mode == VpnState.Mode.FIREWALL

    // Packages that actually moved data — the only honest list to limit.
    val packages = remember(appRates, rules) {
        val fromRates = appRates.entries.map { (pkg, v) ->
            pkg to (v.first + v.second)
        }
        val known = Repo.effectiveRules()
        (fromRates + known.values.map { it.pkg to 1L })
            .distinctBy { it.first }
            .sortedByDescending { it.second }
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

        // ------------------------------------------------------------------ engine
        NpCard {
            SectionTitle(L("lim_title"))
            Row(verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text(
                        if (engineOn) L("lim_engine_on") else L("lim_engine_off"),
                        color = if (engineOn) NpColors.Ok else NpColors.Muted,
                        fontSize = 13.sp,
                        fontWeight = FontWeight.SemiBold
                    )
                    Gap(3)
                    Text(
                        L("lim_rules_active").replace("%1\$s", "${rules.size}"),
                        color = NpColors.Muted,
                        fontSize = 11.sp
                    )
                }
                if (engineOn) {
                    NpChip(L("running"), selected = true, accent = NpColors.Ok) { }
                }
            }
            Gap(6)
            Text(L("lim_block_hint"), color = NpColors.Muted, fontSize = 11.sp)
            Gap(8)
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                NpButton(
                    text = L("add"),
                    modifier = Modifier.weight(1f)
                ) { showPicker = true }
                GhostButton(
                    text = L("clear"),
                    danger = true,
                    modifier = Modifier.weight(1f)
                ) {
                    // Clears the rules only — history, profiles and DNS stay untouched.
                    rules.forEach { Repo.removeRule(it.pkg) }
                }
            }
        }

        // ------------------------------------------------------------------ firewall engine
        FirewallCard(firewallOn = firewallOn, engineOn = engineOn, ruleCount = rules.size)

        // ------------------------------------------------------------------ search
        SearchField(query, { query = it }, L("search"))

        // ------------------------------------------------------------------ rules
        if (rules.isEmpty()) {
            NpCard { EmptyState(L("lim_empty")) }
        } else {
            val filtered = rules.filter {
                query.isBlank() || it.label.contains(query, true) || it.pkg.contains(query, true)
            }
            filtered.forEach { rule ->
                RuleRow(
                    rule = rule,
                    rate = appRates[rule.pkg],
                    engineOn = engineOn,
                    onEdit = { editing = rule },
                    onDelete = { Repo.removeRule(rule.pkg) }
                )
            }
        }

        // ------------------------------------------------------------------ packages hint
        if (packages.isNotEmpty()) {
            NpCard {
                SectionTitle(L("apps_process"))
                Text(
                    packages.take(8).joinToString("  ·  ") { it.first },
                    color = NpColors.Muted,
                    fontSize = 11.sp
                )
            }
        }

        Gap(4)
    }

    editing?.let { rule ->
        RuleEditorDialog(
            initial = rule,
            labelProvider = { pkg -> labelOf(ctx, pkg) },
            onDismiss = { editing = null },
            onSave = { newRule ->
                Repo.setRule(newRule)
                Repo.log(
                    if (newRule.mode == RuleMode.ALLOW) "evt_limit_off" else "evt_limit_on",
                    newRule.label
                )
                editing = null
            },
            onDelete = {
                Repo.removeRule(rule.pkg)
                editing = null
            }
        )
    }

    if (showPicker) {
        AppPickerDialog(
            onDismiss = { showPicker = false },
            onPick = { pkg, label ->
                showPicker = false
                editing = LimitRule(pkg = pkg, label = label, mode = RuleMode.LIMIT, downBps = 512 * 1024)
            }
        )
    }
}

// ------------------------------------------------------------------------ firewall

/**
 * FIREWALL mode control + the counters the engine actually keeps.
 *
 * Per-app attribution needs `ConnectivityManager.getConnectionOwnerUid`, which only exists
 * on Android 10+, so below that the start button stays off and the reason is stated instead
 * of quietly doing nothing.
 */
@Composable
private fun FirewallCard(firewallOn: Boolean, engineOn: Boolean, ruleCount: Int) {
    val ctx = LocalContext.current
    val supported = Build.VERSION.SDK_INT >= NetPilotVpnService.MIN_FIREWALL_SDK

    // The counters are plain volatiles; poll while the engine runs so the card moves.
    var poll by remember { mutableStateOf(0) }
    LaunchedEffect(firewallOn) {
        while (firewallOn) {
            delay(1_000)
            poll++
        }
    }

    val accent = if (firewallOn) NpColors.Accent else NpColors.Stroke

    NpCard(borderColor = accent.copy(alpha = 0.55f)) {
        SectionTitle(L("fw_title"))

        Text(
            when {
                firewallOn -> L("fw_running")
                engineOn -> L("fw_dns_only")
                else -> L("lim_engine_off")
            },
            color = when {
                firewallOn -> NpColors.Ok
                engineOn -> NpColors.Muted
                else -> NpColors.Muted
            },
            fontSize = 13.sp,
            fontWeight = FontWeight.SemiBold
        )

        Gap(4)
        Text(L("fw_note"), color = NpColors.Muted, fontSize = 11.sp)

        if (firewallOn) {
            Gap(8)
            // The counters are plain volatiles, so Compose cannot subscribe to them. The
            // ticker has to be *read* here (keying on it redraws this block every second),
            // otherwise the numbers are drawn once at engine start and stay frozen at 0
            // while the engine keeps counting.
            key(poll) {
                Text(
                    L("fw_blocked_n").replace("%1\$s", "${FirewallStats.blockedPackets}"),
                    color = NpColors.Bad,
                    fontSize = 11.sp
                )
                Text(
                    L("fw_relayed_n").replace("%1\$s", "${FirewallStats.relayedFlows}"),
                    color = NpColors.Muted,
                    fontSize = 11.sp
                )
                if (FirewallStats.unidentifiedFlows > 0) {
                    Text(
                        L("fw_unidentified_n")
                            .replace("%1\$s", "${FirewallStats.unidentifiedFlows}"),
                        color = NpColors.Warn,
                        fontSize = 11.sp
                    )
                }
                if (FirewallStats.attributionBroken) {
                    Gap(6)
                    Text(L("fw_no_attribution"), color = NpColors.Bad, fontSize = 11.sp)
                }
            }
        }

        if (!supported) {
            Gap(6)
            Text(L("fw_needs_api29"), color = NpColors.Warn, fontSize = 11.sp)
        }

        Gap(10)
        if (firewallOn) {
            GhostButton(
                text = L("fw_stop"),
                danger = true,
                modifier = Modifier.fillMaxWidth()
            ) { NetPilotVpnService.stopFirewall(ctx, VpnState.dnsId.value) }
        } else {
            NpButton(
                text = L("fw_start"),
                modifier = Modifier.fillMaxWidth(),
                enabled = supported
            ) { NetPilotVpnService.startFirewall(ctx, VpnState.dnsId.value) }
        }

        Gap(6)
        Text(
            L("lim_rules_active").replace("%1\$s", "$ruleCount"),
            color = NpColors.Muted,
            fontSize = 10.sp
        )
    }
}

// ---------------------------------------------------------------------------- row

@Composable
private fun RuleRow(
    rule: LimitRule,
    rate: Pair<Long, Long>?,
    engineOn: Boolean,
    onEdit: () -> Unit,
    onDelete: () -> Unit
) {
    val accent = when (rule.mode) {
        RuleMode.BLOCK -> NpColors.Bad
        RuleMode.LIMIT -> NpColors.Warn
        RuleMode.ALLOW -> NpColors.Ok
    }
    NpCard(
        borderColor = if (engineOn) accent.copy(alpha = 0.55f) else NpColors.Stroke
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text(
                    rule.label.ifBlank { rule.pkg },
                    color = NpColors.Text,
                    fontSize = 13.sp,
                    fontWeight = FontWeight.SemiBold
                )
                Text(rule.pkg, color = NpColors.Muted, fontSize = 9.sp, maxLines = 1)
                Gap(6)
                Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                    ModeChip(
                        label = L("lim_allow"),
                        selected = rule.mode == RuleMode.ALLOW,
                        accent = NpColors.Ok
                    ) { onEdit() }
                    ModeChip(
                        label = L("lim_limit"),
                        selected = rule.mode == RuleMode.LIMIT,
                        accent = NpColors.Warn
                    ) { onEdit() }
                    ModeChip(
                        label = L("lim_block"),
                        selected = rule.mode == RuleMode.BLOCK,
                        accent = NpColors.Bad
                    ) { onEdit() }
                }
            }
            Column(horizontalAlignment = Alignment.End) {
                when (rule.mode) {
                    RuleMode.LIMIT -> Text(
                        "↓ ${speed(rule.downBps)}\n↑ ${speed(rule.upBps)}",
                        color = NpColors.Warn,
                        fontSize = 11.sp,
                        fontWeight = FontWeight.Bold
                    )
                    RuleMode.BLOCK -> Text(
                        "✕",
                        color = NpColors.Bad,
                        fontSize = 20.sp,
                        fontWeight = FontWeight.Bold
                    )
                    RuleMode.ALLOW -> Text(
                        "✓",
                        color = NpColors.Ok,
                        fontSize = 18.sp,
                        fontWeight = FontWeight.Bold
                    )
                }
                if (rate != null) {
                    Text(
                        "↓ ${com.netpilot.mobile.data.formatRate(rate.first)}",
                        color = NpColors.Muted,
                        fontSize = 9.sp
                    )
                }
                Gap(5)
                Text(
                    L("delete"),
                    color = NpColors.Muted,
                    fontSize = 10.sp,
                    modifier = Modifier
                        .clip(RoundedCornerShape(50))
                        .clickable(onClick = onDelete)
                        .padding(horizontal = 6.dp, vertical = 2.dp)
                )
            }
        }
    }
}

@Composable
private fun ModeChip(label: String, selected: Boolean, accent: androidx.compose.ui.graphics.Color, onClick: () -> Unit) {
    Row(
        modifier = Modifier
            .clip(RoundedCornerShape(50))
            .background(if (selected) accent.copy(alpha = 0.18f) else NpColors.CardAlt)
            .clickable(onClick = onClick)
            .padding(horizontal = 9.dp, vertical = 4.dp)
    ) {
        Text(
            label,
            color = if (selected) accent else NpColors.Muted,
            fontSize = 10.sp,
            fontWeight = if (selected) FontWeight.Bold else FontWeight.Medium
        )
    }
}

internal fun speed(bps: Long): String = when {
    bps <= 0 -> Strings.raw("lim_unlimited")
    bps >= 1024 * 1024 -> String.format(Locale.US, "%.1f %s", bps / 1048576.0, Strings.raw("lim_mbs"))
    else -> String.format(Locale.US, "%.0f %s", bps / 1024.0, Strings.raw("lim_kbs"))
}

/** bytes/sec from a numeric field + a unit flag (0 = KB/s, 1 = MB/s). */
internal fun toBps(value: String, unitMb: Boolean): Long {
    val v = value.toDoubleOrNull() ?: return 0L
    val bytes = if (unitMb) v * 1048576.0 else v * 1024.0
    return bytes.toLong().coerceIn(0L, 10L * 1024 * 1024 * 1024)
}

// ---------------------------------------------------------------------------- editor

@Composable
private fun RuleEditorDialog(
    initial: LimitRule,
    labelProvider: (String) -> String,
    onDismiss: () -> Unit,
    onSave: (LimitRule) -> Unit,
    onDelete: () -> Unit
) {
    var mode by remember { mutableStateOf(initial.mode) }
    var down by remember {
        mutableStateOf(
            if (initial.downBps >= 1048576)
                String.format(Locale.US, "%.1f", initial.downBps / 1048576.0)
            else if (initial.downBps > 0) "${initial.downBps / 1024}" else "1"
        )
    }
    var up by remember {
        mutableStateOf(
            if (initial.upBps >= 1048576)
                String.format(Locale.US, "%.1f", initial.upBps / 1048576.0)
            else if (initial.upBps > 0) "${initial.upBps / 1024}" else "1"
        )
    }
    var unitMb by remember {
        mutableStateOf(initial.downBps >= 1048576 || initial.downBps == 0L)
    }

    androidx.compose.material3.AlertDialog(
        onDismissRequest = onDismiss,
        containerColor = NpColors.Card,
        title = {
            Text(
                initial.label.ifBlank { labelProvider(initial.pkg) },
                color = NpColors.Text, fontSize = 16.sp, fontWeight = FontWeight.Bold
            )
        },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(9.dp)) {
                Text(initial.pkg, color = NpColors.Muted, fontSize = 10.sp)
                Row(horizontalArrangement = Arrangement.spacedBy(7.dp)) {
                    NpChip(L("lim_allow"), mode == RuleMode.ALLOW, Modifier.weight(1f)) {
                        mode = RuleMode.ALLOW
                    }
                    NpChip(L("lim_limit"), mode == RuleMode.LIMIT, Modifier.weight(1f)) {
                        mode = RuleMode.LIMIT
                    }
                    NpChip(L("lim_block"), mode == RuleMode.BLOCK, Modifier.weight(1f)) {
                        mode = RuleMode.BLOCK
                    }
                }

                if (mode == RuleMode.LIMIT) {
                    Text(L("lim_down_limit"), color = NpColors.Muted, fontSize = 11.sp)
                    NumberField(down, { down = it })
                    Text(L("lim_up_limit"), color = NpColors.Muted, fontSize = 11.sp)
                    NumberField(up, { up = it })
                    Row(horizontalArrangement = Arrangement.spacedBy(7.dp)) {
                        NpChip(L("lim_kbs"), !unitMb, Modifier.weight(1f)) { unitMb = false }
                        NpChip(L("lim_mbs"), unitMb, Modifier.weight(1f)) { unitMb = true }
                    }
                    Gap(2)
                    Text(
                        "${L("lim_preset")}: ${speed(toBps(down, unitMb))} / ${speed(toBps(up, unitMb))}",
                        color = NpColors.Accent,
                        fontSize = 11.sp
                    )
                } else if (mode == RuleMode.BLOCK) {
                    Text(L("lim_block_hint"), color = NpColors.Bad, fontSize = 11.sp)
                }
            }
        },
        confirmButton = {
            NpChip(L("save"), selected = true, onClick = {
                onSave(
                    initial.copy(
                        mode = mode,
                        label = initial.label.ifBlank { labelProvider(initial.pkg) },
                        downBps = if (mode == RuleMode.LIMIT) toBps(down, unitMb) else 0L,
                        upBps = if (mode == RuleMode.LIMIT) toBps(up, unitMb) else 0L
                    )
                )
            })
        },
        dismissButton = {
            Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                NpChip(L("delete"), selected = false, accent = NpColors.Bad, onClick = onDelete)
                NpChip(L("cancel"), selected = false, onClick = onDismiss)
            }
        }
    )
}

@Composable
private fun NumberField(value: String, onChange: (String) -> Unit) {
    OutlinedTextField(
        value = value,
        onValueChange = { v -> if (v.isEmpty() || v.matches(Regex("^[0-9]*\\.?[0-9]*$"))) onChange(v) },
        modifier = Modifier.fillMaxWidth(),
        singleLine = true,
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

// ---------------------------------------------------------------------------- app picker

/** Installed packages that can hold a rule, filtered to launchable/networked apps. */
@Composable
private fun rememberPackageLabels(): Map<String, String> {
    val ctx = LocalContext.current
    return remember {
        runCatching {
            val pm = ctx.packageManager
            val intent = Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER)
            @Suppress("DEPRECATION")
            pm.queryIntentActivities(intent, 0).mapNotNull { info ->
                val pkg = info.activityInfo?.packageName ?: return@mapNotNull null
                val label = info.loadLabel(pm)?.toString() ?: pkg
                pkg to label
            }.toMap()
        }.getOrDefault(emptyMap())
    }
}

@Composable
private fun AppPickerDialog(onDismiss: () -> Unit, onPick: (pkg: String, label: String) -> Unit) {
    var query by remember { mutableStateOf("") }
    val labels = rememberPackageLabels()

    androidx.compose.material3.AlertDialog(
        onDismissRequest = onDismiss,
        containerColor = NpColors.Card,
        title = { Text(L("add"), color = NpColors.Text) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                SearchField(query, { query = it }, L("search"))
                Column(
                    Modifier
                        .fillMaxWidth()
                        .padding(top = 2.dp)
                ) {
                    val list = labels.entries
                        .filter { query.isBlank() || it.value.contains(query, true) || it.key.contains(query, true) }
                        .take(40)
                    if (list.isEmpty()) {
                        Text(L("apps_no_data"), color = NpColors.Muted, fontSize = 12.sp)
                    }
                    list.forEach { (pkg, label) ->
                        Text(
                            "$label\n$pkg",
                            color = NpColors.Text,
                            fontSize = 12.sp,
                            modifier = Modifier
                                .fillMaxWidth()
                                .clip(RoundedCornerShape(10.dp))
                                .clickable { onPick(pkg, label) }
                                .padding(vertical = 7.dp, horizontal = 4.dp)
                        )
                    }
                }
            }
        },
        confirmButton = { NpChip(L("cancel"), selected = false, onClick = onDismiss) }
    )
}

internal fun labelOf(ctx: android.content.Context, pkg: String): String = runCatching {
    val pm = ctx.packageManager
    @Suppress("DEPRECATION")
    val info = pm.getApplicationInfo(pkg, 0)
    pm.getApplicationLabel(info).toString()
}.getOrDefault(pkg)
