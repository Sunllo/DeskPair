package com.sunllo.deskpair.media

/**
 * The wire format of a media datagram, mirroring `DeskPair.Protocol.Media.MediaPacket`.
 *
 * Video moves over UDP because a frame that arrives late is worth less than one that never arrives at all:
 * TCP would hold the newest picture behind a retransmission of one nobody will see. What replaces TCP's
 * guarantees is here — a packet sequence that doubles as the encryption nonce and the replay key, forward
 * error correction so a lost shard is usually reconstructed rather than requested, and feedback the sender
 * uses to size its own bitrate.
 *
 * Every field is little-endian, and the byte offsets are the format, not an implementation detail: the
 * desktop writes these and this reads them.
 */
public object MediaPacket {

    public const val MAGIC: Byte = 0xA5.toByte()
    public const val COMMON_HEADER_BYTES: Int = 12
    public const val SHARD_HEADER_BYTES: Int = 32
    public const val TAG_BYTES: Int = 16
    public const val FEEDBACK_BYTES: Int = 48
}

/** What a datagram is for. The numbering is the wire's. */
public enum class MediaPacketType(public val code: Int) {
    VIDEO(0),
    AUDIO(1),
    FEEDBACK(2),
    PING(3),
    PONG(4),
    BIND(5),
    BIND_ACK(6),
    CLOSE(7),

    /** Per-packet arrival report, for the sender's bandwidth estimate. */
    TRANSPORT_FEEDBACK(8),

    /** Probe padding: carries nothing, and exists only to be timed. */
    PADDING(9),
    ;

    public companion object {
        public fun of(code: Int): MediaPacketType? = entries.firstOrNull { it.code == code }
    }
}

/** Flags on a shard. A bitmask rather than an enum, because two of them can be true at once. */
public object MediaPacketFlags {
    public const val NONE: Int = 0
    public const val KEY_FRAME: Int = 1
    public const val PARITY: Int = 2
    public const val LAST_SHARD: Int = 4
}

/**
 * The 12 plaintext bytes in front of every datagram.
 *
 * Plaintext on purpose: the sequence has to be readable before the packet can be decrypted, because it is
 * the nonce. That is also why it is authenticated — it travels as the associated data, so a sequence
 * nobody can alter is bound into the tag.
 */
public data class MediaCommonHeader(
    val type: MediaPacketType,
    val flags: Int,
    val packetSeq: ULong,
) {
    public fun write(destination: ByteArray, offset: Int = 0) {
        destination[offset] = MediaPacket.MAGIC
        destination[offset + 1] = type.code.toByte()
        destination.putShortLe(offset + 2, flags)
        destination.putLongLe(offset + 4, packetSeq.toLong())
    }

    public companion object {
        /** Null for anything that is not one of these, which on an open UDP port is most of what arrives. */
        public fun read(source: ByteArray, offset: Int = 0): MediaCommonHeader? {
            if (source.size - offset < MediaPacket.COMMON_HEADER_BYTES || source[offset] != MediaPacket.MAGIC) {
                return null
            }

            val type = MediaPacketType.of(source[offset + 1].toInt() and 0xFF) ?: return null
            return MediaCommonHeader(
                type = type,
                flags = source.shortLe(offset + 2),
                packetSeq = source.longLe(offset + 4).toULong(),
            )
        }
    }
}

/**
 * The 32 encrypted bytes describing one shard of one frame.
 *
 * A frame is split into [k] data shards and [m] parity shards; any [k] of the [k] + [m] reconstruct it.
 * Large frames are split further into blocks, each with its own parity, so a keyframe does not need one
 * enormous FEC block to be recoverable.
 */
public data class MediaShardHeader(
    val stream: Int,
    val codec: Int,
    val blockIndex: Int,
    val blockCount: Int,
    val frameSeq: UInt,
    val ptsMs: Long,
    val shardIndex: Int,
    val k: Int,
    val m: Int,
    val shardLength: Int,
    val frameLength: UInt,
    val width: Int,
    val height: Int,
) {
    public val isParity: Boolean get() = shardIndex >= k

    public fun write(destination: ByteArray, offset: Int = 0) {
        destination[offset] = stream.toByte()
        destination[offset + 1] = codec.toByte()
        destination[offset + 2] = blockIndex.toByte()
        destination[offset + 3] = blockCount.toByte()
        destination.putIntLe(offset + 4, frameSeq.toInt())
        destination.putLongLe(offset + 8, ptsMs)
        destination.putShortLe(offset + 16, shardIndex)
        destination[offset + 18] = k.toByte()
        destination[offset + 19] = m.toByte()
        destination.putShortLe(offset + 20, shardLength)
        destination.putIntLe(offset + 22, frameLength.toInt())
        destination.putShortLe(offset + 26, width)
        destination.putShortLe(offset + 28, height)
        destination.putShortLe(offset + 30, 0)
    }

    public companion object {
        public const val MAX_SHARD_BYTES: Int = 1200 - MediaPacket.COMMON_HEADER_BYTES -
            MediaPacket.SHARD_HEADER_BYTES - MediaPacket.TAG_BYTES

        /**
         * Null when the header is impossible rather than merely unexpected. A shard index outside its own
         * block, or a length past the datagram limit, cannot be honoured and must not be allowed to size
         * an allocation.
         */
        public fun read(source: ByteArray, offset: Int = 0): MediaShardHeader? {
            if (source.size - offset < MediaPacket.SHARD_HEADER_BYTES) {
                return null
            }

            val header = MediaShardHeader(
                stream = source[offset].toInt() and 0xFF,
                codec = source[offset + 1].toInt() and 0xFF,
                blockIndex = source[offset + 2].toInt() and 0xFF,
                blockCount = source[offset + 3].toInt() and 0xFF,
                frameSeq = source.intLe(offset + 4).toUInt(),
                ptsMs = source.longLe(offset + 8),
                shardIndex = source.shortLe(offset + 16),
                k = source[offset + 18].toInt() and 0xFF,
                m = source[offset + 19].toInt() and 0xFF,
                shardLength = source.shortLe(offset + 20),
                frameLength = source.intLe(offset + 22).toUInt(),
                width = source.shortLe(offset + 26),
                height = source.shortLe(offset + 28),
            )

            val sane = header.k > 0 &&
                header.blockCount > 0 &&
                header.blockIndex < header.blockCount &&
                header.shardIndex < header.k + header.m &&
                header.shardLength <= MAX_SHARD_BYTES
            return if (sane) header else null
        }
    }
}

