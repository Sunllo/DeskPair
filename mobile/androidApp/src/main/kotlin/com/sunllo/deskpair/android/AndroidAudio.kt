package com.sunllo.deskpair.android

import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioTrack
import android.media.MediaCodec
import android.media.MediaFormat
import android.util.Log
import com.sunllo.deskpair.audio.AudioSink
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.concurrent.ArrayBlockingQueue
import java.util.concurrent.TimeUnit

/**
 * Plays the host's sound through MediaCodec and AudioTrack.
 *
 * Decoding happens on a thread of its own. The session's receive loop also carries video, and blocking it
 * while a codec finds an input buffer would stall the picture to keep the sound perfectly intact — the wrong
 * trade in a remote desktop. Packets queue, and the queue drops the oldest when it fills, because late audio
 * is worse than missing audio.
 */
class AndroidAudio : AudioSink {

    /**
     * Told when sound stops working, so a screen can say so rather than the session simply being quiet.
     *
     * The equivalent of iOS's `IosAudio.onProblem`, which this side did not have: a failure here was a
     * `Log.w` and nothing more, so "no sound" and "the host is not playing anything" looked identical.
     */
    var onProblem: ((String?) -> Unit)? = null

    private companion object {
        const val TAG = "DeskPair"

        /** 10 ms frames at 48 kHz, so a second of backlog is already far more than anyone would tolerate. */
        const val QUEUE_DEPTH = 32

        /**
         * Opus pre-skip, in samples at 48 kHz, and the same figure as nanoseconds for the two delay fields
         * Android wants. 3840 samples is 80 ms, the value the reference encoder uses.
         */
        const val PRE_SKIP_SAMPLES = 3840
        const val PRE_SKIP_NANOS = 80_000_000L
    }

    private class Packet(val data: ByteArray, val ptsUs: Long)

    private val queue = ArrayBlockingQueue<Packet>(QUEUE_DEPTH)

    @Volatile
    private var worker: Thread? = null

    @Volatile
    private var running = false

    private var sampleRate = 0
    private var channels = 0

    /** Frames handed to the decoder, for a test to assert on something other than a person listening. */
    @Volatile
    var framesPlayed = 0L
        private set

    @Volatile
    private var samplesPlayed = 0L

    override fun onFormat(sampleRate: Int, channels: Int) {
        if (sampleRate == this.sampleRate && channels == this.channels && running) {
            return
        }

        stop()
        this.sampleRate = sampleRate
        this.channels = channels
        start()
    }

    override fun onFrame(opus: ByteArray, ptsMs: Long) {
        if (!running) {
            // The host sends the format first, but a reconnect can put a frame in front of it.
            return
        }

        val packet = Packet(opus, ptsMs * 1000)
        if (!queue.offer(packet)) {
            queue.poll()
            queue.offer(packet)
        }
    }

    fun stop() {
        running = false
        worker?.interrupt()
        worker = null
        queue.clear()
    }

    private fun start() {
        running = true
        worker = Thread({ decodeLoop() }, "deskpair-audio").apply {
            isDaemon = true
            start()
        }
    }

