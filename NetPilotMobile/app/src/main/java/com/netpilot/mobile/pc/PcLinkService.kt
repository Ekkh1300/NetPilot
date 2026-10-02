package com.netpilot.mobile.pc

import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.os.IBinder
import androidx.core.app.NotificationCompat
import com.netpilot.mobile.App
import com.netpilot.mobile.R
import com.netpilot.mobile.data.LT
import com.netpilot.mobile.ui.MainActivity
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch

/**
 * Keeps the paired-PC link alive while the app is backgrounded.
 *
 * The desktop drops the link after five minutes without a `/report`, so simply closing
 * the app used to show "phone disconnected" on the PC even though nothing had changed —
 * and it stopped the share the PC was riding. This service only exists while `paired`
 * is true (see [App.keepPcLinkAlive], which is the single place that decides).
 *
 * The notification is published from `onCreate` *before* anything else: the service is
 * only ever started with `startForegroundService()`, and Android 12+ turns a missing
 * `startForeground()` into a process-killing exception.
 */
class PcLinkService : Service() {

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var watch: Job? = null

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        publish()
        watch = scope.launch { PcBridge.state.collect { publish() } }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        publish()
        return START_STICKY
    }

    override fun onDestroy() {
        watch?.cancel()
        scope.cancel()
        runCatching { stopForeground(STOP_FOREGROUND_REMOVE) }
        super.onDestroy()
    }

    /** Re-posts the notification with the current link state (cheap, idempotent). */
    private fun publish() {
        val s = PcBridge.state.value
        val text = when {
            s.host.isBlank() -> LT("mv_waiting")
            s.reachable -> LT("mv_link_live").replace("%1\$s", s.host)
            else -> LT("mv_link_wait").replace("%1\$s", s.host)
        }
        val content = PendingIntent.getActivity(
            this, 1,
            Intent(this, MainActivity::class.java).apply {
                flags = Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP
            },
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )
        val notif = NotificationCompat.Builder(this, App.CH_MONITOR)
            .setSmallIcon(R.drawable.ic_stat_np)
            .setContentTitle(LT("mv_title"))
            .setContentText(text)
            .setContentIntent(content)
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setPriority(NotificationCompat.PRIORITY_LOW)
            .setCategory(NotificationCompat.CATEGORY_SERVICE)
            .setForegroundServiceBehavior(NotificationCompat.FOREGROUND_SERVICE_IMMEDIATE)
            .setShowWhen(false)
            .build()
        runCatching { startForeground(NOTIF_ID, notif) }
    }

    companion object {
        private const val NOTIF_ID = 4712

        /** Mirrors what we last asked the system for, so the state flow can fire freely. */
        @Volatile
        private var wanted = false

        /**
         * Start or stop the keep-alive according to [paired]. Idempotent, and it forgets
         * a refused start so the next state change tries again — the first attempt can
         * legitimately fail when the process was launched in the background, where
         * Android 12+ forbids starting a foreground service.
         */
        fun setWanted(context: Context, paired: Boolean) {
            if (wanted == paired) return
            val app = context.applicationContext
            val intent = Intent(app, PcLinkService::class.java)
            wanted = if (paired) {
                runCatching { app.startForegroundService(intent) }.isSuccess
            } else {
                runCatching { app.stopService(intent) }
                false
            }
        }
    }
}
