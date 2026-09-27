package com.sunllo.deskpair.framing

/**
 * Little-endian integer access. Every length, counter and header field in this protocol is little-endian,
 * so there is exactly one place to get the order wrong and it is here.
 */
internal fun ByteArray.writeUInt32Le(offset: Int, value: UInt) {
    this[offset] = (value and 0xFFu).toByte()
    this[offset + 1] = ((value shr 8) and 0xFFu).toByte()
    this[offset + 2] = ((value shr 16) and 0xFFu).toByte()
    this[offset + 3] = ((value shr 24) and 0xFFu).toByte()
}

internal fun ByteArray.readUInt32Le(offset: Int): UInt =
    (this[offset].toUInt() and 0xFFu) or
        ((this[offset + 1].toUInt() and 0xFFu) shl 8) or
        ((this[offset + 2].toUInt() and 0xFFu) shl 16) or
        ((this[offset + 3].toUInt() and 0xFFu) shl 24)

internal fun ByteArray.writeUInt64Le(offset: Int, value: ULong) {
    for (i in 0 until 8) {
        this[offset + i] = ((value shr (i * 8)) and 0xFFuL).toByte()
    }
}

internal fun ByteArray.toHex(): String {
    val digits = "0123456789abcdef"
    val out = StringBuilder(size * 2)
    for (b in this) {
        val v = b.toInt() and 0xFF
        out.append(digits[v shr 4]).append(digits[v and 0x0F])
    }
    return out.toString()
}

internal fun String.hexToBytes(): ByteArray {
    require(length % 2 == 0) { "A hex string must have an even length." }
    val out = ByteArray(length / 2)
    for (i in out.indices) {
        out[i] = ((hexDigit(this[i * 2]) shl 4) or hexDigit(this[i * 2 + 1])).toByte()
    }
    return out
}

private fun hexDigit(c: Char): Int = when (c) {
    in '0'..'9' -> c - '0'
    in 'a'..'f' -> c - 'a' + 10
    in 'A'..'F' -> c - 'A' + 10
    else -> throw IllegalArgumentException("Not a hex digit: $c")
}
