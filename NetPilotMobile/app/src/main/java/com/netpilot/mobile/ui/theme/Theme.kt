package com.netpilot.mobile.ui.theme

import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color

/**
 * NetPilot palette — mirrors the desktop app so both platforms feel like one product.
 */
object NpColors {
    val Bg = Color(0xFF12151C)
    val BgElevated = Color(0xFF171B26)
    val Card = Color(0xFF1A1F2B)
    val CardAlt = Color(0xFF20273A)
    val Stroke = Color(0xFF2A3346)
    val Accent = Color(0xFF38BDF8)
    val AccentDeep = Color(0xFF2A8FD6)
    val Text = Color(0xFFE6EAF2)
    val Muted = Color(0xFF96A0B8)
    val Ok = Color(0xFF34D399)
    val Warn = Color(0xFFFBBF24)
    val Bad = Color(0xFFF87171)
    val Chart2 = Color(0xFFA78BFA)
    val Chart3 = Color(0xFFF472B6)
}

private val Scheme = darkColorScheme(
    primary = NpColors.Accent,
    onPrimary = Color(0xFF06121B),
    secondary = NpColors.AccentDeep,
    background = NpColors.Bg,
    onBackground = NpColors.Text,
    surface = NpColors.Card,
    onSurface = NpColors.Text,
    surfaceVariant = NpColors.CardAlt,
    onSurfaceVariant = NpColors.Muted,
    outline = NpColors.Stroke,
    error = NpColors.Bad
)

@Composable
fun NetPilotTheme(content: @Composable () -> Unit) {
    // The desktop app is dark-only; keep the mobile app consistent.
    @Suppress("UNUSED_EXPRESSION") val ignored = isSystemInDarkTheme()
    MaterialTheme(colorScheme = Scheme, content = content)
}