/**
 * What the receiver tells the sender, 48 bytes, in place of the per-frame ack TCP would have carried.
 *
 * This is the controller's whole contribution to congestion control. The estimator that turns these into a
 * bitrate runs on the sending side; the phone only has to report honestly and often.
 */
public data class MediaFeedback(
    val stream: Int,
    val lossPermille: Int,
    val highestDecodableFrameSeq: UInt,
    val lastFrameSeqReceived: UInt,
    val echoPacketSeq: ULong,
    val echoDelayMicros: UInt,
    val receivedBytes: ULong,
    val receiverClockMicros: ULong,
    val framesGivenUp: UInt,
    val shardsRecovered: UInt,
    /** [LINK_FIELDS_IGNORED] on every report of a round but the first; see the desktop's `MediaFeedbackFlags`. */
    val flags: Int = NO_FLAGS,
) {
    public fun write(destination: ByteArray, offset: Int = 0) {
        destination[offset] = stream.toByte()
        destination[offset + 1] = flags.toByte()
        destination.putShortLe(offset + 2, lossPermille)
        destination.putIntLe(offset + 4, highestDecodableFrameSeq.toInt())
        destination.putIntLe(offset + 8, lastFrameSeqReceived.toInt())
        destination.putLongLe(offset + 12, echoPacketSeq.toLong())
        destination.putIntLe(offset + 20, echoDelayMicros.toInt())
        destination.putLongLe(offset + 24, receivedBytes.toLong())
        destination.putLongLe(offset + 32, receiverClockMicros.toLong())
        destination.putIntLe(offset + 40, framesGivenUp.toInt())
        destination.putIntLe(offset + 44, shardsRecovered.toInt())
    }

    public companion object {
        public const val NO_FLAGS: Int = 0

        /**
         * The link-level fields repeat another report of the same round. Deliberately the inverted sense: a
         * receiver that predates it wrote 0 here, which reads as "these are yours".
         */
        public const val LINK_FIELDS_IGNORED: Int = 1

        public fun read(source: ByteArray, offset: Int = 0): MediaFeedback? {
            if (source.size - offset < MediaPacket.FEEDBACK_BYTES) {
                return null
            }

            return MediaFeedback(
                stream = source[offset].toInt() and 0xFF,
                lossPermille = source.shortLe(offset + 2),
                highestDecodableFrameSeq = source.intLe(offset + 4).toUInt(),
                lastFrameSeqReceived = source.intLe(offset + 8).toUInt(),
                echoPacketSeq = source.longLe(offset + 12).toULong(),
                echoDelayMicros = source.intLe(offset + 20).toUInt(),
                receivedBytes = source.longLe(offset + 24).toULong(),
                receiverClockMicros = source.longLe(offset + 32).toULong(),
                framesGivenUp = source.intLe(offset + 40).toUInt(),
                shardsRecovered = source.intLe(offset + 44).toUInt(),
                flags = source[offset + 1].toInt() and 0xFF,
            )
        }
    }
}

// Little-endian readers and writers. Kotlin has none in the standard library and the format is fixed, so
// they live here rather than being rewritten at every call site.

internal fun ByteArray.putShortLe(offset: Int, value: Int) {
    this[offset] = (value and 0xFF).toByte()
    this[offset + 1] = ((value shr 8) and 0xFF).toByte()
}

internal fun ByteArray.putIntLe(offset: Int, value: Int) {
    for (i in 0 until 4) {
        this[offset + i] = ((value shr (8 * i)) and 0xFF).toByte()
    }
}

internal fun ByteArray.putLongLe(offset: Int, value: Long) {
    for (i in 0 until 8) {
        this[offset + i] = ((value shr (8 * i)) and 0xFF).toByte()
    }
}

internal fun ByteArray.shortLe(offset: Int): Int =
    (this[offset].toInt() and 0xFF) or ((this[offset + 1].toInt() and 0xFF) shl 8)

internal fun ByteArray.intLe(offset: Int): Int {
    var value = 0
    for (i in 0 until 4) {
        value = value or ((this[offset + i].toInt() and 0xFF) shl (8 * i))
    }
    return value
}

internal fun ByteArray.longLe(offset: Int): Long {
    var value = 0L
    for (i in 0 until 8) {
        value = value or ((this[offset + i].toLong() and 0xFF) shl (8 * i))
    }
    return value
}
