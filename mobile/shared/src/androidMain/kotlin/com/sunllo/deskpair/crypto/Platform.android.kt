package com.sunllo.deskpair.crypto

import dev.whyoleg.cryptography.CryptographyProvider
import dev.whyoleg.cryptography.providers.jdk.JDK
import java.security.SecureRandom

/** Android is a JDK, so the same provider the desktop JVM uses covers everything the protocol needs. */
internal actual fun cryptographyProviders(): List<CryptographyProvider> =
    listOf(CryptographyProvider.JDK)

private val random = SecureRandom()

internal actual fun secureRandomBytes(count: Int): ByteArray =
    ByteArray(count).also(random::nextBytes)
