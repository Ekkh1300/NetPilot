package com.netpilot.mobile.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Apps
import androidx.compose.material.icons.outlined.Dashboard
import androidx.compose.material.icons.outlined.Dns
import androidx.compose.material.icons.outlined.MoreHoriz
import androidx.compose.material.icons.outlined.Speed
import androidx.compose.material3.Icon
import androidx.compose.material3.LocalTextStyle
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalLayoutDirection
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.LayoutDirection
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.navigation.NavHostController
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.compose.currentBackStackEntryAsState
import androidx.navigation.compose.rememberNavController
import com.netpilot.mobile.data.Repo
import com.netpilot.mobile.data.Strings
import com.netpilot.mobile.ui.components.NpTopBar
import com.netpilot.mobile.ui.screens.AdaptersScreen
import com.netpilot.mobile.ui.screens.BenchScreen
import com.netpilot.mobile.ui.screens.DashboardScreen
import com.netpilot.mobile.ui.screens.DnsScreen
import com.netpilot.mobile.ui.screens.EventsScreen
import com.netpilot.mobile.ui.screens.HistoryScreen
import com.netpilot.mobile.ui.screens.LimiterScreen
import com.netpilot.mobile.ui.screens.MobileVpnScreen
import com.netpilot.mobile.ui.screens.MonitorScreen
import com.netpilot.mobile.ui.screens.PerAppScreen
import com.netpilot.mobile.ui.screens.ProfilesScreen
import com.netpilot.mobile.ui.screens.SchedulesScreen
import com.netpilot.mobile.ui.screens.SettingsScreen
import com.netpilot.mobile.ui.screens.SmartDnsScreen
import com.netpilot.mobile.ui.screens.ToolsScreen
import com.netpilot.mobile.ui.theme.NpColors
import com.netpilot.mobile.data.L

/** Single-activity shell: top bar + page + bottom navigation, with live RTL flipping. */
@Composable
fun AppRoot() {
    val nav = rememberNavController()
    val lang by Repo.language.collectAsState()
    val layout = if (lang == "fa") LayoutDirection.Rtl else LayoutDirection.Ltr

    CompositionLocalProvider(LocalLayoutDirection provides layout) {
        val backStack by nav.currentBackStackEntryAsState()
        val route = backStack?.destination?.route ?: Routes.DASHBOARD
        val isRoot = route in ROOT_ROUTES

        Column(
            Modifier
                .fillMaxSize()
                .background(NpColors.Bg)
        ) {
            NpTopBar(
                title = titleFor(route),
                onBack = if (isRoot) null else {
                    { if (!nav.popBackStack()) nav.navigate(Routes.DASHBOARD) }
                },
                actions = { LangToggle(lang) }
            )

            Box(
                Modifier
                    .weight(1f)
                    .fillMaxWidth()
            ) {
                AppNavHost(nav)
            }

            BottomBar(route = route, nav = nav)
        }
    }
}

private val ROOT_ROUTES = setOf(
    Routes.DASHBOARD, Routes.DNS, Routes.MONITOR, Routes.APPS, Routes.MORE
)

object Routes {
    const val DASHBOARD = "dashboard"
    const val DNS = "dns"
    const val BENCH = "bench"
    const val SMART = "smart"
    const val MONITOR = "monitor"
    const val APPS = "apps"
    const val LIMITER = "limiter"
    const val SCHEDULES = "schedules"
    const val HISTORY = "history"
    const val EVENTS = "events"
    const val TOOLS = "tools"
    const val ADAPTERS = "adapters"
    const val PROFILES = "profiles"
    const val MOBILE_VPN = "mobilevpn"
    const val SETTINGS = "settings"
    const val MORE = "more"
}

