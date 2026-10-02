package com.netpilot.mobile.ui.screens

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
import com.netpilot.mobile.data.Repo
import com.netpilot.mobile.data.ScheduleRule
import com.netpilot.mobile.data.Strings
import com.netpilot.mobile.ui.components.EmptyState
import com.netpilot.mobile.ui.components.Gap
import com.netpilot.mobile.ui.components.GhostButton
import com.netpilot.mobile.ui.components.NpButton
import com.netpilot.mobile.ui.components.NpCard
import com.netpilot.mobile.ui.components.NpChip
import com.netpilot.mobile.ui.components.SectionTitle
import com.netpilot.mobile.ui.components.SwitchRow
import com.netpilot.mobile.ui.theme.NpColors
import kotlinx.coroutines.delay
import java.util.UUID

/**
 * Scheduled Limits — time windows (minutes from local midnight) during which a rule is
 * forced on top of the base limit. Evaluation happens in [Repo.effectiveRules].
 */
@Composable
fun SchedulesScreen() {
    val ctx = LocalContext.current
    val schedules by Repo.schedules.collectAsState()
    // The wall clock is not observable: without a ticker the "Running" badge stayed frozen
    // at whatever minute the screen happened to be drawn, so a schedule that switched on or
    // off while the screen was open never changed its state.
    var minuteTick by remember { mutableStateOf(0) }
    LaunchedEffect(Unit) {
        while (true) {
            delay(60_000)
            minuteTick++
        }
    }
    val nowMin = remember(minuteTick) { Repo.minutesOfDay() }

    var editing by remember { mutableStateOf<ScheduleRule?>(null) }
    var creating by remember { mutableStateOf(false) }

    Column(
        Modifier
            .fillMaxSize()
            .background(NpColors.Bg)
            .statusBarsPadding()
            .verticalScroll(rememberScrollState())
            .padding(horizontal = 12.dp, vertical = 10.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp)
    ) {

        NpCard {
            SectionTitle(L("sch_title"))
            Text(L("sch_example"), color = NpColors.Muted, fontSize = 11.sp)
            Gap(6)
            Text(L("sch_apply_hint"), color = NpColors.Muted, fontSize = 11.sp)
            Gap(8)
            NpButton(text = L("sch_add"), onClick = { creating = true })
        }

        if (schedules.isEmpty()) {
            NpCard { EmptyState(L("sch_empty")) }
        } else {
            schedules.forEach { s ->
                ScheduleRow(
                    rule = s,
                    active = s.enabled && s.activeAt(nowMin),
                    onToggle = { Repo.toggleSchedule(s.id, it) },
                    onEdit = { editing = s },
                    onDelete = { Repo.removeSchedule(s.id) }
                )
            }
        }

        Gap(4)
    }

    if (creating) {
        ScheduleEditor(
            initial = ScheduleRule(
                id = "",
                pkg = "",
                label = "",
                startMin = 22 * 60,
                endMin = 8 * 60,
                downBps = 1024 * 1024,
                upBps = 256 * 1024
            ),
            onDismiss = { creating = false },
            onSave = { rule ->
                Repo.addSchedule(rule.copy(id = UUID.randomUUID().toString()))
                Repo.log("evt_limit_on", rule.label)
                creating = false
            }
        )
    }

    editing?.let { s ->
        ScheduleEditor(
            initial = s,
            onDismiss = { editing = null },
            onSave = { rule ->
                Repo.updateSchedule(rule)
                editing = null
            }
        )
    }
}

// ---------------------------------------------------------------------------- row

@Composable
private fun ScheduleRow(
    rule: ScheduleRule,
    active: Boolean,
    onToggle: (Boolean) -> Unit,
    onEdit: () -> Unit,
    onDelete: () -> Unit
) {
    NpCard(borderColor = if (active) NpColors.Warn.copy(alpha = 0.6f) else NpColors.Stroke) {
        SwitchRow(
            label = rule.label.ifBlank { rule.pkg },
            checked = rule.enabled,
            onCheckedChange = onToggle,
            sub = "${hhmm(rule.startMin)} → ${hhmm(rule.endMin)} · " +
                    "↓ ${speed(rule.downBps)} / ↑ ${speed(rule.upBps)}" +
                    if (active) "  ·  " + L("running") else ""
        )
        Gap(8)
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            GhostButton(L("edit"), Modifier.weight(1f), onClick = onEdit)
            GhostButton(L("delete"), Modifier.weight(1f), accent = NpColors.Bad, onClick = onDelete)
        }
    }
}

internal fun hhmm(min: Int): String {
    val h = ((min / 60) % 24 + 24) % 24
    val m = ((min % 60) + 60) % 60
    return String.format(java.util.Locale.US, "%02d:%02d", h, m)
}

