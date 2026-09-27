package com.sunllo.deskpair.crypto

import com.sunllo.deskpair.framing.toHex

/**
 * Decides whether the machine that just answered is the one the user meant.
 *
 * The signature in HostHello proves the host holds a private key; it does not say the key is the right one.
 * Something has to answer that, and what answers it differs by how the host was reached: an id from a
 * rendezvous server comes with the server's own signature over the identity, while a raw address comes with
 * nothing at all and has to be pinned on first use.
 */
internal interface HostIdentityVerifier {
    /** Throws if this is not the host the user asked for. Returning normally is consent. */
    fun verify(hostId: String, identityPublicKeySpki: ByteArray)
}

/**
 * Trust on first use, for a host reached by address rather than by id.
 *
 * The first connection to an address records its key; every later one must match. That does not protect the
 * first connection — nothing can, without a prior channel — but it does mean an attacker has to be in the
 * path the very first time and every time after, and the user is told when a key changes.
 */
internal class TofuIdentityVerifier(
    private val store: PinnedKeyStore,
    /**
     * What the pin is filed under: the normalised `host:port` the user dialled, **not** the id the machine
     * announces about itself. The desktop binds the same value at construction
     * (`new TofuIdentityVerifier(KnownHosts, $"{host}:{port}")`), and for the same reason — a name chosen
     * by the party being authenticated is no use for deciding whether to trust that party.
     */
    private val target: String,
    private val onKeyChanged: (address: String, pinned: String, offered: String) -> Unit = { _, _, _ -> },
) : HostIdentityVerifier {

    override fun verify(hostId: String, identityPublicKeySpki: ByteArray) {
        val fingerprint = Crypto.sha256(identityPublicKeySpki).toHex()
        val pinned = store.pinnedFingerprint(target)

        if (pinned == null) {
            store.pin(target, fingerprint)
            return
        }

        if (pinned != fingerprint) {
            onKeyChanged(target, pinned, fingerprint)
            throw HostIdentityChangedException(target, pinned, fingerprint)
        }
    }
}

/**
 * A host reached by its nine-digit id, whose identity the rendezvous server signed. The server key is
 * configured out of band, so this is the stronger of the two paths.
 */
internal class SignedIdentityVerifier(
    private val expectedId: String,
    private val expectedIdentityPk: ByteArray,
) : HostIdentityVerifier {

    override fun verify(hostId: String, identityPublicKeySpki: ByteArray) {
        if (hostId != expectedId) {
            throw HostIdentityMismatchException(
                "Asked for host $expectedId but the machine that answered calls itself $hostId.",
            )
        }

        if (!identityPublicKeySpki.contentEquals(expectedIdentityPk)) {
            throw HostIdentityMismatchException(
                "Host $hostId presented a different key from the one the rendezvous server signed for it.",
            )
        }
    }
}

/**
 * Where pinned fingerprints live. Part of the public surface because the platform has to supply it: the
 * Keychain on iOS, EncryptedSharedPreferences on Android. An implementation that forgets between launches
 * makes trust-on-first-use meaningless, so it needs to be durable.
 */
public interface PinnedKeyStore {
    public fun pinnedFingerprint(hostId: String): String?
    public fun pin(hostId: String, fingerprint: String)

    /**
     * Drops one pin, so the next connection trusts whatever answers.
     *
     * Needed because a host that is legitimately reinstalled gets a new key, and without a way to forget
     * the old one the only remedy is reinstalling the app. `IKnownHostsStore.Remove` is the same door.
     */
    public fun forget(hostId: String)

    /**
     * Everything pinned, for a settings screen to list.
     *
     * Defaulted to empty the way `IKnownHostsStore.All()` is, so a store that cannot enumerate is still a
     * usable store — it just cannot be shown.
     */
    public fun all(): List<PinnedHost> = emptyList()
}

/** One pinned host, as the settings screen shows it. */
public data class PinnedHost(val hostId: String, val fingerprint: String) {
    /**
     * The first eight bytes, which is what the desktop's security tab prints
     * (`Convert.ToHexString(fingerprint.AsSpan(0, 8))`). Enough to compare by eye, short enough to read.
     */
    public val shortFingerprint: String get() = fingerprint.take(16).uppercase()
}

/** In memory only. Fine for a test; useless as a defence, which is why it says so in its name. */
public class EphemeralPinnedKeyStore : PinnedKeyStore {
    private val pins = mutableMapOf<String, String>()
    override fun pinnedFingerprint(hostId: String): String? = pins[hostId]
    override fun pin(hostId: String, fingerprint: String) { pins[hostId] = fingerprint }
    override fun forget(hostId: String) { pins.remove(hostId) }
    override fun all(): List<PinnedHost> = pins.map { PinnedHost(it.key, it.value) }
}

/** Shown to the user rather than swallowed: this is the one case where carrying on would be the mistake. */
public class HostIdentityChangedException(
    public val hostId: String,
    public val pinnedFingerprint: String,
    public val offeredFingerprint: String,
) : Exception(
    "The key for $hostId has changed. It was $pinnedFingerprint and is now $offeredFingerprint. " +
        "Either the host was reinstalled, or something is impersonating it.",
)

public class HostIdentityMismatchException(message: String) : Exception(message)