private fun titleFor(route: String): String = when (route) {
    Routes.DASHBOARD -> Strings.raw("app_title")
    Routes.DNS -> Strings.raw("menu_dns_manager")
    Routes.BENCH -> Strings.raw("menu_benchmark")
    Routes.SMART -> Strings.raw("menu_smart_dns")
    Routes.MONITOR -> Strings.raw("menu_monitor")
    Routes.APPS -> Strings.raw("menu_per_app")
    Routes.LIMITER -> Strings.raw("menu_limiter")
    Routes.SCHEDULES -> Strings.raw("menu_schedules")
    Routes.HISTORY -> Strings.raw("menu_history")
    Routes.EVENTS -> Strings.raw("menu_events")
    Routes.TOOLS -> Strings.raw("menu_tools")
    Routes.ADAPTERS -> Strings.raw("menu_adapters")
    Routes.PROFILES -> Strings.raw("menu_profiles")
    Routes.MOBILE_VPN -> Strings.raw("menu_mobile_vpn")
    Routes.SETTINGS -> Strings.raw("menu_settings")
    Routes.MORE -> Strings.raw("nav_more")
    else -> Strings.raw("app_title")
}

@Composable
private fun AppNavHost(nav: NavHostController) {
    NavHost(navController = nav, startDestination = Routes.DASHBOARD) {

        composable(Routes.DASHBOARD) { DashboardScreen(onOpen = { nav.navigate(it) }) }
        composable(Routes.DNS) { DnsScreen(onOpen = { nav.navigate(it) }) }
        composable(Routes.BENCH) { BenchScreen() }
        composable(Routes.SMART) { SmartDnsScreen() }
        composable(Routes.MONITOR) { MonitorScreen() }
        composable(Routes.APPS) { PerAppScreen() }
        composable(Routes.LIMITER) { LimiterScreen() }
        composable(Routes.SCHEDULES) { SchedulesScreen() }
        composable(Routes.HISTORY) { HistoryScreen() }
        composable(Routes.EVENTS) { EventsScreen() }
        composable(Routes.TOOLS) { ToolsScreen() }
        composable(Routes.ADAPTERS) { AdaptersScreen() }
        composable(Routes.PROFILES) { ProfilesScreen() }
        composable(Routes.MOBILE_VPN) { MobileVpnScreen() }
        composable(Routes.SETTINGS) { SettingsScreen() }
        composable(Routes.MORE) { MoreScreen(onOpen = { nav.navigate(it) }) }
    }
}

// ---------------------------------------------------------------------------- language toggle

@Composable
private fun LangToggle(lang: String) {
    val next = if (lang == "fa") "EN" else "FA"
    Text(
        text = next,
        color = NpColors.Accent,
        fontSize = 12.sp,
        fontWeight = FontWeight.Bold,
        modifier = Modifier
            .clip(RoundedCornerShape(50))
            .background(NpColors.CardAlt)
            .clickable { Repo.setLanguage(if (lang == "fa") "en" else "fa") }
            .padding(horizontal = 12.dp, vertical = 6.dp)
    )
}

// ---------------------------------------------------------------------------- bottom bar

private data class Tab(
    val route: String,
    val icon: ImageVector,
    val labelKey: String
)

private val TABS = listOf(
    Tab(Routes.DASHBOARD, Icons.Outlined.Dashboard, "nav_dashboard"),
    Tab(Routes.DNS, Icons.Outlined.Dns, "nav_dns"),
    Tab(Routes.MONITOR, Icons.Outlined.Speed, "nav_monitor"),
    Tab(Routes.APPS, Icons.Outlined.Apps, "nav_apps"),
    Tab(Routes.MORE, Icons.Outlined.MoreHoriz, "nav_more")
)

/** Maps child routes onto the tab their parent owns, so the bar stays highlighted. */
private fun parentTab(route: String): String = when (route) {
    Routes.BENCH, Routes.SMART -> Routes.DNS
    Routes.LIMITER, Routes.SCHEDULES, Routes.HISTORY, Routes.EVENTS,
    Routes.TOOLS, Routes.ADAPTERS, Routes.PROFILES, Routes.MOBILE_VPN,
    Routes.SETTINGS, Routes.MORE -> Routes.MORE

    else -> route
}

