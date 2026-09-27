package com.sunllo.deskpair

import kotlinx.cinterop.ExperimentalForeignApi
import kotlinx.cinterop.addressOf
import kotlinx.cinterop.usePinned
import platform.Foundation.NSData
import platform.Foundation.create

/**
 * The video sink as Swift should see it.
 *
 * [VideoSink] hands over a Kotlin ByteArray, which Swift can only read one element at a time through the
 * Objective-C bridge — fifty thousand calls for a single 50 KB frame, sixty times a second. This wraps it so
 * the Swift side receives an NSData whose bytes it can pass straight to VideoToolbox.
 */
public interface NativeVideoSink {
    /** What this device decodes; see [VideoSink.formats]. */
    public val formats: Set<VideoFormat>

    /**
     * [vp9] is the stream's configuration, read here from each VP9 keyframe's header, because VideoToolbox
     * needs it before it will decode anything and has no container to find it in. Null for every other frame.
     */
    public fun onFrame(
        data: NSData,
        isKeyFrame: Boolean,
        width: Int,
        height: Int,
        format: VideoFormat,
        vp9: Vp9Configuration?,
    ): Boolean
}

/** Adapts a Swift-implemented [NativeVideoSink] to the sink the session expects. */
public class NativeVideoSinkAdapter(private val inner: NativeVideoSink) : VideoSink {
    override val formats: Set<VideoFormat> get() = inner.formats

    override fun onFrame(data: ByteArray, isKeyFrame: Boolean, width: Int, height: Int, format: VideoFormat): Boolean {
        val vp9 = if (format == VideoFormat.VP9 && isKeyFrame) Vp9Configuration.parse(data) else null
        return inner.onFrame(data.toNSData(), isKeyFrame, width, height, format, vp9)
    }
}

@OptIn(ExperimentalForeignApi::class)
internal fun ByteArray.toNSData(): NSData {
    if (isEmpty()) {
        return NSData()
    }

    // One copy, done by the system, rather than a per-byte round trip through the bridge.
    return usePinned { pinned ->
        NSData.create(bytes = pinned.addressOf(0), length = size.toULong())
    }
}
