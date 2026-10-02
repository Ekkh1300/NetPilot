package com.netpilot.mobile.ui.components

import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.netpilot.mobile.data.formatRate
import com.netpilot.mobile.net.Monitor
import com.netpilot.mobile.ui.theme.NpColors
import kotlin.math.max

// ---------------------------------------------------------------------------- traffic chart

/**
 * Dual-series live chart (download + upload) with a gradient area fill.
 * The vertical scale animates between samples so the line glides instead of jumping.
 */
@Composable
fun TrafficChart(
    history: List<Monitor.Sample>,
    modifier: Modifier = Modifier,
    height: Dp = 132.dp,
    downloadColor: Color = NpColors.Accent,
    uploadColor: Color = NpColors.Chart2
) {
    val peak = history.maxOfOrNull { max(it.rx, it.tx) } ?: 0L
    val targetMax = max(peak, MIN_SCALE)
    val scale by animateFloatAsState(
        targetValue = targetMax.toFloat(),
        animationSpec = tween(450),
        label = "chartScale"
    )

    Box(
        modifier = modifier
            .fillMaxWidth()
            .height(height)
            .clip(RoundedCornerShape(14.dp))
            .background(NpColors.BgElevated)
            .padding(8.dp)
    ) {
        if (history.size < 2) {
            Text(
                "—",
                color = NpColors.Stroke,
                fontSize = 20.sp,
                modifier = Modifier.align(Alignment.Center)
            )
            return@Box
        }

        Canvas(Modifier.fillMaxWidth().height(height - 16.dp)) {
            val w = size.width
            val h = size.height
            val n = history.size
            val stepX = w / (n - 1).coerceAtLeast(1)
            val safeMax = if (scale <= 0f) 1f else scale

            // grid
            for (i in 1 until 4) {
                val y = h * i / 4f
                drawLine(
                    color = NpColors.Stroke.copy(alpha = 0.5f),
                    start = Offset(0f, y),
                    end = Offset(w, y),
                    strokeWidth = 1f
                )
            }

            fun points(selector: (Monitor.Sample) -> Long): List<Offset> =
                history.mapIndexed { i, s ->
                    val v = selector(s).toFloat().coerceAtMost(safeMax)
                    Offset(i * stepX, h - (v / safeMax) * (h - 4f))
                }

            val rxPts = points { it.rx }
            val txPts = points { it.tx }

            fun pathOf(pts: List<Offset>): Path {
                val p = Path()
                if (pts.isEmpty()) return p
                p.moveTo(pts[0].x, pts[0].y)
                for (i in 1 until pts.size) {
                    val prev = pts[i - 1]
                    val cur = pts[i]
                    val midX = (prev.x + cur.x) / 2f
                    p.cubicTo(midX, prev.y, midX, cur.y, cur.x, cur.y)
                }
                return p
            }

            // download area + line
            val rxPath = pathOf(rxPts)
            val area = Path().apply {
                addPath(rxPath)
                lineTo(rxPts.last().x, h)
                lineTo(rxPts.first().x, h)
                close()
            }
            drawPath(
                area,
                brush = Brush.verticalGradient(
                    colors = listOf(downloadColor.copy(alpha = 0.32f), Color.Transparent)
                )
            )
            drawPath(
                rxPath,
                color = downloadColor,
                style = Stroke(width = 3.5f, cap = StrokeCap.Round)
            )

            // upload line
            drawPath(
                pathOf(txPts),
                color = uploadColor,
                style = Stroke(width = 2.5f, cap = StrokeCap.Round)
            )

            // moving head dot
            val head = rxPts.last()
            drawCircle(color = downloadColor, radius = 4.5f, center = head)
            drawCircle(color = Color.White, radius = 2f, center = head)
        }

        Row(
            modifier = Modifier.align(Alignment.TopEnd).padding(top = 2.dp, end = 4.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            LegendDot(downloadColor)
            Spacer(Modifier.size(4.dp))
            Text("↓", color = downloadColor, fontSize = 10.sp)
            Spacer(Modifier.size(8.dp))
            LegendDot(uploadColor)
            Spacer(Modifier.size(4.dp))
            Text("↑", color = uploadColor, fontSize = 10.sp)
        }

        Text(
            text = "max " + formatRate(targetMax.toLong()),
            color = NpColors.Muted,
            fontSize = 9.sp,
            modifier = Modifier.align(Alignment.BottomEnd).padding(bottom = 2.dp, end = 4.dp)
        )
    }
}

@Composable
private fun LegendDot(color: Color) {
    Canvas(Modifier.size(7.dp)) { drawCircle(color) }
}

private const val MIN_SCALE = 256_000L  // 250 KB/s floor so idle traffic does not wobble

// ---------------------------------------------------------------------------- bar chart

/** Vertical bar chart used by Network History (daily / weekly / monthly). */
@Composable
fun BarChart(
    values: List<Float>,
    labels: List<String>,
    modifier: Modifier = Modifier,
    height: Dp = 140.dp,
    color: Color = NpColors.Accent,
    secondary: List<Float>? = null,
    secondaryColor: Color = NpColors.Chart2
) {
    val maxV = max(values.maxOrNull() ?: 0f, (secondary?.maxOrNull() ?: 0f)).coerceAtLeast(1f)
    val animated by animateFloatAsState(maxV, tween(400), label = "barMax")

    Column(
        modifier = modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(14.dp))
            .background(NpColors.BgElevated)
            .padding(10.dp)
    ) {
        Canvas(Modifier.fillMaxWidth().height(height)) {
            val w = size.width
            val h = size.height
            val n = values.size.coerceAtLeast(1)
            val slot = w / n
            val barW = (slot * 0.46f).coerceAtLeast(4f)
            val safeMax = if (animated <= 0f) 1f else animated

            for (i in values.indices) {
                val cx = slot * i + slot / 2f
                val v = values[i].coerceAtMost(safeMax)
                val barH = (v / safeMax) * (h - 6f)
                drawRoundRect(
                    color = color,
                    topLeft = Offset(cx - barW / 2f, h - barH),
                    size = Size(barW, barH),
                    cornerRadius = androidx.compose.ui.geometry.CornerRadius(barW / 2.4f)
                )
                secondary?.getOrNull(i)?.let { sv ->
                    val sh = (sv.coerceAtMost(safeMax) / safeMax) * (h - 6f)
                    drawRoundRect(
                        color = secondaryColor,
                        topLeft = Offset(cx + barW * 0.62f, h - sh),
                        size = Size(barW * 0.62f, sh),
                        cornerRadius = androidx.compose.ui.geometry.CornerRadius(barW / 3f)
                    )
                }
            }
            drawLine(
                color = NpColors.Stroke,
                start = Offset(0f, h - 1f),
                end = Offset(w, h - 1f),
                strokeWidth = 1f
            )
        }
        Spacer(Modifier.height(6.dp))
        Row(Modifier.fillMaxWidth()) {
            labels.forEach { label ->
                Text(
                    label,
                    color = NpColors.Muted,
                    fontSize = 9.sp,
                    modifier = Modifier.weight(1f),
                    maxLines = 1
                )
            }
        }
    }
}

