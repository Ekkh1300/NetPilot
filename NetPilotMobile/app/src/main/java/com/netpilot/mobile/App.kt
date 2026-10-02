package com.netpilot.mobile

import android.app.Application
import android.app.NotificationChannel
import android.app.NotificationManager
import android.os.Build
import com.netpilot.mobile.core.AppGraph
import com.netpilot.mobile.pc.PcBridge
import com.netpilot.mobile.pc.PcLinkService
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch

/**
 * Application entry point. Owns the process-wide object graph (stores, engine
 * holders, bridges) so everything survives Activity recreation.
 */
class App : Application() {

    override fun onCreate() {
        super.onCreate()
        instance = this
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
