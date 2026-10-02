package com.netpilot.mobile.ui.components

import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.size
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import com.netpilot.mobile.ui.theme.NpColors
import kotlin.math.max
import kotlin.math.sin

data class OrbState(
    val connected: Boolean = false,
    val vpnActive: Boolean = false,
    val rxRate: Long = 0,
    val txRate: Long = 0
)

/**
 * Live network orb.
 *
 * - download traffic grows the inner core and speeds up the outer sweep,
 * - upload traffic adds a violet counter-orbit,
 * - a stable idle connection breathes slowly,
 * - no connection collapses the orb to a flat grey ring.
 *
 * A single infinite transition drives everything and the intensity follows a short moving
 * peak, so the animation stays smooth and cheap (one Canvas, no allocations per frame).
 */
@Composable
fun LiveOrb(
    state: OrbState,
    modifier: Modifier = Modifier,
    size: Dp = 168.dp
) {
    val transition = rememberInfiniteTransition(label = "orb")
    val phase by transition.animateFloat(
        initialValue = 0f,
        targetValue = 1f,
        animationSpec = infiniteRepeatable(
            animation = tween(durationMillis = 6000, easing = LinearEasing),
            repeatMode = RepeatMode.Restart
        ),
        label = "orbPhase"
    )

    // Moving peak keeps the orb from saturating during long transfers.
    var peak by remember { mutableFloatStateOf(1f) }
    val raw = ((state.rxRate + state.txRate).toFloat()).coerceAtLeast(0f)
    LaunchedEffect(state.rxRate, state.txRate) {
        peak = max(max(peak * 0.97f, raw), 8f * 1024f)
    }

    val intensity = if (!state.connected) 0f else (raw / peak).coerceIn(0f, 1f)
    val breath = 0.5f + 0.5f * sin(phase * 6.2831853f)
    val downShare = if (raw <= 0f) 0f else state.rxRate.toFloat() / raw

    val accent = when {
        !state.connected -> Color(0xFF4A5264)
        state.vpnActive -> NpColors.Ok
        else -> NpColors.Accent
    }
    val upColor = NpColors.Chart2

    Box(modifier = modifier.size(size), contentAlignment = Alignment.Center) {
        Canvas(Modifier.size(size)) {
            val c = this.size.minDimension / 2f
            val center = Offset(c, c)
            val base = c * 0.86f

            if (!state.connected) {
                // Flat grey ring + a slow dash to show "searching".
                drawCircle(
                    color = NpColors.Stroke,
                    radius = base * 0.78f,
                    style = Stroke(width = c * 0.075f)
                )
                val a0 = phase * 360f
                drawArc(
                    color = Color(0xFF6B7386),
                    startAngle = a0,
                    sweepAngle = 60f,
                    useCenter = false,
                    topLeft = Offset(c - base * 0.78f, c - base * 0.78f),
                    size = androidx.compose.ui.geometry.Size(
                        base * 1.56f, base * 1.56f
                    ),
                    style = Stroke(width = c * 0.075f, cap = StrokeCap.Round)
                )
                return@Canvas
            }

            // outer soft glow (grows with traffic + breath)
            val glowR = base * (0.82f + intensity * 0.16f + breath * 0.04f * (0.3f + intensity))
            drawCircle(
                brush = Brush.radialGradient(
                    colors = listOf(
                        accent.copy(alpha = 0.30f + intensity * 0.35f),
                        accent.copy(alpha = 0.06f),
                        Color.Transparent
                    ),
                    center = center,
                    radius = glowR * 1.45f
                ),
                radius = glowR * 1.45f,
                center = center
            )

            // base ring
            drawCircle(
                color = NpColors.CardAlt,
                radius = base * 0.80f,
                style = Stroke(width = c * 0.055f)
            )

            // activity sweep: idle = slow full circle, busy = a short fast comet
            val sweep = if (intensity < 0.04f) 360f else (40f + 260f * intensity)
            val start = if (intensity < 0.04f) 0f else phase * 720f * (0.4f + intensity)
            drawArc(
                color = accent,
                startAngle = start,
                sweepAngle = sweep,
                useCenter = false,
                topLeft = Offset(c - base * 0.80f, c - base * 0.80f),
                size = androidx.compose.ui.geometry.Size(base * 1.60f, base * 1.60f),
                style = Stroke(width = c * 0.055f, cap = StrokeCap.Round)
            )

            // upload counter-orbit (only visible while uploading)
            if (state.txRate > 0L && intensity > 0.01f) {
                val upFrac = 1f - downShare
                drawArc(
                    color = upColor,
                    startAngle = -phase * 720f * (0.3f + upFrac),
                    sweepAngle = 30f + 90f * upFrac,
                    useCenter = false,
                    topLeft = Offset(c - base * 0.66f, c - base * 0.66f),
                    size = androidx.compose.ui.geometry.Size(base * 1.32f, base * 1.32f),
                    style = Stroke(width = c * 0.030f, cap = StrokeCap.Round)
                )
            }

            // inner core: breathes when idle, swells with traffic
            val coreR = base * (0.40f + intensity * 0.20f + breath * 0.035f * (0.35f + intensity))
            drawCircle(
                brush = Brush.verticalGradient(
                    colors = listOf(accent.copy(alpha = 0.95f), accent.copy(alpha = 0.55f)),
                    startY = c - coreR,
                    endY = c + coreR
                ),
                radius = coreR,
                center = center
            )
            drawCircle(
                color = Color.White.copy(alpha = 0.18f + intensity * 0.25f),
                radius = coreR * 0.55f,
                center = Offset(c - coreR * 0.18f, c - coreR * 0.22f)
            )

            // two orbiting sparks — speed follows throughput
            val dotR = c * 0.045f
            val orbit = base * 0.92f
            val downAngle = phase * 360f * (1f + intensity * 5f)
            val a1 = Math.toRadians(downAngle.toDouble())
            drawCircle(
                color = accent,
                radius = dotR * (1f + intensity),
                center = Offset(
                    center.x + (orbit * Math.cos(a1)).toFloat(),
                    center.y + (orbit * Math.sin(a1)).toFloat()
                )
            )
            if (state.txRate > 0L) {
                val a2 = Math.toRadians(-downAngle * 0.8)
                drawCircle(
                    color = upColor,
                    radius = dotR * 0.85f,
                    center = Offset(
                        center.x + (orbit * Math.cos(a2)).toFloat(),
                        center.y + (orbit * Math.sin(a2)).toFloat()
                    )
                )
            }
        }
    }
}
