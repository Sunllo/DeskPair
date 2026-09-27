package com.sunllo.deskpair

import kotlinx.cinterop.ExperimentalForeignApi
import kotlinx.cinterop.addressOf
import kotlinx.cinterop.usePinned
import platform.Foundation.NSData
import platform.posix.memcpy
import kotlin.coroutines.cancellation.CancellationException

/**
 * Sending an image from Swift without paying for the bridge.
 *
 * A Kotlin `ByteArray` crosses into Objective-C element by element, which is invisible for a cursor bitmap
 * and painful for a three megabyte screenshot — the same reason [NativeVideoSink], [NativeAudioSink] and
 * [NativeCursorSink] exist. This takes the `NSData` Swift already holds and copies it once.
 *
 * An object rather than an extension function: an extension's exported name depends on where the compiler
 * decides to put it, and a Swift call site should not have to guess.
 */
public object NativeClipboard {

    /**
     * Offers a PNG to the host's clipboard. False means the host already has it, which is ordinary.
     *
     * Throws when the picture is larger than [com.sunllo.deskpair.clipboard.ClipboardSync.MAX_BYTES];
     * the caller is the only thing that knows how to make it smaller.
     */
    @OptIn(ExperimentalForeignApi::class)
    @Throws(CancellationException::class, Throwable::class)
    public suspend fun sendImage(session: RemoteSession, png: NSData): Boolean {
        val bytes = ByteArray(png.length.toInt())
        if (bytes.isNotEmpty()) {
            bytes.usePinned { pinned ->
                memcpy(pinned.addressOf(0), png.bytes, png.length)
            }
        }

        return session.sendClipboardImage(bytes)
    }
}
