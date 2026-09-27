package com.sunllo.deskpair.account

import com.sunllo.deskpair.store.NetworkDirectoryClient
import com.sunllo.deskpair.store.SecretKeys
import com.sunllo.deskpair.store.SecretStore
import kotlin.io.encoding.Base64
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json

/** What this installation knows about the account it belongs to, if any. */
public data class LinkState(
    val deviceId: String = "",
    val account: String = "",
    val alias: String = "",
    /** The portal this device is linked to, which may differ from what settings currently point at. */
    val portalUrl: String = "",
    /** Address books this device may sync. Empty until the portal has been asked. */
    val scopes: List<PortalScope> = emptyList(),
) {
    public val isLinked: Boolean get() = deviceId.isNotEmpty()

    public companion object {
        public val UNLINKED: LinkState = LinkState()
    }
}

/** The part of the link that has to survive a restart. Kept in the platform keystore, not in settings. */
@Serializable
internal data class StoredLink(
    val portalUrl: String,
    val deviceId: String,
    val token: String,
    val account: String,
    val alias: String,
)

/**
 * Binds this installation to a portal account, using the key [DeviceIdentity] holds.
 *
 * Mirrors `Core/Portal/AccountLink.cs`, and on purpose: the two clients must behave the same way, because
 * the portal cannot tell them apart and a person with a phone and a laptop expects one to work like the
 * other. No account password is ever typed into the app; what is stored is a revocable token, in the
 * keystore beside the private key rather than in settings.
 */
public class AccountLink(
    private val identity: DeviceIdentity,
    private val secrets: SecretStore,
    private val platform: String,
    private val clientFor: (String) -> PortalClient = { PortalClient(it) },
) {
    private val json = Json { ignoreUnknownKeys = true }

    /**
     * Sends the code and a signature over [DeviceLink.challenge], and keeps what comes back.
     *
     * The audience is the portal's base URL as this client computed it, which must match what the portal is
     * configured with. A mismatch fails as a bad signature — the one confusing failure here, and worth
     * knowing about when a link refuses for no visible reason.
     */
    public suspend fun link(portalUrl: String, code: String, alias: String): LinkState {
        val client = clientFor(portalFor(portalUrl))
        if (client.baseUrl.isEmpty()) {
            client.close()
            throw PortalException("No portal address is set.")
        }

        return client.use {
            val normalised = DeviceLink.normalise(code)
            val signature = identity.sign(DeviceLink.challenge(normalised, it.baseUrl))
            val response = it.link(
                LinkRequest(
                    code = normalised,
                    publicKey = Base64.encode(identity.publicKeySpki()),
                    signature = Base64.encode(signature),
                    alias = alias,
                    platform = platform,
                ),
            )

            save(StoredLink(it.baseUrl, response.deviceId, response.token, response.account, response.alias))
            LinkState(response.deviceId, response.account, response.alias, it.baseUrl)
        }
    }

    /**
     * Signs in with an email and a password, and links this device with what that buys.
     *
     * Two calls rather than one endpoint that does both, because linking means signing a challenge derived
     * from the code: the server cannot do that half and the app cannot skip it. So the portal hands back a
     * code and this goes on to [link] with it, which keeps one implementation of the signature check
     * instead of two.
     *
     * Neither the address nor the password is stored anywhere. What survives is the device token, exactly
     * as it does when somebody types a code from the website.
     */
    public suspend fun signIn(portalUrl: String, email: String, password: String, alias: String): LinkState {
        val code = clientFor(portalFor(portalUrl)).use { it.signIn(email, password).code }
        return link(portalUrl, code, alias)
    }

    /**
     * Makes an account and links this device to it.
     *
     * Returns [LinkState.UNLINKED] when the portal wants the address confirmed first: the account exists
     * by then, so the app says to check the inbox rather than repeating the form.
     */
    public suspend fun register(
        portalUrl: String,
        email: String,
        password: String,
        name: String,
        alias: String,
        language: String,
    ): LinkState {
        val answer = clientFor(portalFor(portalUrl)).use { it.register(email, password, name, language) }
        if (answer.verify || answer.code.isEmpty()) {
            return LinkState.UNLINKED
        }

        return link(portalUrl, answer.code, alias)
    }

    /** What this portal will let an app do. */
    public suspend fun options(portalUrl: String): AccountOptions =
        clientFor(portalFor(portalUrl)).use { it.accountOptions() }

    /**
     * The portal to talk to: the configured one, or ours.
     *
     * Empty settings mean the official portal, which is the rule signalling has followed since the
     * directory landed -- [NetworkDirectoryClient.OFFICIAL_PORTAL] is the same constant. The account half
     * did not follow it, and threw "No portal address is set" instead, which on a fresh install meant
     * signing in could not work at all and the screen could not even find out whether the portal took new
     * accounts: it asked, was refused for having no address, swallowed that quietly, and hid the button.
     *
     * A person who has typed an address in gets theirs, exactly as before.
     */
    private fun portalFor(portalUrl: String): String =
        portalUrl.ifBlank { NetworkDirectoryClient.OFFICIAL_PORTAL }

    /** What is stored, without asking the portal. Safe offline, and what the settings screen shows first. */
    public fun current(): LinkState {
        val stored = read() ?: return LinkState.UNLINKED
        return LinkState(stored.deviceId, stored.account, stored.alias, stored.portalUrl)
    }

    /**
     * Asks the portal whether this device is still linked, and what it may sync.
     *
     * A revoked device learns it here and forgets its token. Keeping a dead token means every later call
     * fails the same way and the app can never tell "revoked" from "the portal is down".
     */
    public suspend fun refresh(): LinkState {
        val stored = read() ?: return LinkState.UNLINKED
        return clientFor(stored.portalUrl).use { client ->
            val response = try {
                client.whoAmI(stored.token)
            } catch (e: PortalException) {
                if (e.code == "unlinked") {
                    secrets.remove(SecretKeys.PORTAL_LINK)
                    return@use LinkState.UNLINKED
                }
                throw e
            }

            if (response.account != stored.account || response.alias != stored.alias) {
                // Renamed in the console, or re-linked to a different account from elsewhere.
                save(stored.copy(account = response.account, alias = response.alias))
            }

            LinkState(response.deviceId, response.account, response.alias, stored.portalUrl, response.scopes)
        }
    }

    /**
     * Drops the link from this end, telling the portal first so the device stops appearing as live.
     *
     * The local token goes whether or not the portal could be reached, and the failure is returned rather
     * than thrown. Someone unlinking a phone they are about to hand over cares that it stops working here;
     * keeping the credential because the network was down would be the wrong way round.
     */
    public suspend fun unlink(): PortalException? {
        val stored = read() ?: return null
        var failure: PortalException? = null
        try {
            clientFor(stored.portalUrl).use { it.unlink(stored.token) }
        } catch (e: PortalException) {
            failure = e
        }

        secrets.remove(SecretKeys.PORTAL_LINK)
        return failure
    }

    /** The bearer token for API calls, or null when this device is not linked. */
    public fun credential(): Pair<String, String>? = read()?.let { it.portalUrl to it.token }

    private fun read(): StoredLink? {
        val raw = secrets.get(SecretKeys.PORTAL_LINK) ?: return null
        return runCatching { json.decodeFromString<StoredLink>(raw) }.getOrNull()
    }

    private fun save(link: StoredLink) {
        secrets.set(SecretKeys.PORTAL_LINK, json.encodeToString(link))
    }
}
