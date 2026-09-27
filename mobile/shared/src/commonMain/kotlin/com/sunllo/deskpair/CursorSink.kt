package com.sunllo.deskpair

/**
 * Where the host's pointer goes, supplied by the app.
 *
 * A phone has no pointer of its own, and in touch mode the finger covers whatever it is aiming at. Drawing
 * the host's own cursor on top of the picture is what turns a guess into a sight: you can see the I-beam
 * land between two characters before you commit to a tap.
 *
 * Shape and position arrive separately and at very different rates. The shape changes only when the host's
 * cursor changes — an arrow becoming a resize handle — and carries a bitmap, so it is cached by [id] and
 * re-sent rarely. The position changes constantly and costs eight bytes.
 *
 * The position is the host's own, not a dead-reckoned guess from what this client sent. That matters in
 * relative mode, where the host applies its own acceleration curve and any local estimate would drift.
 */
public interface CursorSink {

    /**
     * A new cursor shape, as BGRA premultiplied, [width] × [height].
     *
     * [hotX] and [hotY] are where inside the bitmap the pointer actually points — the tip of the arrow, the
     * centre of a crosshair. Drawing the image at the cursor position without subtracting the hotspot puts
     * the picture in the right place and the point in the wrong one.
     *
     * [id] identifies the shape so an app can keep the ones it has already decoded; the host sends a shape
     * once and then refers to it.
     */
    public fun onCursorShape(id: Long, hotX: Int, hotY: Int, width: Int, height: Int, bgra: ByteArray)

    /** The host's cursor moved, in the host's own coordinates. */
    public fun onCursorPosition(x: Int, y: Int)

    /** The host switched to a shape already sent. Apps that cache by id use this; others may ignore it. */
    public fun onCursorShapeChanged(id: Long) {}
}
