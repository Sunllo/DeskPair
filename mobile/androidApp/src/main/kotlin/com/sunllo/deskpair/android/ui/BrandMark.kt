package com.sunllo.deskpair.android.ui

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.layout.size
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.StrokeJoin
import androidx.compose.ui.graphics.drawscope.DrawScope
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.drawscope.withTransform
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp

/**
 * The DeskPair mark, drawn rather than shipped.
 *
 * The same 256-unit construction as `assets/logo/deskpair.svg` and
 * `tools/DeskPair.Tools.IconGen/Program.cs`: a 176 square rotated 45 degrees with corner radius 30,
 * crossed by two 24-wide chevrons offset 16 units above and below the centre line. Drawing it keeps one
 * definition of the geometry — a bitmap in `res/drawable` would be a second one, free to drift.
 *
 * The stagger is the mark. The two chevrons are not a mirrored pair on one baseline: the left-pointing one
 * sits low, the right-pointing one high, so they read as two arrows passing each other in opposite
 * directions. Anything that "tidies" them into alignment has thrown the meaning away.
 */
@Composable
fun BrandMark(modifier: Modifier = Modifier, size: Dp = 64.dp) {
    Canvas(modifier = modifier.size(size)) { drawBrandMark() }
}

private fun DrawScope.drawBrandMark() {
    // Everything below is in the logo's own 256 units; this is the only place the real size enters.
    val u = this.size.minDimension / 256f
    fun at(x: Float, y: Float) = Offset(x * u, y * u)

    withTransform({ rotate(degrees = 45f, pivot = at(128f, 128f)) }) {
        // The gradient axis is the unrotated square's own diagonal, which the rotation stands upright: on
        // screen the diamond runs pale cyan at its top point down to near-black navy at its bottom point.
        drawRoundRect(
            brush = Brush.linearGradient(
                0.00f to Logo.Sky,
                0.35f to Logo.Cyan,
                0.72f to Logo.Azure,
                1.00f to Logo.Navy,
                start = at(46f, 46f),
                end = at(210f, 210f),
            ),
            topLeft = at(40f, 40f),
            size = Size(176 * u, 176 * u),
            cornerRadius = CornerRadius(30 * u, 30 * u),
        )

        // A gloss bloom near the top vertex. Without it the diamond is a flat lozenge.
        drawRoundRect(
            brush = Brush.radialGradient(
                0.00f to Color.White.copy(alpha = 97f / 255f),
                0.55f to Color.White.copy(alpha = 15f / 255f),
                1.00f to Color.Transparent,
                center = at(92f, 80f),
                radius = 150 * u,
            ),
            topLeft = at(40f, 40f),
            size = Size(176 * u, 176 * u),
            cornerRadius = CornerRadius(30 * u, 30 * u),
        )
    }

    // Outside the transform: the chevrons are upright, and that is what sets them against the diamond.
    val chevron = Stroke(width = 24 * u, cap = StrokeCap.Round, join = StrokeJoin.Round)
    drawPath(bend(at(117f, 100f), at(74f, 144f), at(117f, 188f)), Color.White, style = chevron)
    drawPath(bend(at(139f, 68f), at(182f, 112f), at(139f, 156f)), Color.White, style = chevron)
}

private fun bend(from: Offset, apex: Offset, to: Offset): Path = Path().apply {
    moveTo(from.x, from.y)
    lineTo(apex.x, apex.y)
    lineTo(to.x, to.y)
}
