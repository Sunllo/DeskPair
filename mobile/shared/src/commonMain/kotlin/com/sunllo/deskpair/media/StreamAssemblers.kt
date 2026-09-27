package com.sunllo.deskpair.media

import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

/**
 * The receiver's streams: one [FrameAssembler] per display being watched, and the rules for which datagrams
 * belong to one.
 *
 * Until the session says which displays it is on, one stream is live at a time. A keyframe of another display
 * replaces it -- the host switched before the confirmation reached us -- and a delta of another display is
 * dropped, having nothing to decode against. Once the session says ([expect]), exactly those streams are live
 * and anything else is a straggler from a display it has left.
 *
 * The phones watch one display, so for them this is the rules for a set of one. It exists here anyway because
 * the desktop's `StreamAssemblers` is the same thing, and two copies of this logic that drift apart are how the
 * last display-switch bug had to be found twice.
 */
public class StreamAssemblers(private val nowMillis: () -> Long) {

    private val guard = Mutex()
    private val streams = LinkedHashMap<Int, FrameAssembler>()
    private var expected: Set<Int>? = null
    private var hadStream = false

    /** Feeds one decrypted shard, its header followed by its bytes, to the stream it belongs to. */
    public suspend fun accept(common: MediaCommonHeader, plaintext: ByteArray): Unit = guard.withLock {
        val header = MediaShardHeader.read(plaintext) ?: return
        if (plaintext.size < MediaPacket.SHARD_HEADER_BYTES + header.shardLength) {
            return
        }

        var assembler = streams[header.stream]
        if (assembler == null) {
            if (expected != null) {
                return // a straggler from a display the session has left
            }

            if (streams.isNotEmpty() && common.flags and MediaPacketFlags.KEY_FRAME == 0) {
                return // a delta of some other display has nothing to decode against
            }

            // A keyframe of another display before the session has said which it is on: the host switched,
            // and the new display replaces the old one. Only a confirmed set keeps two.
            streams.clear()
            assembler = add(header.stream)
        }

        assembler.accept(common, header, plaintext)
    }

    /** The one display the session is now watching. */
    public suspend fun expect(stream: Int): Unit = expect(setOf(stream))

    /** The displays the session is now watching: frames of any other stream are dropped from here on. */
    public suspend fun expect(wanted: Set<Int>): Unit = guard.withLock {
        expected = wanted
        streams.keys.retainAll(wanted)
        // A stream confirmed before any of its frames arrived keeps whatever arrives next; one that is
        // already running (its keyframe beat the confirmation) keeps what it has.
        for (stream in wanted) {
            if (stream !in streams) {
                add(stream)
            }
        }
    }

    /** Applies the give-up rules on every stream, even when nothing is arriving. */
    public suspend fun tick(): Unit = guard.withLock {
        for (assembler in streams.values) {
            assembler.tick()
        }
    }

    public suspend fun poll(): AssembledFrame? = guard.withLock {
        for (assembler in streams.values) {
            assembler.poll()?.let { return it }
        }
        null
    }

    /** True while any live stream has lost a frame and waits for a keyframe. */
    public suspend fun referenceBroken(): Boolean = guard.withLock { streams.values.any { it.referenceBroken } }

    /** The live streams, for the per-stream reports. */
    public suspend fun live(): List<FrameAssembler> = guard.withLock { streams.values.toList() }

    private fun add(stream: Int): FrameAssembler {
        val assembler = FrameAssembler(nowMillis, stream, startsBroken = hadStream)
        hadStream = true
        streams[stream] = assembler
        return assembler
    }
}
