package com.sunllo.deskpair.framing

import com.sunllo.deskpair.crypto.SessionKeys
import com.sunllo.deskpair.transport.ByteStream
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

/**
 * The length-prefixed message stream both peers speak, mirroring
 * `DeskPair.Protocol.Framing.FramedStream`.
 *
 * The header is four bytes little-endian: bit 31 marks an encrypted frame, bit 30 is reserved, and the low
 * thirty bits are the body length. A zero-length frame is a heartbeat and is never encrypted. An encrypted
 * body is AES-GCM ciphertext followed by its 16-byte tag, and **the header itself is the associated data**,
 * so a length nobody can tamper with is bound into every frame.
 *
 * Two rules here are load-bearing rather than defensive.
 *
 * Frames are encrypted while holding the send lock, not before taking it. The nonce counter is never sent;
 * both ends simply count, so the order frames are encrypted in has to be the order they reach the wire in.
 * Encrypting outside the lock would let two coroutines swap places and desynchronise the counters — which
 * shows up as a tag failure on a frame that was never tampered with.
 *
 * And the encrypted bit must match the receiver's state exactly, in both directions. Accepting a plaintext
 * frame after the handshake would let anyone inject one.
 */
internal class FramedStream(
    private val socket: ByteStream,
    private val maxFrameBytes: Int,
) {

    companion object {
        const val HEADER_BYTES = 4
        private const val ENCRYPTED_FLAG = 0x8000_0000u
        private const val LENGTH_MASK = 0x3FFF_FFFFu

        /** A peer session carries video frames and so allows a large frame. */
        const val MAX_PEER_FRAME_BYTES = 32 * 1024 * 1024

        /** The rendezvous and relay streams carry only small control messages. */
        const val MAX_CONTROL_FRAME_BYTES = 16 * 1024
    }

    private val sendLock = Mutex()
    private var tx: SessionCipher? = null
    private var rx: SessionCipher? = null

    val isEncrypted: Boolean get() = tx != null

    /**
     * Switches both directions to AES-GCM, exactly once, between the hello messages and the first encrypted
     * frame. Everything after this point is encrypted in both directions with no further negotiation.
     */
    fun enableEncryption(keys: SessionKeys) {
        check(tx == null) { "Encryption is already enabled on this stream." }
        tx = SessionCipher(keys.txKey, keys.txIvPrefix)
        rx = SessionCipher(keys.rxKey, keys.rxIvPrefix)
    }

    suspend fun send(payload: ByteArray) {
        require(payload.isNotEmpty()) { "An empty payload is a heartbeat; use sendHeartbeat()." }
        require(payload.size <= maxFrameBytes) {
            "A frame of ${payload.size} bytes exceeds the $maxFrameBytes byte limit."
        }

        sendLock.withLock {
            val cipher = tx
            val bodyLength = if (cipher == null) payload.size else payload.size + SessionCipher.TAG_BYTES
            val header = ByteArray(HEADER_BYTES)
            header.writeUInt32Le(
                0,
                bodyLength.toUInt() or (if (cipher == null) 0u else ENCRYPTED_FLAG),
            )

            // Inside the lock: see the note above about counter order.
            val body = cipher?.seal(payload, header) ?: payload

            socket.write(header, 0, header.size)
            socket.write(body, 0, body.size)
            socket.flush()
        }
    }

    /** Four zero bytes. It proves the connection is alive and consumes no cipher counter. */
    suspend fun sendHeartbeat() {
        sendLock.withLock {
            socket.write(ByteArray(HEADER_BYTES), 0, HEADER_BYTES)
            socket.flush()
        }
    }

    /**
     * The next real frame, or null at end of stream. Heartbeats are consumed here and never surface: a
     * caller counting messages should not have to know they exist.
     */
    suspend fun receive(): ByteArray? {
        while (true) {
            val header = ByteArray(HEADER_BYTES)
            if (!readFullyOrEof(header)) {
                return null
            }

            val raw = header.readUInt32Le(0)
            val bodyLength = (raw and LENGTH_MASK).toInt()
            val encrypted = (raw and ENCRYPTED_FLAG) != 0u

            if (bodyLength == 0) {
                if (encrypted) {
                    throw ProtocolException("A heartbeat frame cannot be encrypted.")
                }
                continue
            }

            val cipher = rx
            if (encrypted != (cipher != null)) {
                throw ProtocolException(
                    if (encrypted) "An encrypted frame arrived before the handshake finished."
                    else "A plaintext frame arrived after the handshake finished.",
                )
            }

            val maxBody = if (cipher == null) maxFrameBytes else maxFrameBytes + SessionCipher.TAG_BYTES
            if (bodyLength > maxBody) {
                throw ProtocolException("A frame of $bodyLength bytes exceeds the $maxFrameBytes byte limit.")
            }

            if (encrypted && bodyLength < SessionCipher.TAG_BYTES + 1) {
                throw ProtocolException("An encrypted frame of $bodyLength bytes is too short to hold a tag.")
            }

            val body = ByteArray(bodyLength)
            if (!readFullyOrEof(body)) {
                return null
            }

            if (cipher == null) {
                return body
            }

            return cipher.open(body, header)
                ?: throw ProtocolException("A frame failed its authentication tag; the stream is compromised.")
        }
    }

    /** False at a clean end of stream, which is not an error; a partial frame is. */
    private suspend fun readFullyOrEof(destination: ByteArray): Boolean {
        var read = 0
        while (read < destination.size) {
            val n = socket.read(destination, read, destination.size - read)
            if (n <= 0) {
                if (read == 0) {
                    return false
                }
                throw ProtocolException("The stream ended part-way through a frame.")
            }
            read += n
        }
        return true
    }
}

/** The peer broke the framing or the crypto. Always fatal to the session. */
internal class ProtocolException(message: String) : Exception(message)
