package com.sunllo.deskpair

import platform.Foundation.NSData

/**
 * The cursor sink as Swift should see it.
 *
 * Same reason as [NativeVideoSink] and [NativeAudioSink]: a Kotlin ByteArray reaches Swift one element at a
 * time through the Objective-C bridge. A cursor bitmap is small and arrives rarely, so this matters less
 * than it does for video — but a 64×64 cursor is still sixteen thousand bridge calls, and an NSData can go
 * straight into a CGImage.
 */
public interface NativeCursorSink {
    public fun onCursorShape(id: Long, hotX: Int, hotY: Int, width: Int, height: Int, bgra: NSData)

    public fun onCursorPosition(x: Int, y: Int)

    public fun onCursorShapeChanged(id: Long)
}

/** Adapts a Swift-implemented [NativeCursorSink] to the sink the session expects. */
public class NativeCursorSinkAdapter(private val inner: NativeCursorSink) : CursorSink {
    override fun onCursorShape(id: Long, hotX: Int, hotY: Int, width: Int, height: Int, bgra: ByteArray): Unit =
        inner.onCursorShape(id, hotX, hotY, width, height, bgra.toNSData())

    override fun onCursorPosition(x: Int, y: Int): Unit = inner.onCursorPosition(x, y)

    override fun onCursorShapeChanged(id: Long): Unit = inner.onCursorShapeChanged(id)
}