    private fun decodeLoop() {
        var codec: MediaCodec? = null
        var track: AudioTrack? = null
        try {
            codec = MediaCodec.createDecoderByType(MediaFormat.MIMETYPE_AUDIO_OPUS).apply {
                configure(opusFormat(), null, null, 0)
                start()
            }
            track = createTrack().apply { play() }

            val info = MediaCodec.BufferInfo()
            while (running && !Thread.currentThread().isInterrupted) {
                queue.poll(200, TimeUnit.MILLISECONDS)?.let { packet ->
                    val index = codec.dequeueInputBuffer(20_000)
                    if (index >= 0) {
                        codec.getInputBuffer(index)?.apply {
                            clear()
                            put(packet.data)
                        }
                        codec.queueInputBuffer(index, 0, packet.data.size, packet.ptsUs, 0)
                        framesPlayed++
                        // Counted where packets go in rather than where samples come out: one input can
                        // drain as several output buffers, and logging there prints the same milestone
                        // twice. The first line proves the decoder works; five seconds apart after that
                        // tells a stopped stream from one the host is simply not filling.
                        if (framesPlayed == 1L || framesPlayed % 500 == 0L) {
                            Log.d(TAG, "audio decoded $framesPlayed packet(s), $samplesPlayed samples")
                        }
                    }
                }

                var out = codec.dequeueOutputBuffer(info, 0)
                while (out >= 0) {
                    codec.getOutputBuffer(out)?.let { buffer ->
                        val pcm = ByteArray(info.size)
                        buffer.position(info.offset)
                        buffer.get(pcm)
                        track.write(pcm, 0, pcm.size)
                        samplesPlayed += pcm.size / 2
                    }
                    codec.releaseOutputBuffer(out, false)
                    out = codec.dequeueOutputBuffer(info, 0)
                }
            }
        } catch (_: InterruptedException) {
        } catch (e: Exception) {
            // Was a log line and nothing else, so a device that could not decode Opus was simply silent —
            // indistinguishable from a host that is not playing anything. iOS has said so for a while.
            onProblem?.invoke(e.message ?: e::class.simpleName)
            Log.w(TAG, "Audio stopped", e)
        } finally {
            runCatching { codec?.stop() }
            runCatching { codec?.release() }
            runCatching { track?.stop() }
            runCatching { track?.release() }
        }
    }

    /**
     * Opus needs three pieces of codec-specific data, and MediaCodec will not start without them: the
     * OpusHead identification header, then the codec delay and the seek pre-roll as nanoseconds. The stream
     * carries none of this — it is raw packets — so the standard values are rebuilt here.
     */
    private fun opusFormat(): MediaFormat =
        MediaFormat.createAudioFormat(MediaFormat.MIMETYPE_AUDIO_OPUS, sampleRate, channels).apply {
            setByteBuffer("csd-0", opusHead())
            setByteBuffer("csd-1", nanos(PRE_SKIP_NANOS))
            setByteBuffer("csd-2", nanos(PRE_SKIP_NANOS))
        }

    private fun opusHead(): ByteBuffer = ByteBuffer.allocate(19).order(ByteOrder.LITTLE_ENDIAN).apply {
        put("OpusHead".toByteArray(Charsets.US_ASCII))
        put(1)                                   // version
        put(channels.toByte())
        putShort(PRE_SKIP_SAMPLES.toShort())
        putInt(sampleRate)
        putShort(0)                              // output gain
        put(0)                                   // channel mapping family: stereo as-is
        flip()
    }

    private fun nanos(value: Long): ByteBuffer =
        ByteBuffer.allocate(8).order(ByteOrder.LITTLE_ENDIAN).putLong(value).apply { flip() }

    private fun createTrack(): AudioTrack {
        val mask = if (channels >= 2) AudioFormat.CHANNEL_OUT_STEREO else AudioFormat.CHANNEL_OUT_MONO
        val minimum = AudioTrack.getMinBufferSize(sampleRate, mask, AudioFormat.ENCODING_PCM_16BIT)

        return AudioTrack.Builder()
            .setAudioAttributes(
                AudioAttributes.Builder()
                    // Media rather than voice: this is the desktop's sound, and it should follow the media
                    // volume the user already set.
                    .setUsage(AudioAttributes.USAGE_MEDIA)
                    .setContentType(AudioAttributes.CONTENT_TYPE_MOVIE)
                    .build(),
            )
            .setAudioFormat(
                AudioFormat.Builder()
                    .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                    .setSampleRate(sampleRate)
                    .setChannelMask(mask)
                    .build(),
            )
            // Two device buffers: enough to ride out a scheduling hiccup without adding audible latency.
            .setBufferSizeInBytes(maxOf(minimum * 2, minimum))
            .setTransferMode(AudioTrack.MODE_STREAM)
            .build()
    }
}
