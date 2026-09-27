package com.sunllo.deskpair.store

import com.sunllo.deskpair.crypto.PinnedHost
import com.sunllo.deskpair.crypto.PinnedKeyStore

/**
 * Trust-on-first-use pins that survive the app being closed.
 *
 * Until now both apps used [com.sunllo.deskpair.crypto.EphemeralPinnedKeyStore], which is honest about
 * being a test double: every pin was forgotten at exit, so every launch was a first use and the guarantee
 * TOFU is supposed to give — that an attacker has to be in the path *every* time, not just once — was not
 * being given at all.
 *
 * The keystore is the right home even though a fingerprint is not secret. What matters here is not
 * confidentiality but integrity: anyone who can rewrite the pin file can silently defeat the check, and on
 * a phone the keystore is the storage the OS actually defends.
 */
public class PersistentPinnedKeyStore(private val secrets: SecretStore) : PinnedKeyStore {

    override fun pinnedFingerprint(hostId: String): String? = secrets.get(SecretKeys.pin(hostId))

    override fun pin(hostId: String, fingerprint: String) {
        secrets.set(SecretKeys.pin(hostId), fingerprint)
    }

    override fun forget(hostId: String) {
        secrets.remove(SecretKeys.pin(hostId))
    }

    override fun all(): List<PinnedHost> =
        secrets.keys(SecretKeys.pinPrefix).mapNotNull { key ->
            val target = SecretKeys.targetOf(key, SecretKeys.pinPrefix) ?: return@mapNotNull null
            val fingerprint = secrets.get(key) ?: return@mapNotNull null
            PinnedHost(target, fingerprint)
        }.sortedBy { it.hostId }
}

/**
 * Passwords the user asked to be remembered.
 *
 * The desktop has no equivalent — `SessionViewModelBase.Password` is transient and nothing writes it
 * anywhere — so this is a new capability rather than a port. It follows the desktop's instinct about
 * *where* such a thing belongs: the host's own password hashes live in the keystore, not in `desktop.json`,
 * and a settings file in plain text is not somewhere to put a password that opens someone's desk.
 */
public class RememberedPasswords(private val secrets: SecretStore) {

    public fun get(target: String): String? = secrets.get(SecretKeys.password(target))

    public fun remember(target: String, password: String) {
        if (password.isEmpty()) {
            forget(target)
        } else {
            secrets.set(SecretKeys.password(target), password)
        }
    }

    public fun forget(target: String) {
        secrets.remove(SecretKeys.password(target))
    }

    /** The targets that have a password stored. The passwords themselves never leave this class. */
    public fun targets(): List<String> =
        secrets.keys(SecretKeys.passwordPrefix)
            .mapNotNull { SecretKeys.targetOf(it, SecretKeys.passwordPrefix) }
            .sorted()

    /** Offered in settings, because "which machines does this phone hold a key to" deserves one button. */
    public fun forgetAll() {
        secrets.keys(SecretKeys.passwordPrefix).forEach(secrets::remove)
    }
}
