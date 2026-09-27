package com.sunllo.deskpair

import kotlin.math.max
import kotlin.math.min

/** A point on the host's desktop, in the host's own pixels. */
public data class HostPoint(val x: Int, val y: Int)

/** A point in the view showing that desktop, in the phone's points. */
public data class ViewPoint(val x: Float, val y: Float)

/**
 * Where the host's picture sits inside the view, and how big it is drawn.
 *
 * Immutable on purpose. Every operation returns a new state, so a gesture that turns out to be something
 * else — a pinch that started as a drag — can be abandoned without unwinding anything, and so the whole
 * geometry can be tested without a screen. Each app wraps one of these in whatever its UI framework
 * observes; the arithmetic lives here once rather than being written twice and drifting.
 *
 * A phone screen is far smaller than a desktop, and squeezing 2560×1440 into six inches makes everything
 * smaller than a fingertip. So the picture is not locked to the viewport: it can be zoomed past fitting and
 * panned around, and the mapping from a touch back to a host pixel has to account for both.
 */
public data class CanvasState(
    val hostWidth: Int,
    val hostHeight: Int,
    val viewWidth: Float,
    val viewHeight: Float,
    /** Points of view per host pixel. 1.0 draws the desktop at its true size. */
    val scale: Float,
    /** Where the picture's top-left corner sits in the view. Negative means it starts off-screen. */
    val x: Float,
    val y: Float,
) {

    /** The scale at which the whole desktop is visible, letterboxed. */
    public val fitScale: Float
        get() = if (hostWidth <= 0 || hostHeight <= 0 || viewWidth <= 0f || viewHeight <= 0f) {
            1f
        } else {
            min(viewWidth / hostWidth, viewHeight / hostHeight)
        }

    /**
     * Zoom limits, relative to the content rather than fixed numbers.
     *
     * You can always reach 150%, however large the desktop — otherwise a 4K host on a phone could never be
     * magnified enough to hit a checkbox. And you can always pull back to half again wider than fitting, so
     * there is somewhere to go when you have panned into a corner and lost your bearings.
     */
    public val minScale: Float get() = fitScale / 1.5f

    public val maxScale: Float get() = max(1.5f, fitScale)

    /** True when the whole desktop is visible and centred, which is what the "fit" control returns to. */
    public val isFitted: Boolean
        get() = kotlin.math.abs(scale - fitScale) < 0.001f

    /** The whole desktop, centred, with black bars on whichever axis has room to spare. */
    public fun fitted(): CanvasState {
        val s = fitScale
        return copy(
            scale = s,
            x = (viewWidth - hostWidth * s) / 2f,
            y = (viewHeight - hostHeight * s) / 2f,
        )
    }

    /** The desktop at its true size, centred on the same host pixel that was at the view's centre. */
    public fun actualSize(): CanvasState = zoomed(1f / scale, viewWidth / 2f, viewHeight / 2f)

    /**
     * Scales by [factor] about a fixed point.
     *
     * The focal point stays under the same host pixel throughout, which is what makes a pinch feel attached
     * to the fingers rather than to the screen. Derived from wanting `toHost(focal)` unchanged: the new
     * offset is the focal point minus the old distance to it, rescaled.
     */
    public fun zoomed(factor: Float, focalX: Float, focalY: Float): CanvasState {
        val wanted = (scale * factor).coerceIn(minScale, maxScale)
        if (wanted == scale) {
            return this
        }

        return copy(
            scale = wanted,
            x = focalX - (focalX - x) / scale * wanted,
            y = focalY - (focalY - y) / scale * wanted,
        )
    }

    /**
     * Slides the picture.
     *
     * Deliberately unbounded: clamping a pan to keep the picture on screen fights the user during a pinch,
     * where the focal point legitimately drags the image well past the edge before the zoom catches up.
     * The escape hatch is a reset control, not a constraint.
     */
    public fun panned(dx: Float, dy: Float): CanvasState = copy(x = x + dx, y = y + dy)

    /**
     * The view changed size — rotation, a keyboard appearing, a split screen.
     *
     * A canvas that was fitted stays fitted, because that is plainly what the user wants after a rotation.
     * One that had been zoomed keeps its scale and its centre, because they zoomed there on purpose.
     */
    public fun resized(newWidth: Float, newHeight: Float): CanvasState {
        if (newWidth <= 0f || newHeight <= 0f) {
            return this
        }
        if (viewWidth <= 0f || viewHeight <= 0f) {
            return copy(viewWidth = newWidth, viewHeight = newHeight).fitted()
        }

        val wasFitted = isFitted
        val centre = toHost(viewWidth / 2f, viewHeight / 2f)
        val resized = copy(viewWidth = newWidth, viewHeight = newHeight)
        return if (wasFitted) resized.fitted() else resized.centredOn(centre)
    }

    /** Puts a host pixel in the middle of the view, without changing the scale. */
    public fun centredOn(point: HostPoint): CanvasState = copy(
        x = viewWidth / 2f - point.x * scale,
        y = viewHeight / 2f - point.y * scale,
    )

    /**
     * Brings a host pixel into view if it is not already, moving as little as possible.
     *
     * Used when the soft keyboard shrinks the canvas: the cursor should still be visible, but yanking the
     * picture to centre it would lose the context the user was looking at.
     */
    public fun revealing(point: HostPoint, margin: Float = 48f): CanvasState {
        val at = toView(point.x, point.y)
        var dx = 0f
        var dy = 0f

        if (at.x < margin) dx = margin - at.x
        if (at.x > viewWidth - margin) dx = viewWidth - margin - at.x
        if (at.y < margin) dy = margin - at.y
        if (at.y > viewHeight - margin) dy = viewHeight - margin - at.y

        return if (dx == 0f && dy == 0f) this else panned(dx, dy)
    }

    /**
     * A touch, as a host pixel.
     *
     * Clamped to the desktop: a finger on the black bar beside a letterboxed picture still means the
     * nearest real pixel, which is better than dropping the gesture or sending a coordinate the host will
     * reject.
     */
    public fun toHost(viewX: Float, viewY: Float): HostPoint {
        if (scale <= 0f) {
            return HostPoint(0, 0)
        }

        // Rounded, not truncated. A touch means the nearest pixel rather than the one below and left of
        // it, and truncation also breaks the round trip: toView(1280) comes back as 1279.9999, which
        // truncates to the wrong pixel and puts every mapping one out.
        val hx = kotlin.math.round((viewX - x) / scale).toInt().coerceIn(0, max(0, hostWidth - 1))
        val hy = kotlin.math.round((viewY - y) / scale).toInt().coerceIn(0, max(0, hostHeight - 1))
        return HostPoint(hx, hy)
    }

    /** Where a host pixel is drawn. Not clamped — the caller may well want to know it is off-screen. */
    public fun toView(hostX: Int, hostY: Int): ViewPoint = ViewPoint(x + hostX * scale, y + hostY * scale)

    public companion object {
        /** A canvas that knows nothing yet. Becomes real once a frame and a view size have arrived. */
        public fun initial(): CanvasState = CanvasState(0, 0, 0f, 0f, 1f, 0f, 0f)

        /** The host's size changed — a different display, or a resolution change mid-session. */
        public fun forHost(previous: CanvasState, width: Int, height: Int): CanvasState =
            if (previous.hostWidth == width && previous.hostHeight == height) {
                previous
            } else {
                previous.copy(hostWidth = width, hostHeight = height).fitted()
            }
    }
}
