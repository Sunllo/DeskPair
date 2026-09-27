package com.sunllo.deskpair.audio

/**
 * Where the host's sound goes, supplied by the app.
 *
 * The shared module never decodes audio, for the same reason it never decodes video: the decoder belongs to
 * the platform, and handing it compressed frames is both cheaper and the only way to reach the hardware one.
 * Opus at 48 kHz, in 10 ms frames, which is what the host encodes.
 *
 * A host suppresses silence after a few seconds, so a gap in [onFrame] means nothing was playing rather than
 * that something was lost. A sink that fills the gap with silence of its own will not drift.
 */
public interface AudioSink {

    /**
     * The stream's shape, before any frame arrives and again if the host's output device changes.
     *
     * A decoder usually has to be created against this, so it comes as its own call rather than as fields on
     * every frame.
     */
    public fun onFormat(sampleRate: Int, channels: Int)

    /** One Opus packet. [ptsMs] is the host's clock, useful for ordering rather than for scheduling. */
    public fun onFrame(opus: ByteArray, ptsMs: Long)
}
