package com.sunllo.deskpair.crypto

import dev.whyoleg.cryptography.CryptographyProvider
import dev.whyoleg.cryptography.providers.apple.Apple
import dev.whyoleg.cryptography.providers.cryptokit.CryptoKit

/**
 * CryptoKit first, Security.framework second.
 *
 * Measured, not assumed: with only the Apple provider, SHA-256, HMAC and ECDSA work while ECDH and AES-GCM
 * fail with "Algorithm not found" — which is half the handshake and all of the session cipher. CryptoKit has
 * both, and reads P-256 keys in the SPKI DER form this protocol puts on the wire.
 */
internal actual fun cryptographyProviders(): List<CryptographyProvider> =
    listOf(CryptographyProvider.CryptoKit, CryptographyProvider.Apple)
