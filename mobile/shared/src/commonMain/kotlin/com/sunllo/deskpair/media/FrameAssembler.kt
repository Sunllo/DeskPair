package com.sunllo.deskpair.media

import kotlinx.coroutines.sync.withLock

/** A frame rebuilt from its datagrams. */
public class AssembledFrame(
    public val data: ByteArray,
    public val stream: Int,
    public val codec: Int,
    public val frameSeq: UInt,
    public val ptsMs: Long,
    public val width: Int,
    public val height: Int,
    public val keyFrame: Boolean,
)

/**
 * Puts frames back together from the datagrams that carried them.
 *
 * Three jobs, and the third is the one that makes UDP usable. It collects shards per frame and per FEC
 * block; it repairs a block with Reed-Solomon the moment k shards have arrived, so an ordinary loss costs
 * nothing at all; and it decides when a frame is not coming. That last decision is what separates a
 * picture that stutters from one that stops: a frame held forever blocks everything behind it, and a frame
 * abandoned too early throws away one that was merely large.
 *
 * So the give-up rules are written in terms of what is known rather than a single timeout. A completed
 * keyframe further along makes everything before it irrelevant. A frame that is still receiving shards is
 * judged by the time since its last one, not its first, because a keyframe paced across many milliseconds
 * is arriving, not late. And after a frame is given up, every delta that follows is dropped until a
 * keyframe arrives — a delta applied to a reference that was never drawn produces a picture made of
 * smeared wreckage, which looks far worse than a brief freeze.
 *
 * One stream: each of the host's displays numbers its frames from one, so everything here belongs to one
 * display and starts again with the next. Which streams exist is [StreamAssemblers]' business; what the link
 * as a whole delivered is [LinkStats]'.
 *
 * @param stream the display whose frames this assembles; shards of any other are ignored.
 * @param startsBroken true when there is nothing to decode against until this stream's first keyframe (a
 *   display switched to mid-session). The first stream of a session starts clean, so a lossy start does not
 *   ask for an extra keyframe.
 */
