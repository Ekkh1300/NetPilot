package com.netpilot.mobile.ui.screens

import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.PowerManager
import android.provider.Settings as AndroidSettings
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
import com.netpilot.mobile.data.Repo
import com.netpilot.mobile.data.Strings
import com.netpilot.mobile.net.Monitor
import com.netpilot.mobile.ui.components.Gap
import com.netpilot.mobile.ui.components.GhostButton
import com.netpilot.mobile.ui.components.NpButton
import com.netpilot.mobile.ui.components.NpCard
import com.netpilot.mobile.ui.components.NpChip
import com.netpilot.mobile.ui.components.SectionTitle
import com.netpilot.mobile.ui.components.SwitchRow
import com.netpilot.mobile.ui.theme.NpColors

/**
 * Settings — language, sampling interval, battery exemption, backup/restore and reset.
 * Export writes the whole store snapshot to the clipboard as JSON (no cloud, no account).
 */
@Composable
fun SettingsScreen() {
    val ctx = LocalContext.current

    val lang by Repo.language.collectAsState()
    val settingsVer by Repo.settingsVersion.collectAsState()
    val interval = remember(settingsVer) { Repo.monitorInterval() }

    var toast by remember { mutableStateOf("") }
    var importText by remember { mutableStateOf("") }

    val versionName = remember {
        runCatching {
            ctx.packageManager.getPackageInfo(ctx.packageName, 0).versionName ?: "1.0"
        }.getOrDefault("1.0")
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

        // ------------------------------------------------------------------ language
        NpCard {
            SectionTitle(L("st_language"))
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                NpChip(
                    L("st_english"),
                    lang == "en",
                    Modifier.weight(1f)
                ) { Repo.setLanguage("en") }
                NpChip(
                    L("st_persian"),
                    lang == "fa",
                    Modifier.weight(1f)
                ) { Repo.setLanguage("fa") }
            }
        }

        // ------------------------------------------------------------------ interval
        NpCard {
            SectionTitle(L("st_monitor_interval"))
            Row(horizontalArrangement = Arrangement.spacedBy(7.dp)) {
                listOf(500L, 1000L, 2000L, 5000L).forEach { ms ->
                    NpChip(
                        text = "${ms} ms",
                        selected = interval == ms,
                        onClick = {
                            Repo.setMonitorInterval(ms)
                            Monitor.setInterval(ms)
                        }
                    )
                }
            }
            Gap(6)
            Text(L("st_battery_hint"), color = NpColors.Muted, fontSize = 11.sp)
        }

        // ------------------------------------------------------------------ battery
        NpCard {
            val ignoring = isIgnoringBattery(ctx)
            SwitchRow(
                label = L("st_battery"),
                checked = ignoring,
                onCheckedChange = { want ->
                    if (want) requestBatteryExemption(ctx) else openBatterySettings(ctx)
                },
                sub = L("st_battery_hint")
            )
        }

        // ------------------------------------------------------------------ backup
        NpCard {
            SectionTitle(L("st_export"))
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                NpButton(
                    text = L("st_export"),
                    modifier = Modifier.weight(1f)
                ) {
                    val json = com.netpilot.mobile.core.AppGraph.store
                        .snapshot(com.netpilot.mobile.data.Store.allDocuments())
                        .toString()
                    copyToClipboard(ctx, json)
                    toast = Strings.raw("copied")
                }
                GhostButton(
                    text = L("st_import"),
                    modifier = Modifier.weight(1f)
                ) {
                    val text = pasteFromClipboard(ctx)
                    if (text.isBlank()) {
                        // Nothing to import is a normal state (fresh install, clipboard used
                        // for something else), not a failure - say so instead of "went wrong".
                        toast = Strings.raw("st_import_empty")
                    } else {
                        importText = text
                        toast = runCatching {
                            val obj = org.json.JSONObject(text)
                            com.netpilot.mobile.core.AppGraph.store.restore(
                                obj,
                                com.netpilot.mobile.data.Store.allDocuments()
                            )
                            // The import only touched the store; without this the UI kept the
                            // pre-import lists in memory and the next edit wrote the old ones
                            // straight back over the imported data.
                            Repo.reloadAll()
                            Repo.loadLanguage()
                            Strings.raw("st_import")
                        }.getOrElse { Strings.raw("err_unknown") }
                    }
                }
            }
            if (importText.isNotBlank()) Gap(2)
            if (toast.isNotBlank()) {
                Gap(6)
                Text(toast, color = NpColors.Ok, fontSize = 11.sp)
            }
        }

        // ------------------------------------------------------------------ reset
        NpCard {
            SectionTitle(L("st_reset"))
            GhostButton(
                L("st_reset"),
                modifier = Modifier.fillMaxWidth(),
                accent = NpColors.Bad
            ) {
                Repo.clearAll()
                toast = Strings.raw("st_data_reset")
            }
        }

        // ------------------------------------------------------------------ about
        NpCard {
            SectionTitle(L("st_about"))
            InfoRow(L("st_version"), versionName)
            InfoRow(L("st_creator"), "esi")
            InfoRow(L("st_theme"), L("st_dark"))
            Gap(6)
            Text(
                "NetPilot Mobile · ${Build.MANUFACTURER} ${Build.MODEL} · API ${Build.VERSION.SDK_INT}",
                color = NpColors.Muted,
                fontSize = 10.sp
            )
        }

        Gap(4)
    }
}

@Composable
private fun InfoRow(k: String, v: String) {
    Row(Modifier.fillMaxWidth().padding(vertical = 3.dp)) {
        Text(k, color = NpColors.Muted, fontSize = 12.sp, modifier = Modifier.weight(1f))
        Text(v, color = NpColors.Text, fontSize = 12.sp, fontWeight = FontWeight.Medium)
    }
}

// ---------------------------------------------------------------------------- battery

private fun isIgnoringBattery(ctx: android.content.Context): Boolean = runCatching {
    val pm = ctx.getSystemService(android.content.Context.POWER_SERVICE) as? PowerManager
    pm?.isIgnoringBatteryOptimizations(ctx.packageName) == true
}.getOrDefault(false)

private fun requestBatteryExemption(ctx: android.content.Context) {
    runCatching {
        ctx.startActivity(
            Intent(
                AndroidSettings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS,
                Uri.parse("package:${ctx.packageName}")
            )
        )
    }.onFailure { openBatterySettings(ctx) }
}

private fun openBatterySettings(ctx: android.content.Context) {
    runCatching {
        ctx.startActivity(Intent(AndroidSettings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS))
    }
}

// ---------------------------------------------------------------------------- clipboard

private fun copyToClipboard(ctx: android.content.Context, text: String) {
    runCatching {
        val cm = ctx.getSystemService(android.content.Context.CLIPBOARD_SERVICE)
                as? android.content.ClipboardManager ?: return
        cm.setPrimaryClip(android.content.ClipData.newPlainText("netpilot", text))
    }
}

private fun pasteFromClipboard(ctx: android.content.Context): String = runCatching {
    val cm = ctx.getSystemService(android.content.Context.CLIPBOARD_SERVICE)
            as? android.content.ClipboardManager ?: return@runCatching ""
    val item = cm.primaryClip?.takeIf { it.itemCount > 0 }?.getItemAt(0) ?: return@runCatching ""
    item.coerceToText(ctx)?.toString() ?: ""
}.getOrDefault("")
