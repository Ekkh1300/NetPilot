package com.netpilot.mobile.ui

import android.Manifest
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import com.netpilot.mobile.vpn.NetPilotVpnService

class MainActivity : ComponentActivity() {

    /**
     * Android 13+ hides every notification — including the tunnel's — until the user
     * grants POST_NOTIFICATIONS. Asked once at startup; a denial is remembered by the
     * system, so the launcher simply returns immediately afterwards.
     */
    private val notificationPermission =
        registerForActivityResult(ActivityResultContracts.RequestPermission()) { }

    /**
     * Runs the system VPN consent dialog and replays the queued apply when it returns.
     * Launching it *for a result* is what makes ConfirmDialog keep its calling package and
     * actually stay on screen.
     */
    private val vpnConsent = registerForActivityResult(
        ActivityResultContracts.StartActivityForResult()
    ) {
        NetPilotVpnService.resumePending(this)
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        enableEdgeToEdge()
        super.onCreate(savedInstanceState)
        NetPilotVpnService.consentLauncher = { vpnConsent.launch(it) }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            notificationPermission.launch(Manifest.permission.POST_NOTIFICATIONS)
        }
        setContent {
            com.netpilot.mobile.ui.theme.NetPilotTheme {
                AppRoot()
            }
        }
    }

    /**
     * The system VPN consent dialog returns here; replay the DNS apply that was queued
     * behind it so "Apply" always finishes what the user asked for.
     */
    override fun onResume() {
        super.onResume()
        NetPilotVpnService.resumePending(this)
    }
}
