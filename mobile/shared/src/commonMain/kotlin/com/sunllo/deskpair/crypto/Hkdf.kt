package com.sunllo.deskpair.crypto

/**
 * HKDF-SHA256 (RFC 5869), written here rather than taken from a library.
 *
 * It is twenty lines over HMAC, it is checked byte-for-byte against vectors from the C# implementation, and
 * writing it removes a question about which HKDF surface the platform backend happens to expose. This is the
 * one exception to "no hand-rolled crypto": HKDF is a construction over HMAC, not a primitive, and the
 * primitive underneath it still comes from the platform.
 */
internal object Hkdf {

    private const val HASH_BYTES = 32

    fun extract(salt: ByteArray, inputKeyMaterial: ByteArray): ByteArray =
        Crypto.hmacSha256(salt, inputKeyMaterial)

    fun expand(pseudoRandomKey: ByteArray, info: ByteArray, length: Int): ByteArray {
        require(length > 0) { "HKDF length must be positive." }
        require(length <= 255 * HASH_BYTES) { "HKDF cannot expand to $length bytes." }

        val output = ByteArray(length)
        var previous = ByteArray(0)
        var written = 0
        var counter = 1

        while (written < length) {
            // T(n) = HMAC(prk, T(n-1) || info || n)
            val block = Crypto.hmacSha256(pseudoRandomKey, previous + info + byteArrayOf(counter.toByte()))
            val take = minOf(block.size, length - written)
            block.copyInto(output, written, 0, take)
            written += take
            previous = block
            counter++
        }

        return output
    }
}
