package com.sunllo.deskpair

import com.sunllo.deskpair.audio.AudioSink
import platform.Foundation.NSData

/**
 * The audio sink as Swift should see it.
 *
 * Same reason as [NativeVideoSink]: a Kotlin ByteArray reaches Swift one element at a time through the
 * Objective-C bridge, and an Opus packet arrives every ten milliseconds. An NSData can be handed to
 * AVAudioConverter without being taken apart first.
 */
public interface NativeAudioSink {
    public fun onFormat(sampleRate: Int, channels: Int)

    public fun onFrame(opus: NSData, ptsMs: Long)
}

/** Adapts a Swift-implemented [NativeAudioSink] to the sink the session expects. */
public class NativeAudioSinkAdapter(private val inner: NativeAudioSink) : AudioSink {
    override fun onFormat(sampleRate: Int, channels: Int): Unit = inner.onFormat(sampleRate, channels)

    override fun onFrame(opus: ByteArray, ptsMs: Long): Unit = inner.onFrame(opus.toNSData(), ptsMs)
}
