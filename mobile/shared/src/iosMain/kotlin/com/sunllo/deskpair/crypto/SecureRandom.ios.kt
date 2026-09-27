package com.sunllo.deskpair.crypto

import kotlinx.cinterop.ExperimentalForeignApi
import kotlinx.cinterop.addressOf
import kotlinx.cinterop.usePinned
import platform.Security.SecRandomCopyBytes
import platform.Security.errSecSuccess
import platform.Security.kSecRandomDefault

/**
 * The system CSPRNG. Failure is fatal rather than silently falling back to something weaker: a nonce that is
 * not random makes the AES-GCM counter repeat, and that breaks the cipher outright.
 */
@OptIn(ExperimentalForeignApi::class)
internal actual fun secureRandomBytes(count: Int): ByteArray {
    if (count == 0) {
        return ByteArray(0)
    }

    val bytes = ByteArray(count)
    val status = bytes.usePinned { pinned ->
        SecRandomCopyBytes(kSecRandomDefault, count.toULong(), pinned.addressOf(0))
    }

    check(status == errSecSuccess) { "SecRandomCopyBytes failed with status $status." }
    return bytes
}