// ---------------------------------------------------------------------------- progress ring

/** Health ring: a 270° arc with the score in the middle. */
@Composable
fun ProgressRing(
    progress: Float,
    modifier: Modifier = Modifier,
    size: Dp = 128.dp,
    stroke: Dp = 12.dp,
    color: Color = NpColors.Accent,
    label: String = "",
    sub: String = ""
) {
    val animated by animateFloatAsState(
        targetValue = progress.coerceIn(0f, 1f),
        animationSpec = tween(700),
        label = "ring"
    )
    Box(modifier = modifier.size(size), contentAlignment = Alignment.Center) {
        Canvas(Modifier.size(size)) {
            val dim = this.size.minDimension
            val st = stroke.toPx()
            val inset = st / 2f + 4f
            val topLeft = androidx.compose.ui.geometry.Offset(inset, inset)
            val arcSize = Size(dim - inset * 2, dim - inset * 2)
            drawArc(
                color = NpColors.Stroke,
                startAngle = -135f,
                sweepAngle = 270f,
                useCenter = false,
                topLeft = topLeft,
                size = arcSize,
                style = Stroke(width = st, cap = StrokeCap.Round)
            )
            drawArc(
                color = color,
                startAngle = -135f,
                sweepAngle = 270f * animated,
                useCenter = false,
                topLeft = topLeft,
                size = arcSize,
                style = Stroke(width = st, cap = StrokeCap.Round)
            )
        }
        Column(horizontalAlignment = Alignment.CenterHorizontally) {
            Text(
                "${(animated * 100).toInt()}%",
                color = NpColors.Text,
                fontSize = 26.sp,
                fontWeight = FontWeight.Bold
            )
            if (label.isNotEmpty()) {
                Text(label, color = color, fontSize = 11.sp, fontWeight = FontWeight.SemiBold)
            }
            if (sub.isNotEmpty()) {
                Text(sub, color = NpColors.Muted, fontSize = 9.sp)
            }
        }
    }
}

/** Horizontal proportional bar (per-app usage share, benchmark comparison). */
@Composable
fun HBar(
    fraction: Float,
    modifier: Modifier = Modifier,
    color: Color = NpColors.Accent,
    track: Color = NpColors.CardAlt,
    height: Dp = 7.dp
) {
    val animated by animateFloatAsState(
        targetValue = fraction.coerceIn(0f, 1f),
        animationSpec = tween(400),
        label = "hbar"
    )
    Canvas(modifier.fillMaxWidth().height(height)) {
        drawRoundRect(
            color = track,
            cornerRadius = androidx.compose.ui.geometry.CornerRadius(size.height / 2f)
        )
        drawRoundRect(
            color = color,
            size = Size(size.width * animated, size.height),
            cornerRadius = androidx.compose.ui.geometry.CornerRadius(size.height / 2f)
        )
    }
}
