package com.netpilot.mobile.ui.screens

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.net.wifi.WifiManager
import android.os.Build
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
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.netpilot.mobile.data.L
import com.netpilot.mobile.data.Strings
import com.netpilot.mobile.net.Monitor
import com.netpilot.mobile.ui.components.Gap
import com.netpilot.mobile.ui.components.GhostButton
import com.netpilot.mobile.ui.components.NpCard
import com.netpilot.mobile.ui.components.SectionTitle
import com.netpilot.mobile.ui.components.StatTile
import com.netpilot.mobile.ui.theme.NpColors
import com.netpilot.mobile.vpn.VpnState

private data class Adapter(
    val name: String,
    val transport: String,
    val active: Boolean,
    val lines: List<Pair<String, String>>
)

/**
 * Adapters — every network the platform currently knows about, read straight from
 * [ConnectivityManager]; the active one carries IP / gateway / DNS / link speed.
 */
@Composable
fun AdaptersScreen() {
    val ctx = LocalContext.current
    val link by Monitor.link.collectAsState()
    val mode by VpnState.mode.collectAsState()
    var refreshKey by remember { mutableStateOf(0) }

    val adapters = remember(refreshKey, link, mode) { scanAdapters(ctx) }

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
            Row(verticalAlignment = Alignment.CenterVertically) {
                SectionTitle(L("adp_title"), Modifier.weight(1f))
                GhostButton(L("refresh")) { refreshKey++ }
            }
        }

        if (adapters.isEmpty()) {
            NpCard { Text(L("adp_none"), color = NpColors.Muted, fontSize = 13.sp) }
        }

        adapters.forEach { a ->
            AdapterCard(a)
        }

        // ------------------------------------------------------------ active details
        NpCard {
            SectionTitle(L("adp_status"))
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                StatTile(
                    label = L("adp_ip"),
                    value = link.localIp.ifBlank { "—" },
                    accent = NpColors.Accent,
                    modifier = Modifier.weight(1f)
                )
                StatTile(
                    label = L("adp_gateway"),
                    value = link.gateway.ifBlank { "—" },
                    accent = NpColors.Accent,
                    modifier = Modifier.weight(1f)
                )
            }
            Gap(8)
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                StatTile(
                    label = L("adp_dns"),
                    value = link.dnsServers.firstOrNull() ?: "—",
                    sub = if (link.dnsServers.size > 1) link.dnsServers[1] else "",
                    accent = if (mode != VpnState.Mode.OFF) NpColors.Ok else NpColors.Text,
                    modifier = Modifier.weight(1f)
                )
                StatTile(
                    label = L("adp_link_speed"),
                    value = if (link.linkSpeedMbps > 0) "${link.linkSpeedMbps} Mbps" else "—",
                    accent = NpColors.Chart2,
                    modifier = Modifier.weight(1f)
                )
            }
            Gap(6)
            Text(
                "${L("adp_status")}: " +
                        (if (link.connected) L("adp_active") else L("adp_inactive")) +
                        (if (mode != VpnState.Mode.OFF) " · " + L("adp_vpn") else ""),
                color = if (link.connected) NpColors.Ok else NpColors.Bad,
                fontSize = 12.sp
            )
        }

        Gap(4)
    }
}

@Composable
private fun AdapterCard(a: Adapter) {
    NpCard(
        borderColor = if (a.active) NpColors.Ok.copy(alpha = 0.5f) else NpColors.Stroke
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text(
                    a.name,
                    color = if (a.active) NpColors.Ok else NpColors.Text,
                    fontSize = 14.sp,
                    fontWeight = FontWeight.Bold
                )
                Text(
                    a.transport,
                    color = NpColors.Muted,
                    fontSize = 10.sp
                )
            }
            Text(
                if (a.active) L("adp_active") else L("adp_inactive"),
                color = if (a.active) NpColors.Ok else NpColors.Muted,
                fontSize = 11.sp
            )
        }
        if (a.lines.isNotEmpty()) {
            Gap(8)
            a.lines.forEach { (k, v) ->
                Row(Modifier.padding(vertical = 2.dp)) {
                    Text(k, color = NpColors.Muted, fontSize = 11.sp, modifier = Modifier.weight(1f))
                    Text(v, color = NpColors.Text, fontSize = 11.sp)
                }
            }
        }
    }
}

