package com.netpilot.mobile.pc

import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.os.Build
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
        // Reaching onCreate at all is the proof that the start was granted. The caller cannot
        // know this - startForegroundService returning without throwing means the request was
        // accepted, not that Android allowed it - so the service reports its own state and the
        // UI reads that instead of assuming.
        markRunning()
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
        // So the next setWanted(true) is not short-circuited by a stale "wanted".
        markStopped()
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

        /**
         * Mirrors what we last asked the system for, so the state flow can fire freely.
         *
         * Not proof the service is running - see [running]. It records the last request so a
         * repeated call with the same value does nothing.
         */
        @Volatile
        private var wanted = false

        /**
         * Set by the service itself once it is actually in the foreground, so a start that
         * appeared to succeed can be checked afterwards.
         *
         * This is the distinction the old code got wrong. `startForegroundService` returning
         * without throwing says only that the call was accepted; Android then decides whether the
         * app was allowed to start one. From the background - which is exactly how this is
         * reached, since pairing is reported by a background report loop - API 31+ forbids it and
         * the service never runs. `isSuccess` was true for that.
         *
         * So the report of whether it worked comes from the service, not the caller.
         */
        @Volatile
        var running: Boolean = false
            private set

        /**
         * How many times a start was refused, so the failure is visible instead of silent.
         */
        @Volatile
        var startFailures: Int = 0
            private set

        /** Called by the service once startForeground has succeeded. */
        fun markRunning() {
            running = true
            wanted = true
        }

        /** Called by the service when it stops for any reason. */
        fun markStopped() {
            running = false
            wanted = false
        }

        /**
         * Start or stop the keep-alive according to [paired].
         *
         * Idempotent, and - the part that mattered - a refused start does not latch. The old code
         * assigned the result of `runCatching{...}.isSuccess` to [wanted], so one background start
         * that Android declined set `wanted` to false, and since the next call short-circuits on
         * `wanted == paired`, nothing ever tried again for the rest of the process's life. Pairing
         * succeeded, the UI said it did, and the keep-alive simply never ran.
         *
         * Two things are now recorded separately: what was asked for ([wanted]) and what is
         * actually running ([running]). Only [wanted] gates a repeat call, and it is set
         * optimistically so a later state change is free to retry.
         */
        fun setWanted(context: Context, paired: Boolean) {
            if (wanted == paired && (paired == running || !paired)) return

            val app = context.applicationContext
            val intent = Intent(app, PcLinkService::class.java)

            if (!paired) {
                runCatching { app.stopService(intent) }
                    .onFailure { com.netpilot.mobile.core.log.NpLog.warn("pclink", "stop refused", it) }
                running = false
                wanted = false
                return
            }

            // Set before the call, not from its result: whether Android grants the start is not
            // known here, and latching on a local guess is what left the service dead.
            wanted = true

            try {
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                    app.startForegroundService(intent)
                } else {
                    app.startService(intent)
                }
            } catch (t: Throwable) {
                startFailures++
                // The request stays wanted, so the next state change retries - which is the point.
                com.netpilot.mobile.core.log.NpLog.error(
                    "pclink",
                    "the system refused to start the PC link service. This is expected when the " +
                        "app is in the background, which is where pairing usually arrives; the " +
                        "next state change will try again",
                    t
                )
            }
        }
    }
}
