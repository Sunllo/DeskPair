package com.sunllo.deskpair.account

import com.sunllo.deskpair.crypto.Crypto
import com.sunllo.deskpair.store.SecretKeys
import com.sunllo.deskpair.store.SecretStore
import kotlin.io.encoding.Base64

/**
 * This installation's own signing key.
 *
 * The controller never had one. It verified a host's identity and pinned it, but held no private key itself
 * — which is exactly what `SecretStore`'s own comment says: "a controller-only client has no private key to
 * hold". Linking to an account needs one, so this generates a P-256 key on first use and keeps it in the
 * platform keystore (EncryptedSharedPreferences on Android, the Keychain on iOS) beside the pins and the
 * remembered passwords.
 *
 * Created lazily rather than at start-up: an install that never signs in to an account never generates one.
 *
 * Losing it means linking again. That is the right failure rather than a bad one: the key *is* the device,
 * so a new key is honestly a new device, and the console shows it as one.
 */
public class DeviceIdentity(private val secrets: SecretStore) {

    private var cached: Keys? = null

    private class Keys(val privateKeyPkcs8: ByteArray, val publicKeySpki: ByteArray)

    /** SPKI DER of the public half: what the portal stores, and what identifies this device to it. */
    public fun publicKeySpki(): ByteArray = keys().publicKeySpki

    public fun sign(data: ByteArray): ByteArray = Crypto.ecdsaSign(keys().privateKeyPkcs8, data)

    /**
     * Both halves in one stored value, separated by a colon and base64 each.
     *
     * One value rather than two entries so the halves cannot end up disagreeing: two `set` calls are two
     * chances to be interrupted, and a private key stored against somebody else's public key produces
     * signatures the portal rejects with no way to tell why. Deriving the public half from the private one
     * would be better still and is not reachable through this library's API at the version pinned here.
     */
    private fun keys(): Keys {
        cached?.let { return it }

        val stored = secrets.get(SecretKeys.DEVICE_KEY)
        if (stored != null) {
            val parsed = runCatching {
                val (priv, pub) = stored.split(':', limit = 2).let { it[0] to it[1] }
                Keys(Base64.decode(priv), Base64.decode(pub))
            }.getOrNull()

            if (parsed != null) {
                cached = parsed
                return parsed
            }

            // A truncated or re-encoded entry. Replacing it beats failing every call forever: the cost is
            // linking again, which is one code away, where the alternative is a device that can never link
            // without the app's data being cleared.
            secrets.remove(SecretKeys.DEVICE_KEY)
        }

        val pair = Crypto.generateEcdsaKeyPair()
        secrets.set(
            SecretKeys.DEVICE_KEY,
            Base64.encode(pair.privateKeyPkcs8) + ":" + Base64.encode(pair.publicKeySpki),
        )
        return Keys(pair.privateKeyPkcs8, pair.publicKeySpki).also { cached = it }
    }
}
