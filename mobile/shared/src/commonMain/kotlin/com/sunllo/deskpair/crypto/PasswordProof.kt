package com.sunllo.deskpair.crypto

import sunllo.messages.PasswordKdf

/**
 * Mirrors `DeskPair.Protocol.Crypto.PasswordProof`. The host stores h1 and never the password; the
 * controller proves knowledge of h1 against a challenge that is fresh per connection, so a captured proof
 * is worth nothing next time.
 *
 * How h1 is derived is the host's choice, announced in the challenge: protocol 1 knew only one SHA-256
 * over password ‖ salt, which a stolen h1 turns into a password guessable at hardware speed; protocol 2
 * hosts derive with PBKDF2-HMAC-SHA256 and still name the old way for a permanent password that predates
 * it. Either way the proof is SHA256(h1 ‖ challenge).
 *
 * When the host is in click-to-approve mode the proof is empty and the person at the host decides instead.
 */
internal object PasswordProof {

    /** The most rounds this side will do on a host's say-so: a challenge arrives before the host is trusted. */
    const val MAX_ITERATIONS = 1_000_000

    const val HASH_BYTES = 32

    fun computeH1(password: String, salt: ByteArray, kdf: PasswordKdf = PasswordKdf.KDF_SHA256, iterations: Int = 0): ByteArray =
        when (kdf) {
            PasswordKdf.KDF_SHA256 -> Crypto.sha256(password.encodeToByteArray() + salt)
            PasswordKdf.KDF_PBKDF2_SHA256 -> {
                require(iterations in 1..MAX_ITERATIONS) { "PBKDF2 iterations out of range: $iterations" }
                Crypto.pbkdf2Sha256(password.encodeToByteArray(), salt, iterations, HASH_BYTES)
            }
        }

    fun computeProof(h1: ByteArray, challenge: ByteArray): ByteArray =
        Crypto.sha256(h1 + challenge)

    fun computeProof(
        password: String,
        salt: ByteArray,
        challenge: ByteArray,
        kdf: PasswordKdf = PasswordKdf.KDF_SHA256,
        iterations: Int = 0,
    ): ByteArray = computeProof(computeH1(password, salt, kdf, iterations), challenge)
}
