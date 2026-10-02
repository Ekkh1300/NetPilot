package com.netpilot.mobile.ui.screens

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
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
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.netpilot.mobile.data.L
import com.netpilot.mobile.data.Repo
import com.netpilot.mobile.data.Strings
import com.netpilot.mobile.ui.components.EmptyState
import com.netpilot.mobile.ui.components.Gap
import com.netpilot.mobile.ui.components.GhostButton
import com.netpilot.mobile.ui.components.NpCard
import com.netpilot.mobile.ui.components.SectionTitle
import com.netpilot.mobile.ui.theme.NpColors
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * Connection History — the audit trail the desktop app keeps: every DNS change, limit
 * toggle, tunnel start/stop and PC pairing, newest first.
 */
@Composable
fun EventsScreen() {
    val events by Repo.events.collectAsState()

    Column(
        Modifier
            .fillMaxSize()
            .background(NpColors.Bg)
            .statusBarsPadding()
            .verticalScroll(rememberScrollState())
            .padding(horizontal = 12.dp, vertical = 10.dp),
        verticalArrangement = Arrangement.spacedBy(10.dp)
    ) {

        NpCard {
            Row(verticalAlignment = Alignment.CenterVertically) {
                SectionTitle(L("evt_title"), Modifier.weight(1f))
                if (events.isNotEmpty()) {
                    GhostButton(L("clear"), accent = NpColors.Bad) {
                        Repo.clearEvents()
                    }
                }
            }
            Text(
                "${events.size} / ${Repo.MAX_EVENTS}",
                color = NpColors.Muted,
                fontSize = 10.sp
            )
        }

        if (events.isEmpty()) {
            NpCard { EmptyState(L("evt_empty")) }
        } else {
            events.forEach { ev ->
                val accent = when (ev.type) {
                    "evt_dns_changed", "evt_dns_restored" -> NpColors.Accent
                    "evt_limit_on", "evt_blocked" -> NpColors.Warn
                    "evt_limit_off", "evt_unblocked" -> NpColors.Ok
                    "evt_net_reset" -> NpColors.Bad
                    "evt_pc_pair", "evt_pc_unpair" -> NpColors.Chart2
                    "evt_fw_start", "evt_fw_unsupported" -> NpColors.Accent
                    "evt_tun_lost", "evt_relay_lost" -> NpColors.Bad
                    else -> NpColors.Muted
                }
                NpCard(borderColor = accent.copy(alpha = 0.35f)) {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Box2(accent)
                        Column(Modifier.weight(1f).padding(start = 10.dp)) {
                            Text(
                                titleOf(ev.type),
                                color = NpColors.Text,
                                fontSize = 13.sp,
                                fontWeight = FontWeight.SemiBold
                            )
                            if (ev.info.isNotBlank()) {
                                Gap(2)
                                Text(ev.info, color = NpColors.Muted, fontSize = 10.sp, maxLines = 2)
                            }
                        }
                        Text(
                            TIME_FMT.format(Date(ev.ts)),
                            color = NpColors.Muted,
                            fontSize = 10.sp
                        )
                    }
                }
            }
        }

        Gap(4)
    }
}

@Composable
private fun Box2(color: androidx.compose.ui.graphics.Color) {
    androidx.compose.foundation.layout.Box(
        Modifier
            .size(9.dp)
            .clip(RoundedCornerShape(50))
            .background(color)
    )
}

/** Non-composable lookup: this runs inside a plain formatting helper. */
private fun titleOf(type: String): String = when (type) {
    "evt_dns_changed" -> Strings.raw("evt_dns_changed")
    "evt_dns_restored" -> Strings.raw("evt_dns_restored")
    "evt_limit_on" -> Strings.raw("evt_limit_on")
    "evt_limit_off" -> Strings.raw("evt_limit_off")
    "evt_blocked" -> Strings.raw("evt_blocked")
    "evt_unblocked" -> Strings.raw("evt_unblocked")
    "evt_net_reset" -> Strings.raw("evt_net_reset")
    "evt_profile" -> Strings.raw("evt_profile")
    "evt_vpn_start" -> Strings.raw("evt_vpn_start")
    "evt_vpn_stop" -> Strings.raw("evt_vpn_stop")
    "evt_pc_pair" -> Strings.raw("evt_pc_pair")
    "evt_pc_unpair" -> Strings.raw("evt_pc_unpair")
    "evt_fw_start" -> Strings.raw("evt_fw_start")
    "evt_fw_unsupported" -> Strings.raw("evt_fw_unsupported")
    "evt_tun_lost" -> Strings.raw("evt_tun_lost")
    "evt_relay_lost" -> Strings.raw("evt_relay_lost")
    else -> type
}

private val TIME_FMT = SimpleDateFormat("MM-dd HH:mm", Locale.US)
