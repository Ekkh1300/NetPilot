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
import com.netpilot.mobile.App
import com.netpilot.mobile.core.AppGraph
import com.netpilot.mobile.data.L
import com.netpilot.mobile.data.Profile
import com.netpilot.mobile.data.Repo
import com.netpilot.mobile.data.Strings
import com.netpilot.mobile.ui.components.Gap
import com.netpilot.mobile.ui.components.GhostButton
import com.netpilot.mobile.ui.components.NpButton
import com.netpilot.mobile.ui.components.NpCard
import com.netpilot.mobile.ui.components.SectionTitle
import com.netpilot.mobile.ui.theme.NpColors
import com.netpilot.mobile.vpn.NetPilotVpnService

/**
 * Profiles — a profile is a snapshot of DNS + limits that can be recalled in one tap
 * (the mobile counterpart of the desktop profile switcher).
 */
@Composable
fun ProfilesScreen() {
    val ctx = LocalContext.current

    val profiles by Repo.profiles.collectAsState()
    // `settingsVer` must actually be *read*: an unread `collectAsState()` never subscribes,
    // so the screen used to keep showing the previous profile as "Profile applied".
    val settingsVer by Repo.settingsVersion.collectAsState()
    val activeId = remember(settingsVer) { Repo.activeProfileId() }
    val rules by Repo.rules.collectAsState()

    var showNew by remember { mutableStateOf(false) }
    var confirmDelete by remember { mutableStateOf<Profile?>(null) }
    var toast by remember { mutableStateOf("") }

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
            SectionTitle(L("prf_title"))
            Text(L("prf_contains"), color = NpColors.Muted, fontSize = 11.sp)
            Gap(6)
            Text(
                "${L("dns_current")}: " +
                        (AppGraph.dnsCatalog.find(AppGraph.dnsCatalog.selectedId)?.name
                            ?: L("dash_dns_auto")) +
                        "  ·  ${rules.size} × ${L("lim_title")}",
                color = NpColors.Text,
                fontSize = 12.sp
            )
            Gap(10)
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                NpButton(
                    text = L("prf_new"),
                    modifier = Modifier.weight(1f)
                ) { showNew = true }
                GhostButton(
                    text = L("save"),
                    modifier = Modifier.weight(1f)
                ) {
                    // Snapshot the current state into the active profile.
                    Repo.captureInto(activeId)
                    Repo.log("evt_profile", Repo.profile(activeId)?.name ?: activeId)
                    toast = Strings.raw("save")
                }
            }
            if (toast.isNotBlank()) {
                Gap(6)
                Text(toast, color = NpColors.Ok, fontSize = 11.sp)
            }
        }

        profiles.forEach { p ->
            val active = p.id == activeId
            NpCard(
                borderColor = if (active) NpColors.Ok.copy(alpha = 0.55f) else NpColors.Stroke
            ) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Column(Modifier.weight(1f)) {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Text(
                                localizedName(p),
                                color = if (active) NpColors.Ok else NpColors.Text,
                                fontSize = 14.sp,
                                fontWeight = FontWeight.Bold
                            )
                            if (p.builtin) {
                                Gap(6)
                                Text(
                                    L("prf_builtin"),
                                    color = NpColors.Muted,
                                    fontSize = 9.sp,
                                    modifier = Modifier
                                        .clip(RoundedCornerShape(50))
                                        .background(NpColors.CardAlt)
                                        .padding(horizontal = 6.dp, vertical = 2.dp)
                                )
                            }
                        }
                        Gap(4)
                        Text(
                            "${AppGraph.dnsCatalog.find(p.dnsId)?.name ?: p.dnsId.ifBlank { "—" }}" +
                                    "  ·  ${p.rules.size} × ${L("lim_title")}",
                            color = NpColors.Muted,
                            fontSize = 11.sp
                        )
                    }
                    Column(horizontalAlignment = Alignment.End) {
                        if (active) {
                            Text(L("prf_applied"), color = NpColors.Ok, fontSize = 11.sp)
                        } else {
                            NpChipApply { applyProfile(p) }
                        }
                        Gap(6)
                        if (!p.builtin) {
                            Text(
                                L("delete"),
                                color = NpColors.Muted,
                                fontSize = 10.sp,
                                modifier = Modifier
                                    .clip(RoundedCornerShape(50))
                                    .clickable { confirmDelete = p }
                                    .padding(horizontal = 6.dp, vertical = 2.dp)
                            )
                        }
                    }
                }
            }
        }

        Gap(4)
    }

    if (showNew) {
        NewProfileDialog(
            onDismiss = { showNew = false },
            onCreate = { name ->
                val p = Repo.addProfile(name)
                showNew = false
                applyProfile(p)
            }
        )
    }

    confirmDelete?.let { p ->
        androidx.compose.material3.AlertDialog(
            onDismissRequest = { confirmDelete = null },
            containerColor = NpColors.Card,
            title = { Text(L("prf_delete_confirm"), color = NpColors.Text) },
            text = { Text(p.name, color = NpColors.Muted) },
            confirmButton = {
                com.netpilot.mobile.ui.components.NpChip(
                    L("delete"), selected = false, accent = NpColors.Bad
                ) {
                    Repo.deleteProfile(p.id)
                    confirmDelete = null
                }
            },
            dismissButton = {
                com.netpilot.mobile.ui.components.NpChip(
                    L("cancel"), selected = false
                ) { confirmDelete = null }
            }
        )
    }
}

@Composable
private fun NpChipApply(onClick: () -> Unit) {
    com.netpilot.mobile.ui.components.NpChip(L("apply"), selected = false, onClick = onClick)
}

/** Apply a profile: recall its DNS through the tunnel and restore its limit set. */
private fun applyProfile(p: Profile) {
    Repo.setActiveProfile(p.id)
    // Rules are restored first so the engine never runs with a half-applied profile. This is
    // a *replace*, not a merge: a profile is a snapshot, and one without limits has to mean
    // "no limits" - the previous merge left every earlier rule in force.
    Repo.replaceRules(p.rules)
    if (p.dnsId.isNotBlank()) {
        NetPilotVpnService.applyDns(App.instance, p.dnsId)
    }
    Repo.log("evt_profile", p.name)
}

private fun localizedName(p: Profile): String = when (p.id) {
    "default" -> Strings.raw("prf_default")
    "browsing" -> Strings.raw("prf_browsing")
    "gaming" -> Strings.raw("prf_gaming")
    else -> p.name
}

@Composable
private fun NewProfileDialog(onDismiss: () -> Unit, onCreate: (String) -> Unit) {
    var name by remember { mutableStateOf("") }

    androidx.compose.material3.AlertDialog(
        onDismissRequest = onDismiss,
        containerColor = NpColors.Card,
        title = { Text(L("prf_new"), color = NpColors.Text) },
        text = {
            OutlinedTextField(
                value = name,
                onValueChange = { name = it },
                modifier = Modifier.fillMaxWidth(),
                singleLine = true,
                label = { Text(L("dns_name"), color = NpColors.Muted) },
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
        },
        confirmButton = {
            com.netpilot.mobile.ui.components.NpChip(
                L("save"), selected = true
            ) { if (name.isNotBlank()) onCreate(name.trim()) }
        },
        dismissButton = {
            com.netpilot.mobile.ui.components.NpChip(L("cancel"), selected = false, onClick = onDismiss)
        }
    )
}
