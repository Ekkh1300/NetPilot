package com.netpilot.mobile.service

import android.app.Notification
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.Manifest
import android.os.Build
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import com.netpilot.mobile.App
import com.netpilot.mobile.R
import com.netpilot.mobile.data.LT
import com.netpilot.mobile.ui.MainActivity
import com.netpilot.mobile.vpn.NetPilotVpnService

/**
 * Persistent notification = the mobile equivalent of the desktop system-tray menu.
 *
 * It always shows which resolver is active and carries one-tap actions so DNS can be
 * switched (or restored) without opening the app — feature 14 of the spec.
 */
object QuickDns {

    private const val NOTIF_ID = 4711
    private const val ACT_CLOUDFLARE = 4701
    private const val ACT_GOOGLE = 4702
    private const val ACT_QUAD9 = 4703
    private const val ACT_RESTORE = 4704

    fun build(context: Context, dnsName: String?): Notification {
        val content = PendingIntent.getActivity(
            context, 0,
            Intent(context, MainActivity::class.java).apply {
                flags = Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP
            },
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )

        fun servicePending(id: Int, action: String, extraDnsId: String? = null): PendingIntent =
            PendingIntent.getService(
                context, id,
                Intent(context, NetPilotVpnService::class.java).apply {
                    this.action = action
                    extraDnsId?.let { putExtra(NetPilotVpnService.EXTRA_DNS_ID, it) }
                },
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
            )

        val active = dnsName ?: LT("dash_system_dns")
        val builder = NotificationCompat.Builder(context, App.CH_VPN)
            .setSmallIcon(R.drawable.ic_stat_np)
            .setContentTitle("${LT("notify_channel")} · ${LT("app_title")}")
            .setContentText(LT("notify_active").replace("%1\$s", active))
            .setContentIntent(content)
            .setOngoing(dnsName != null)
            .setOnlyAlertOnce(true)
            .setPriority(NotificationCompat.PRIORITY_LOW)
            .setCategory(NotificationCompat.CATEGORY_SERVICE)
            .setForegroundServiceBehavior(NotificationCompat.FOREGROUND_SERVICE_IMMEDIATE)
            .setShowWhen(false)

        builder.addAction(
            0, "Cloudflare",
            servicePending(ACT_CLOUDFLARE, NetPilotVpnService.ACTION_APPLY, "cloudflare")
        )
        builder.addAction(
            0, "Google",
            servicePending(ACT_GOOGLE, NetPilotVpnService.ACTION_APPLY, "google")
        )
        builder.addAction(
            0, LT("restore"),
            servicePending(ACT_RESTORE, NetPilotVpnService.ACTION_RESTORE)
        )
        if (dnsName != null) {
            builder.addAction(
                0, "Quad9",
                servicePending(ACT_QUAD9, NetPilotVpnService.ACTION_APPLY, "quad9")
            )
        }
        return builder.build()
    }

    /** Re-post with a new "active resolver" line (no-op when notifications are blocked). */
    fun update(context: Context, dnsName: String?) {
        runCatching {
            if (!NotificationManagerCompat.from(context).areNotificationsEnabled()) return
            // Android 13+: POST_NOTIFICATIONS is a runtime permission the user can refuse.
            // The post is dropped when it is missing rather than letting notify() throw.
            if (Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU ||
                context.checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) ==
                PackageManager.PERMISSION_GRANTED
            ) {
                NotificationManagerCompat.from(context)
                    .notify(NOTIF_ID, build(context, dnsName))
            }
        }
    }

    fun clear() {
        runCatching {
            val ctx = App.instance
            NotificationManagerCompat.from(ctx).cancel(NOTIF_ID)
        }
    }
}