internal fun parseHhMm(text: String, fallback: Int): Int {
    val parts = text.trim().split(':')
    if (parts.size != 2) return fallback
    val h = parts[0].toIntOrNull()?.coerceIn(0, 23) ?: return fallback
    val m = parts[1].toIntOrNull()?.coerceIn(0, 59) ?: return fallback
    return h * 60 + m
}

// ---------------------------------------------------------------------------- editor

@Composable
private fun ScheduleEditor(
    initial: ScheduleRule,
    onDismiss: () -> Unit,
    onSave: (ScheduleRule) -> Unit
) {
    val ctx = LocalContext.current
    var label by remember { mutableStateOf(initial.label) }
    var pkg by remember { mutableStateOf(initial.pkg) }
    var from by remember { mutableStateOf(hhmm(initial.startMin)) }
    var to by remember { mutableStateOf(hhmm(initial.endMin)) }
    var down by remember { mutableStateOf(bpsToNumber(initial.downBps)) }
    var up by remember { mutableStateOf(bpsToNumber(initial.upBps)) }
    var error by remember { mutableStateOf("") }

    androidx.compose.material3.AlertDialog(
        onDismissRequest = onDismiss,
        containerColor = NpColors.Card,
        title = {
            Text(L("sch_add"), color = NpColors.Text, fontSize = 16.sp, fontWeight = FontWeight.Bold)
        },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Field(L("dns_name"), label, { label = it })

                if (initial.pkg.isBlank()) {
                    // Pick an existing rule's package when one is available.
                    val rules by Repo.rules.collectAsState()
                    // Pre-select the first rule: with nothing chosen Save used to fail with a
                    // generic error even though the app was right there on screen.
                    LaunchedEffect(rules) {
                        if (pkg.isBlank() && rules.isNotEmpty()) pkg = rules.first().pkg
                    }
                    if (rules.isNotEmpty()) {
                        Text(L("lim_title"), color = NpColors.Muted, fontSize = 11.sp)
                        Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                            rules.take(4).forEach { r ->
                                NpChip(
                                    r.label.ifBlank { r.pkg },
                                    pkg == r.pkg,
                                    accent = NpColors.Warn
                                ) { pkg = r.pkg; label = r.label }
                            }
                        }
                    } else {
                        Field(L("apps_process"), pkg, { pkg = it })
                    }
                }

                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    Column(Modifier.weight(1f)) {
                        Field(L("sch_from"), from, { from = it })
                    }
                    Column(Modifier.weight(1f)) {
                        Field(L("sch_to"), to, { to = it })
                    }
                }
                Text(L("sch_limit"), color = NpColors.Muted, fontSize = 11.sp)
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    Column(Modifier.weight(1f)) {
                        Field(L("lim_down_limit"), down, { down = it })
                    }
                    Column(Modifier.weight(1f)) {
                        Field(L("lim_up_limit"), up, { up = it })
                    }
                }
                Text(L("lim_kbs"), color = NpColors.Muted, fontSize = 10.sp)

                if (error.isNotBlank()) Text(error, color = NpColors.Bad, fontSize = 11.sp)
            }
        },
        confirmButton = {
            NpChip(L("save"), selected = true, onClick = {
                if (pkg.isBlank() && initial.pkg.isBlank()) {
                    error = Strings.raw("sch_pick_app")
                    return@NpChip
                }
                val s = parseHhMm(from, initial.startMin)
                val e = parseHhMm(to, initial.endMin)
                onSave(
                    initial.copy(
                        pkg = pkg.ifBlank { initial.pkg },
                        label = label.trim().ifBlank { pkg.ifBlank { initial.label } },
                        startMin = s,
                        endMin = e,
                        downBps = numberToBps(down),
                        upBps = numberToBps(up),
                        enabled = true
                    )
                )
            })
        },
        dismissButton = { NpChip(L("cancel"), selected = false, onClick = onDismiss) }
    )
}

/** KB/s number entry → bytes/sec. */
internal fun numberToBps(text: String): Long {
    val v = text.toDoubleOrNull() ?: return 0L
    return (v * 1024.0).toLong().coerceIn(0L, 10L * 1024 * 1024 * 1024)
}

internal fun bpsToNumber(bps: Long): String =
    if (bps <= 0) "0" else if (bps >= 1048576)
        String.format(java.util.Locale.US, "%.1f", bps / 1048576.0)
    else "${bps / 1024}"

@Composable
private fun Field(label: String, value: String, onChange: (String) -> Unit) {
    OutlinedTextField(
        value = value,
        onValueChange = onChange,
        modifier = Modifier.fillMaxWidth(),
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
