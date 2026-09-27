package com.sunllo.deskpair.media

import com.sunllo.deskpair.crypto.Crypto

/**
 * AES-256-GCM for datagrams, and the replay window that keeps UDP honest.
 *
 * Two differences from the TCP cipher, both forced by the medium. The nonce counter travels in the clear
 * inside every packet, because datagrams arrive out of order and a receiver that counted its own would
 * decrypt the wrong one; and the plaintext header is the associated data, so the sequence that chose the
 * nonce cannot be altered without breaking the tag.
 *
 * What a counter both sides can see costs is replay protection, which TCP gave away free. A 1024-packet
 * sliding window is the answer: anything newer than everything seen is fresh, anything older than the
 * window is refused outright, and in between a bitmap remembers what has already been opened. At sixty
 * frames a second over several shards each, a thousand packets is a few seconds of reordering tolerance —
 * far more than any real network needs and short enough that the window cannot be walked.
 */
public class MediaCipher(
    private val txKey: ByteArray,
    txIvPrefix: ByteArray,
    private val rxKey: ByteArray,
    rxIvPrefix: ByteArray,
) {

    private companion object {
        const val WINDOW_BITS = 1024
        const val NONCE_BYTES = 12
    }

    private val txNonce = ByteArray(NONCE_BYTES).also { txIvPrefix.copyInto(it, 0, 0, 4) }
    private val rxNonce = ByteArray(NONCE_BYTES).also { rxIvPrefix.copyInto(it, 0, 0, 4) }
    private val window = LongArray(WINDOW_BITS / 64)

    private var txSeq: ULong = 0uL
    private var rxHighest: ULong = 0uL

    public val nextPacketSeq: ULong get() = txSeq + 1uL

    /** Builds a complete datagram: plaintext header, ciphertext, tag. */
    public fun seal(type: MediaPacketType, flags: Int, plaintext: ByteArray): ByteArray {
        txSeq += 1uL
        val header = MediaCommonHeader(type, flags, txSeq)
        val datagram = ByteArray(MediaPacket.COMMON_HEADER_BYTES + plaintext.size + MediaPacket.TAG_BYTES)
        header.write(datagram)

        txNonce.putLongLe(4, txSeq.toLong())
        val associated = datagram.copyOfRange(0, MediaPacket.COMMON_HEADER_BYTES)
        val sealed = Crypto.aesGcmSeal(txKey, txNonce, plaintext, associated)
        sealed.copyInto(datagram, MediaPacket.COMMON_HEADER_BYTES)
        return datagram
    }

    /**
     * Authenticates, decrypts and admits a datagram. Null for anything that is not ours, has been
     * tampered with, or has been seen before — all of which are ordinary on an open UDP port and none of
     * which deserve an exception.
     */
    public fun open(datagram: ByteArray, length: Int = datagram.size): OpenedPacket? {
        val header = MediaCommonHeader.read(datagram) ?: return null
        if (length < MediaPacket.COMMON_HEADER_BYTES + MediaPacket.TAG_BYTES) {
            return null
        }
        if (!isFresh(header.packetSeq)) {
            return null
        }

        rxNonce.putLongLe(4, header.packetSeq.toLong())
        val associated = datagram.copyOfRange(0, MediaPacket.COMMON_HEADER_BYTES)
        val body = datagram.copyOfRange(MediaPacket.COMMON_HEADER_BYTES, length)
        val plaintext = Crypto.aesGcmOpen(rxKey, rxNonce, body, associated) ?: return null

        // Marked only after it verified. Marking on arrival would let anyone close the window by
        // spraying sequence numbers they cannot authenticate.
        mark(header.packetSeq)
        return OpenedPacket(header, plaintext)
    }

    private fun isFresh(seq: ULong): Boolean {
        if (seq == 0uL) {
            return false
        }
        if (seq > rxHighest) {
            return true
        }

        val age = rxHighest - seq
        if (age >= WINDOW_BITS.toULong()) {
            return false
        }

        val index = (seq % WINDOW_BITS.toULong()).toInt()
        return (window[index / 64] and (1L shl (seq % 64uL).toInt())) == 0L
    }

    private fun mark(seq: ULong) {
        if (seq > rxHighest) {
            // Clear what the window slides over, or a sequence a thousand packets ago would look seen.
            val advance = minOf(seq - rxHighest, WINDOW_BITS.toULong())
            var s = rxHighest + 1uL
            while (s <= rxHighest + advance) {
                val index = (s % WINDOW_BITS.toULong()).toInt()
                window[index / 64] = window[index / 64] and (1L shl (s % 64uL).toInt()).inv()
                s += 1uL
            }
            rxHighest = seq
        }

        val index = (seq % WINDOW_BITS.toULong()).toInt()
        window[index / 64] = window[index / 64] or (1L shl (seq % 64uL).toInt())
    }
}

/** A datagram that verified, with its plaintext header and body. */
public class OpenedPacket(public val header: MediaCommonHeader, public val plaintext: ByteArray)
