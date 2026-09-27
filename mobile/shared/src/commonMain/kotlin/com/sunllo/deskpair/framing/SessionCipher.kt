package com.sunllo.deskpair.framing

import com.sunllo.deskpair.crypto.Crypto

/**
 * One direction of AES-256-GCM, mirroring `DeskPair.Protocol.Framing.SessionCipher`.
 *
 * The nonce is a 4-byte prefix from the handshake followed by an 8-byte little-endian counter that starts
 * at 1. Nothing on the wire carries it: both ends count independently and the order they count in is the
 * order frames go out in, so every frame must be encrypted under the same lock that writes it. A heartbeat
 * is never encrypted and therefore never consumes a counter.
 *
 * Reusing a counter under one key breaks GCM completely, so the counter is checked rather than allowed to
 * wrap.
 */
internal class SessionCipher(private val key: ByteArray, ivPrefix: ByteArray) {

    companion object {
        const val TAG_BYTES = 16
        const val NONCE_BYTES = 12
    }

    init {
        require(ivPrefix.size == 4) { "The nonce prefix is 4 bytes, got ${ivPrefix.size}." }
        require(key.size == 32) { "AES-256 needs a 32-byte key, got ${key.size}." }
    }

    private val nonce = ByteArray(NONCE_BYTES).also { ivPrefix.copyInto(it) }
    private var counter: ULong = 0uL

    /** The counter most recently used. Exposed so the conformance vectors can assert it. */
    val lastCounter: ULong get() = counter

    fun seal(plaintext: ByteArray, associatedData: ByteArray): ByteArray {
        nextNonce()
        return Crypto.aesGcmSeal(key, nonce, plaintext, associatedData)
    }

    /** Returns null when the tag does not verify. The caller must close the session rather than carry on. */
    fun open(ciphertextAndTag: ByteArray, associatedData: ByteArray): ByteArray? {
        nextNonce()
        return Crypto.aesGcmOpen(key, nonce, ciphertextAndTag, associatedData)
    }

    private fun nextNonce() {
        check(counter != ULong.MAX_VALUE) { "Session cipher counter exhausted; the session must be rekeyed." }
        counter++
        nonce.writeUInt64Le(4, counter)
    }
}
