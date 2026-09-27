package com.sunllo.deskpair

/**
 * The picture formats a host can send, named for the decoders that take them.
 *
 * The session's own enum rather than the wire's: the apps choose a decoder from it, and a switch in Swift or
 * Kotlin over a generated protocol type would tie both apps to the generator's naming.
 */
public enum class VideoFormat(internal val wire: Int) {
    VP8(0),
    VP9(1),
    H264(2),
    H265(3),
    AV1(4),
    ;

    internal companion object {
        /** The format a frame says it is in; null for a number this build does not know. */
        fun fromWire(code: Int): VideoFormat? = entries.firstOrNull { it.wire == code }
    }
}