public class FrameAssembler(
    private val nowMillis: () -> Long,
    public val stream: Int = 0,
    startsBroken: Boolean = false,
) {

    /**
     * Everything below is touched from two coroutines: the loop that receives datagrams and the one that
     * reports back every fifty milliseconds. Both mutate the pending frames, and without this they raced —
     * one emptied the map between the other's "is it empty?" and its "give me the first", which threw and,
     * on Android, took the whole process with it.
     */
    private val guard = kotlinx.coroutines.sync.Mutex()

    private companion object {
        const val MAX_PENDING = 8
        const val MAX_AGE_MS = 1000L
        const val STALL_AFTER_MS = 60L
    }

    private val pending = HashMap<UInt, PendingFrame>()
    private val ready = ArrayDeque<AssembledFrame>()

    private var nextExpected: UInt = 0u
    private var haveDelivered = false
    private var intervalMs = 1000.0 / 60
    private var lastPtsMs = 0L

    public var framesDelivered: Long = 0
        private set

    public var framesGivenUp: UInt = 0u
        private set

    public var shardsRecovered: UInt = 0u
        private set

    public var highestDecodable: UInt = 0u
        private set

    public var lastFrameSeqReceived: UInt = 0u
        private set

    /** True after a frame was given up, until a keyframe arrives. The channel asks for a refresh while set. */
    public var referenceBroken: Boolean = startsBroken
        private set

    /** Feeds one decrypted shard: its header followed by its bytes. */
    public suspend fun accept(common: MediaCommonHeader, plaintext: ByteArray) {
        val header = MediaShardHeader.read(plaintext) ?: return
        if (plaintext.size < MediaPacket.SHARD_HEADER_BYTES + header.shardLength) {
            return
        }

        accept(common, header, plaintext)
    }

    /** Feeds a shard whose header the caller has already read and checked. */
    internal suspend fun accept(common: MediaCommonHeader, header: MediaShardHeader, plaintext: ByteArray): Unit = guard.withLock {
        if (header.stream != stream) {
            return
        }

        if (header.frameSeq > lastFrameSeqReceived) {
            lastFrameSeqReceived = header.frameSeq
        }

        // Already delivered, or already given up. Either way nobody is waiting for it.
        if (haveDelivered && header.frameSeq < nextExpected) {
            return
        }

        var frame = pending[header.frameSeq]
        if (frame == null) {
            if (pending.size >= MAX_PENDING) {
                giveUpOldest()
            }

            frame = PendingFrame(
                header = header,
                keyFrame = common.flags and MediaPacketFlags.KEY_FRAME != 0,
                firstSeenMs = nowMillis(),
            )
            pending[header.frameSeq] = frame

            // A running estimate of the frame interval, used to decide how long reordering deserves.
            if (lastPtsMs != 0L && header.ptsMs > lastPtsMs && header.ptsMs - lastPtsMs < 200) {
                intervalMs = 0.9 * intervalMs + 0.1 * (header.ptsMs - lastPtsMs)
            }
            if (header.ptsMs > lastPtsMs) {
                lastPtsMs = header.ptsMs
            }
        }

        frame.lastShardSeenMs = nowMillis()
        val shard = plaintext.copyOfRange(
            MediaPacket.SHARD_HEADER_BYTES,
            MediaPacket.SHARD_HEADER_BYTES + header.shardLength,
        )
        shardsRecovered += frame.addShard(header, shard)

        drain()
    }

    /** Applies the give-up rules even when nothing is arriving, which is exactly when they matter. */
    public suspend fun tick(): Unit = guard.withLock { drain() }

    public suspend fun poll(): AssembledFrame? = guard.withLock { ready.removeFirstOrNull() }

    private fun drain() {
        while (pending.isNotEmpty()) {
            val headSeq = pending.keys.min()
            val frame = pending[headSeq]!!

            if (!haveDelivered) {
                nextExpected = headSeq
                haveDelivered = true
            }

            if (frame.isComplete) {
                deliver(headSeq, frame)
                continue
            }

            // Anything before a finished keyframe is no longer worth waiting for.
            if (pending.any { it.key > headSeq && it.value.isComplete && it.value.keyFrame }) {
                giveUp(headSeq, frame)
                continue
            }

            val now = nowMillis()
            val age = now - frame.firstSeenMs
            val stalled = now - frame.lastShardSeenMs
            val newerComplete = pending.any { it.key > headSeq && it.value.isComplete }
            val reorderWait = maxOf((2 * intervalMs).toLong(), STALL_AFTER_MS)

            if (age > MAX_AGE_MS || (newerComplete && stalled > reorderWait)) {
                giveUp(headSeq, frame)
                continue
            }

            // Still arriving. Everything behind it waits, because frames are drawn in order.
            break
        }
    }

    private fun deliver(seq: UInt, frame: PendingFrame) {
        pending.remove(seq)
        nextExpected = seq + 1u
        if (seq > highestDecodable) {
            highestDecodable = seq
        }

        if (frame.keyFrame) {
            referenceBroken = false
        }

        if (referenceBroken) {
            // A delta whose reference was never drawn would smear the picture rather than update it.
            return
        }

        ready.addLast(frame.finish())
        framesDelivered++
    }

    private fun giveUp(seq: UInt, frame: PendingFrame) {
        pending.remove(seq)
        nextExpected = seq + 1u
        if (seq > highestDecodable) {
            highestDecodable = seq
        }
        framesGivenUp += 1u
        referenceBroken = true
    }

    private fun giveUpOldest() {
        val seq = pending.keys.min()
        giveUp(seq, pending[seq]!!)
    }

    private class PendingFrame(
        private val header: MediaShardHeader,
        val keyFrame: Boolean,
        val firstSeenMs: Long,
    ) {
        private val blocks = arrayOfNulls<Block>(header.blockCount)
        private val frameLength = header.frameLength.toInt()
        private val shardLength = header.shardLength

        var lastShardSeenMs: Long = firstSeenMs

        val isComplete: Boolean get() = blocks.all { it != null && it.decodable }

        /** Returns how many shards the repair rebuilt, which is what the sender is told about. */
        fun addShard(shardHeader: MediaShardHeader, shard: ByteArray): UInt {
            val index = shardHeader.blockIndex
            val block = blocks[index] ?: Block(shardHeader.k, shardHeader.m, shardLength).also { blocks[index] = it }

            // A block whose shape changed mid-frame is a frame nobody can trust.
            if (block.k != shardHeader.k || block.m != shardHeader.m) {
                return 0u
            }

            block.add(shardHeader.shardIndex, shard)
            if (block.decodable && !block.repaired) {
                return block.repair().toUInt()
            }
            return 0u
        }

        fun finish(): AssembledFrame {
            val buffer = ByteArray(maxOf(frameLength, 1))
            var offset = 0
            for (block in blocks) {
                var i = 0
                while (i < block!!.k && offset < frameLength) {
                    val take = minOf(shardLength, frameLength - offset)
                    block.shards[i]!!.copyInto(buffer, offset, 0, take)
                    offset += take
                    i++
                }
            }

            return AssembledFrame(
                data = if (buffer.size == frameLength) buffer else buffer.copyOf(frameLength),
                stream = header.stream,
                codec = header.codec,
                frameSeq = header.frameSeq,
                ptsMs = header.ptsMs,
                width = header.width,
                height = header.height,
                keyFrame = keyFrame,
            )
        }
    }

    private class Block(val k: Int, val m: Int, private val shardLength: Int) {
        val shards = arrayOfNulls<ByteArray>(k + m)
        private var present = 0
        var repaired = false
            private set

        val decodable: Boolean get() = present >= k

        fun add(index: Int, shard: ByteArray) {
            if (index >= shards.size || shards[index] != null) {
                return
            }

            // Copied to exactly the shard length: Reed-Solomon works across shards column by column, so
            // one that is shorter than the rest would read past its own end.
            val buffer = ByteArray(shardLength)
            shard.copyInto(buffer, 0, 0, minOf(shard.size, shardLength))
            shards[index] = buffer
            present++
        }

        /** Rebuilds the missing data shards; returns how many were rebuilt. */
        fun repair(): Int {
            repaired = true
            val missing = (0 until k).count { shards[it] == null }
            if (missing == 0) {
                return 0
            }

            val flags = BooleanArray(k + m) { shards[it] != null }
            val buffers = Array(k + m) { shards[it] ?: ByteArray(shardLength) }

            val ok = ReedSolomon.decode(buffers, flags, k, shardLength)
            if (!ok) {
                return 0
            }

            for (i in 0 until k) {
                if (shards[i] == null) {
                    shards[i] = buffers[i]
                    present++
                }
            }
            return missing
        }
    }
}
