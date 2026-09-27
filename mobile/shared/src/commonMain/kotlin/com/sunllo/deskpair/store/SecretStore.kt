package com.sunllo.deskpair.store

/**
 * Small secrets, kept somewhere the operating system protects.
 *
 * Mirrors `ISecretStore` on the desktop (Platform.Abstractions/Security/ISecretStore.cs), with two
 * deliberate differences. Values are strings rather than bytes, because a controller-only client has no
 * private key to hold — the whole list is pinned fingerprints and, if the user asks for it, passwords. And
 * nothing here suspends: the Keychain and EncryptedSharedPreferences are both synchronous, and a suspend
 * function in an interface is painful to implement from Swift for no gain.
 *
 * The platform supplies the implementation, for the same reason [com.sunllo.deskpair.crypto.PinnedKeyStore]
 * does: EncryptedSharedPreferences needs a Context, and the Keychain is a Security.framework call. Neither
 * belongs in a module that also has to compile for the JVM.
 */
public interface SecretStore {
    /** The stored value, or null if there is none. */
    public fun get(key: String): String?

    public fun set(key: String, value: String)

    public fun remove(key: String)

    /**
     * Every key starting with [prefix], in no particular order.
     *
     * Needed because the settings screen has to be able to list what is trusted and forget it, which
     * `ISecretStore` cannot do and `IKnownHostsStore.All()` can.
     */
    public fun keys(prefix: String): List<String>
}

/**
 * How the two kinds of secret are named, and what a name is allowed to contain.
 *
 * The charset rule is `FileSecretStore.PathFor`'s, widened by one character: a target is `host:port`, and a
 * colon is legal in both a SharedPreferences key and a Keychain account name. Everything the original
 * rejected is still rejected, so an implementation that ever falls back to files cannot be walked out of its
 * directory.
 */
public object SecretKeys {
    private const val PIN = "pin:"
    private const val PASSWORD = "pw:"

    /** Pinned host identity, value = the SHA-256 of the identity SPKI as lowercase hex. */
    public fun pin(target: String): String = PIN + validate(target)

    /** A remembered password for a target. Only written when the user ticks the box. */
    public fun password(target: String): String = PASSWORD + validate(target)

    /**
     * This installation's own ECDSA P-256 identity, both halves in one value.
     *
     * A fixed name rather than one built from a target: there is exactly one of these per install. It is the
     * first private key a controller has ever held — see `DeviceIdentity` for why it now needs one.
     */
    public const val DEVICE_KEY: String = "device-key"

    /** The account this install is linked to, as JSON. A token, so it belongs here and not in settings. */
    public const val PORTAL_LINK: String = "portal-link"

    public val pinPrefix: String get() = PIN
    public val passwordPrefix: String get() = PASSWORD

    /** The target a key was made from, or null if it is not that kind of key. */
    public fun targetOf(key: String, prefix: String): String? =
        if (key.startsWith(prefix)) key.substring(prefix.length) else null

    private fun validate(target: String): String {
        require(target.isNotEmpty()) { "A secret key must name something." }
        require(target.length <= 256) { "Secret key is too long." }
        require(!target.contains("..")) { "Secret keys may not contain \"..\"." }
        require(target.all(::isAllowed)) {
            "Secret keys must be alphanumeric, with '.', '_', '-' and ':'."
        }
        return target
    }

    private fun isAllowed(c: Char): Boolean =
        c in 'a'..'z' || c in 'A'..'Z' || c in '0'..'9' || c == '.' || c == '_' || c == '-' || c == ':'
}

/**
 * In memory only. Fine for a test, and the honest default for an app that has not supplied a real one —
 * losing everything on exit is at least visible, where a silent file in the app's own sandbox would not be.
 */
public class EphemeralSecretStore : SecretStore {
    private val values = mutableMapOf<String, String>()

    override fun get(key: String): String? = values[key]

    override fun set(key: String, value: String) {
        values[key] = value
    }

    override fun remove(key: String) {
        values.remove(key)
    }

    override fun keys(prefix: String): List<String> = values.keys.filter { it.startsWith(prefix) }
}