// ---------------------------------------------------------------------------- scan

private fun scanAdapters(context: Context): List<Adapter> {
    // Non-composable context: the bilingual table is read through Strings.raw().
    fun T(key: String) = Strings.raw(key)

    val app = context.applicationContext
    val cm = app.getSystemService(Context.CONNECTIVITY_SERVICE) as? ConnectivityManager
        ?: return emptyList()
    val wifi = app.getSystemService(Context.WIFI_SERVICE) as? WifiManager
    val active = cm.activeNetwork
    val out = ArrayList<Adapter>()

    val networks: List<Network> = runCatching {
        cm.allNetworks?.toList() ?: emptyList()
    }.getOrDefault(emptyList())

    for (n in networks) {
        val caps = runCatching { cm.getNetworkCapabilities(n) }.getOrNull() ?: continue
        val lp = runCatching { cm.getLinkProperties(n) }.getOrNull()

        val transport = when {
            caps.hasTransport(NetworkCapabilities.TRANSPORT_VPN) -> T("adp_vpn")
            caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) -> T("adp_wifi")
            caps.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET) -> T("adp_ethernet")
            caps.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR) -> T("adp_cellular")
            else -> T("mon_none")
        }

        val lines = ArrayList<Pair<String, String>>()

        val ip = lp?.linkAddresses
            ?.firstOrNull { it.address is java.net.Inet4Address }
            ?.address?.hostAddress
        if (!ip.isNullOrBlank()) lines.add(T("adp_ip") to ip)

        val gw = lp?.routes
            ?.firstOrNull { it.isDefaultRoute && it.gateway is java.net.Inet4Address }
            ?.gateway?.hostAddress
        if (!gw.isNullOrBlank()) lines.add(T("adp_gateway") to gw)

        val dns = lp?.dnsServers?.mapNotNull { it.hostAddress }
        if (!dns.isNullOrEmpty()) lines.add(T("adp_dns") to dns.joinToString(" · "))

        if (caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) && wifi != null) {
            val info = runCatching { wifi.connectionInfo }.getOrNull()
            val ssid = info?.ssid?.trim('"')
            if (!ssid.isNullOrBlank() && ssid != "<unknown ssid>") {
                lines.add(T("adp_ssid") to ssid)
            }
            val bssid = info?.bssid
            if (!bssid.isNullOrBlank() && bssid != "02:00:00:00:00:00") {
                lines.add(T("adp_bssid") to bssid)
            }
            val freq = runCatching { info?.frequency ?: -1 }.getOrDefault(-1)
            if (freq > 0) lines.add(T("adp_frequency") to "$freq MHz")
            // Negotiated rate lives on WifiInfo, not on WifiManager.
            val speed = runCatching { info?.linkSpeed ?: -1 }.getOrDefault(-1)
            if (speed > 0) lines.add(T("adp_link_speed") to "$speed Mbps")
        }

        val validated = caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED)
        lines.add(T("adp_status") to if (validated) T("adp_active") else T("adp_inactive"))
        if (Build.VERSION.SDK_INT >= 29) {
            val vpn = caps.hasTransport(NetworkCapabilities.TRANSPORT_VPN)
            if (vpn) lines.add(T("adp_vpn") to "NetPilot")
        }

        out.add(
            Adapter(
                name = transport,
                transport = typeName(caps),
                active = n == active,
                lines = lines
            )
        )
    }

    return out.sortedByDescending { it.active }
}

private fun typeName(caps: NetworkCapabilities): String = when {
    caps.hasTransport(NetworkCapabilities.TRANSPORT_VPN) -> "VPN"
    caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) -> "WIFI"
    caps.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET) -> "ETHERNET"
    caps.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR) -> "CELLULAR"
    else -> "OTHER"
}
