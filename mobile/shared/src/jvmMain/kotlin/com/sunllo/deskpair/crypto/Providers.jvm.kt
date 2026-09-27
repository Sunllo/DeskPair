package com.sunllo.deskpair.crypto

import dev.whyoleg.cryptography.CryptographyProvider
import dev.whyoleg.cryptography.providers.jdk.JDK

/** The JDK provider covers everything this protocol needs, on both the JVM and Android. */
internal actual fun cryptographyProviders(): List<CryptographyProvider> =
    listOf(CryptographyProvider.JDK)
