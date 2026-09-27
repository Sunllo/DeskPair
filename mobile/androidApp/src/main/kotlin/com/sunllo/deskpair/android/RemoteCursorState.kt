package com.sunllo.deskpair.android

import android.graphics.Bitmap
import android.graphics.Color

/**
 * The host's pointer, ready to draw.
 *
 * Shapes are kept by id because that is how the host sends them: the bitmap travels once, and after that
 * the host just names it. A session that moves between an arrow, an I-beam and a resize handle would
 * otherwise re-send three bitmaps every time the pointer crossed a window edge.
 */
data class RemoteCursorState(
    val x: Int,
    val y: Int,
    val shapeId: Long,
    private val shapes: Map<Long, CursorShape>,
) {
    val shape: CursorShape? get() = shapes[shapeId]

    fun at(x: Int, y: Int): RemoteCursorState = copy(x = x, y = y)

    fun usingShape(id: Long): RemoteCursorState = if (shapes.containsKey(id)) copy(shapeId = id) else this

    fun withShape(id: Long, hotX: Int, hotY: Int, width: Int, height: Int, bgra: ByteArray): RemoteCursorState {
        val decoded = CursorShape.decode(hotX, hotY, width, height, bgra) ?: return copy(shapeId = id)

        // Bounded, because a long session crossing many window edges would otherwise keep every shape it
        // ever saw. Sixteen is far more than any desktop actually cycles through.
        val kept = if (shapes.size >= 16) shapes.entries.drop(1).associate { it.toPair() } else shapes
        return copy(shapeId = id, shapes = kept + (id to decoded))
    }

    companion object {
        fun empty(): RemoteCursorState = RemoteCursorState(0, 0, 0L, emptyMap())
    }
}

/** One cursor bitmap and the point inside it that actually points. */
data class CursorShape(val hotX: Int, val hotY: Int, val bitmap: Bitmap) {
    companion object {
        /**
         * BGRA premultiplied, as the protocol carries it, into something Android can draw.
         *
         * Android has no BGRA config, so the channels are swapped here. Doing it once per shape rather
         * than per frame is the whole reason shapes are cached by id.
         */
        fun decode(hotX: Int, hotY: Int, width: Int, height: Int, bgra: ByteArray): CursorShape? {
            if (width <= 0 || height <= 0 || bgra.size < width * height * 4) {
                return null
            }

            val pixels = IntArray(width * height)
            for (i in pixels.indices) {
                val at = i * 4
                val b = bgra[at].toInt() and 0xFF
                val g = bgra[at + 1].toInt() and 0xFF
                val r = bgra[at + 2].toInt() and 0xFF
                val a = bgra[at + 3].toInt() and 0xFF
                pixels[i] = Color.argb(a, r, g, b)
            }

            val bitmap = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888)
            bitmap.setPixels(pixels, 0, width, 0, 0, width, height)
            return CursorShape(hotX, hotY, bitmap)
        }
    }
}
