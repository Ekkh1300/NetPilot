package com.netpilot.mobile

import android.app.Application
import android.app.NotificationChannel
import android.app.NotificationManager
import android.os.Build
import com.netpilot.mobile.core.AppGraph
import com.netpilot.mobile.core.log.LogLevel
import com.netpilot.mobile.core.log.NpLog
import com.netpilot.mobile.pc.PcBridge
import com.netpilot.mobile.pc.PcLinkService
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import java.io.File

/**
 * Application entry point. Owns the process-wide object graph (stores, engine
 * holders, bridges) so everything survives Activity recreation.
 */
class App : Application() {

    override fun onCreate() {
        super.onCreate()
        instance = this

        // Logging first, before anything that can fail. This app had no logging at all - not
        // one android.util.Log call and no file - so a fault in the tunnel, the DNS forwarder
        // or the proxy produced nothing to look at and nothing to send. Now it produces a log
        // that is rotated, bounded, and stripped of the pairing token before it touches disk.
        NpLog.configure(File(filesDir, "logs"), LogLevel.INFO)
        // The relay is the busiest subsystem by an order of magnitude; Trace there would fill
        // the file with per-packet noise and bury the one line that explains a failure.
        NpLog.setCategoryLevel("probe", LogLevel.DEBUG)
        NpLog.info("app", "NetPilot ${BuildConfig.VERSION_NAME} starting")
        NpLog.info("app", "log file: ${NpLog.filePath ?: "(memory only)"}")

        AppGraph.init(this)
        createChannels()
        keepPcLinkAlive()
    }

    private val appScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    /**
     * Owns the `/report` loop for the whole process.
     *
     * The desktop drops the link after five minutes of silence, and a screen-scoped effect
     * would stop reporting the moment the user navigates away from the Mobile VPN page —
     * so pairing state is watched here instead. The keep-alive service is driven from the
     * same place: pairing decides whether the process is allowed to stay alive.
     */
    private fun keepPcLinkAlive() {
        appScope.launch {
            PcBridge.state.collect { state ->
                PcLinkService.setWanted(this@App, state.paired)
                if (state.paired) PcBridge.startReporting() else PcBridge.stopReporting()
            }
        }
    }

    private fun createChannels() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return
        val nm = getSystemService(NotificationManager::class.java) ?: return
        nm.createNotificationChannel(
            NotificationChannel(
                CH_VPN,
                getString(R.string.vpn_channel_name),
                NotificationManager.IMPORTANCE_LOW
            ).apply { description = getString(R.string.vpn_channel_desc) }
        )
        nm.createNotificationChannel(
            NotificationChannel(
                CH_MONITOR,
                getString(R.string.monitor_channel_name),
                NotificationManager.IMPORTANCE_LOW
            ).apply { description = getString(R.string.monitor_channel_desc) }
        )
    }

    companion object {
        const val CH_VPN = "netpilot_vpn"
        const val CH_MONITOR = "netpilot_monitor"

        @Volatile
        lateinit var instance: App
            private set
    }
}