@Composable
private fun BottomBar(route: String, nav: NavHostController) {
    val selected = parentTab(route)
    Row(
        Modifier
            .fillMaxWidth()
            .background(NpColors.BgElevated)
            .navigationBarsPadding()
            .padding(horizontal = 6.dp, vertical = 6.dp)
    ) {
        TABS.forEach { tab ->
            val active = tab.route == selected
            Column(
                modifier = Modifier
                    .weight(1f)
                    .clip(RoundedCornerShape(14.dp))
                    .background(if (active) NpColors.Accent.copy(alpha = 0.14f) else androidx.compose.ui.graphics.Color.Transparent)
                    .clickable {
                        if (tab.route != route) {
                            nav.navigate(tab.route) {
                                // Always land on the tab's own root. With saveState +
                                // restoreState the popped segment is restored as a whole, so
                                // tapping "More" while on Net Limiter put Net Limiter back on
                                // screen instead of the More grid.
                                popUpTo(Routes.DASHBOARD) { inclusive = false }
                                launchSingleTop = true
                            }
                        }
                    }
                    .padding(vertical = 7.dp),
                horizontalAlignment = Alignment.CenterHorizontally
            ) {
                Icon(
                    tab.icon,
                    contentDescription = Strings.raw(tab.labelKey),
                    tint = if (active) NpColors.Accent else NpColors.Muted,
                    modifier = Modifier.size(21.dp)
                )
                Spacer(Modifier.height(3.dp))
                Text(
                    Strings.raw(tab.labelKey),
                    color = if (active) NpColors.Accent else NpColors.Muted,
                    fontSize = 10.sp,
                    fontWeight = if (active) FontWeight.Bold else FontWeight.Medium,
                    style = LocalTextStyle.current
                )
            }
        }
    }
}

// ---------------------------------------------------------------------------- "More" page

/** Grid of every secondary page. */
@Composable
private fun MoreScreen(onOpen: (String) -> Unit) {
    val items = listOf(
        Triple(Strings.raw("menu_benchmark"), Routes.BENCH, "benchmark"),
        Triple(Strings.raw("menu_smart_dns"), Routes.SMART, "smart_dns"),
        Triple(Strings.raw("menu_limiter"), Routes.LIMITER, "limiter"),
        Triple(Strings.raw("menu_schedules"), Routes.SCHEDULES, "schedule"),
        Triple(Strings.raw("menu_history"), Routes.HISTORY, "history"),
        Triple(Strings.raw("menu_events"), Routes.EVENTS, "events"),
        Triple(Strings.raw("menu_tools"), Routes.TOOLS, "tools"),
        Triple(Strings.raw("menu_adapters"), Routes.ADAPTERS, "adapters"),
        Triple(Strings.raw("menu_profiles"), Routes.PROFILES, "profiles"),
        Triple(Strings.raw("menu_mobile_vpn"), Routes.MOBILE_VPN, "mobile_vpn"),
        Triple(Strings.raw("menu_settings"), Routes.SETTINGS, "settings")
    )
    androidx.compose.foundation.lazy.grid.LazyVerticalGrid(
        columns = androidx.compose.foundation.lazy.grid.GridCells.Fixed(2),
        modifier = Modifier
            .fillMaxSize()
            .background(NpColors.Bg)
            .statusBarsPadding()
            .padding(horizontal = 10.dp, vertical = 8.dp),
        horizontalArrangement = androidx.compose.foundation.layout.Arrangement.spacedBy(10.dp),
        verticalArrangement = androidx.compose.foundation.layout.Arrangement.spacedBy(10.dp)
    ) {
        items(items.size) { i ->
            val (title, route, _) = items[i]
            Box(
                Modifier
                    .fillMaxWidth()
                    .height(74.dp)
                    .clip(RoundedCornerShape(16.dp))
                    .background(NpColors.Card)
                    .clickable { onOpen(route) }
                    .padding(14.dp),
                contentAlignment = Alignment.CenterStart
            ) {
                Text(title, color = NpColors.Text, fontSize = 13.sp, fontWeight = FontWeight.SemiBold)
            }
        }
    }
}
