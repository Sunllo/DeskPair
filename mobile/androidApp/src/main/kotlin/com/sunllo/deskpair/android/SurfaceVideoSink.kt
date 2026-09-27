package com.sunllo.deskpair.android

import android.media.MediaCodec
import android.media.MediaCodecList
import android.media.MediaFormat
import android.util.Log
import android.view.Surface
import com.sunllo.deskpair.VideoFormat
import java.nio.ByteBuffer

/**
 * Decodes H.264 or VP9 straight onto a [Surface].
 *
 * The desktop client decodes to a BGRA buffer because Avalonia needs one, and pays a copy of the whole
 * frame every time. Here the encoded bytes go into MediaCodec and the picture comes out the other side
 * already on the display — no pixel ever crosses into managed memory, which on a phone is the difference
 * between a warm device at 15 fps and a cool one at 60.
 *
 * MediaCodec is a pipeline, so it legitimately produces nothing for the first several frames while it fills.
 * That is not an error and must not be treated as one: the session's own policy already tolerates a run of
 * silent decodes before it asks for a fresh keyframe.
 */
class SurfaceVideoSink(private val surface: Surface) : AutoCloseable {

    companion object {
        private const val TAG = "DeskPair/Video"
        private const val DEQUEUE_TIMEOUT_US = 10_000L

        private fun mimeOf(format: VideoFormat): String? = when (format) {
            VideoFormat.H264 -> MediaFormat.MIMETYPE_VIDEO_AVC
            VideoFormat.VP9 -> MediaFormat.MIMETYPE_VIDEO_VP9
            else -> null
        }

        /**
         * What this device decodes, as the session tells the host. H.264 always, as before: it is what every
         * host can send and what a phone decodes in hardware. VP9 when MediaCodec has a decoder for a desk-sized
         * picture, which is what a host without an H.264 encoder -- a Linux machine with neither VAAPI nor
         * OpenH264 -- can send, and without which such a host could not be watched from a phone at all.
         */
        val formats: Set<VideoFormat> by lazy {
            val list = MediaCodecList(MediaCodecList.REGULAR_CODECS)
            val vp9 = runCatching {
                list.findDecoderForFormat(MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_VP9, 1920, 1080)) != null
            }.getOrDefault(false)
            if (vp9) setOf(VideoFormat.H264, VideoFormat.VP9) else setOf(VideoFormat.H264)
        }
    }

    private var codec: MediaCodec? = null
    private var configuredMime: String? = null
    private var configuredWidth = 0
    private var configuredHeight = 0

    /** Frames the decoder has actually rendered. The Android test asserts on this rather than on a screenshot alone. */
    @Volatile
    var renderedFrames: Int = 0
        private set

    @Volatile
    var lastError: String? = null
        private set

    /**
     * Told when the decoder fails, so a screen can say so.
     *
     * [lastError] was written in two places and read in none, which meant a device with no H.264 decoder
     * showed a permanently black picture under a healthy green signal chip and said nothing at all.
     */
    var onProblem: ((String?) -> Unit)? = null

    /**
     * Feeds one access unit. [isKeyFrame] matters because a decoder started mid-stream produces nothing
     * until it sees one, and feeding it inter frames in that state wastes work and hides the real problem.
     *
     * Returns false when the frame could not be accepted, which the caller answers by asking the host for a
     * keyframe rather than by giving up.
     */
    fun decode(data: ByteArray, isKeyFrame: Boolean, width: Int, height: Int, format: VideoFormat): Boolean {
        try {
            val mime = mimeOf(format) ?: return unsupported(format)
            val codec = ensureCodec(mime, width, height) ?: return false

            val inputIndex = codec.dequeueInputBuffer(DEQUEUE_TIMEOUT_US)
            if (inputIndex < 0) {
                // Every input buffer is still in flight. Dropping an inter frame is survivable; dropping a
                // keyframe is not, so say so and let the caller ask again.
                return !isKeyFrame
            }

            val buffer: ByteBuffer = codec.getInputBuffer(inputIndex) ?: return false
            buffer.clear()
            if (buffer.capacity() < data.size) {
                Log.w(TAG, "Frame of ${data.size} bytes does not fit a ${buffer.capacity()} byte input buffer")
                return false
            }

            buffer.put(data)
            codec.queueInputBuffer(
                inputIndex,
                0,
                data.size,
                System.nanoTime() / 1000,
                if (isKeyFrame) MediaCodec.BUFFER_FLAG_KEY_FRAME else 0,
            )

            drainOutput(codec)
            return true
        } catch (e: IllegalStateException) {
            // The codec died, usually because the surface went away underneath it.
            lastError = e.message
            // Not reported: this one recovers on the next keyframe, and a notice that flickers up for
            // every transient codec reset is noise rather than news.
            Log.w(TAG, "Decoder failed; it will be rebuilt on the next keyframe", e)
            release()
            return false
        }
    }

    /** Releases rendered frames to the surface. Buffers held here are buffers the decoder cannot reuse. */
    private fun drainOutput(codec: MediaCodec) {
        val info = MediaCodec.BufferInfo()
        while (true) {
            when (val index = codec.dequeueOutputBuffer(info, 0)) {
                MediaCodec.INFO_TRY_AGAIN_LATER -> return
                MediaCodec.INFO_OUTPUT_FORMAT_CHANGED -> Log.i(TAG, "Decoder format: ${codec.outputFormat}")
                else -> if (index >= 0) {
                    // true means "show it": the surface receives the frame here, not on some later call.
                    codec.releaseOutputBuffer(index, true)
                    renderedFrames++
                }
            }
        }
    }

    /** A format this sink never claimed; the host sends only what the session listed, so this is a newer host. */
    private fun unsupported(format: VideoFormat): Boolean {
        if (lastError == null) {
            lastError = format.name
            onProblem?.invoke(lastError)
        }
        return false
    }

    /**
     * The host can change resolution mid-session, so the codec is rebuilt when the picture does — the
     * desktop learned this the hard way, where a stale size silently cropped the picture for the rest of the
     * session rather than failing. And when the format does: a viewer joining that cannot decode the stream
     * makes the host start it again in another one, keyframe first.
     */
    private fun ensureCodec(mime: String, width: Int, height: Int): MediaCodec? {
        val existing = codec
        if (existing != null && mime == configuredMime && width == configuredWidth && height == configuredHeight) {
            return existing
        }

        release()
        if (width <= 0 || height <= 0) {
            return null
        }

        return try {
            MediaCodec.createDecoderByType(mime).apply {
                configure(MediaFormat.createVideoFormat(mime, width, height), surface, null, 0)
                start()
            }.also {
                codec = it
                configuredMime = mime
                configuredWidth = width
                configuredHeight = height
                Log.i(TAG, "Decoder ${it.name} started for $mime ${width}x$height")
            }
        } catch (e: Exception) {
            // The class name when there is no message: "No H.264 decoder:" with nothing after the
            // colon is worse than useless to whoever is being asked about it.
            // Blank as well as null: MediaCodec throws with an empty message often enough that
            // "No H.264 decoder:" with nothing after the colon was what actually reached the screen.
            // The detail only; the sentence around it is the app's, in the reader's language (video_unavailable).
            lastError = e.message?.takeIf { m -> m.isNotBlank() } ?: e::class.simpleName.orEmpty()
            onProblem?.invoke(lastError)
            Log.e(TAG, "Could not start a decoder for ${width}x$height", e)
            null
        }
    }

    private fun release() {
        codec?.let {
            runCatching { it.stop() }
            runCatching { it.release() }
        }
        codec = null
        configuredMime = null
        configuredWidth = 0
        configuredHeight = 0
    }

    override fun close() = release()
}
