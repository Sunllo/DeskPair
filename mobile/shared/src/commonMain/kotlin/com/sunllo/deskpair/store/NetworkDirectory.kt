package com.sunllo.deskpair.store

import com.sunllo.deskpair.account.PortalClient
import io.ktor.client.HttpClient
import io.ktor.client.call.body
import io.ktor.client.plugins.HttpTimeout
import io.ktor.client.plugins.contentnegotiation.ContentNegotiation
import io.ktor.client.request.get
import io.ktor.client.statement.HttpResponse
import io.ktor.http.isSuccess
import io.ktor.serialization.kotlinx.json.json
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json

/** Where to signal, and the key to expect when we get there. Empty when the portal names none. */
public data class NetworkDirectory(
    val rendezvous: String = "",
    val publicKey: String = "",
) {
    public val isEmpty: Boolean get() = rendezvous.isEmpty()

    public companion object {
        public val None: NetworkDirectory = NetworkDirectory()
    }
}

@Serializable
private data class NetworkPayload(
    val rendezvous: String = "",
    val publicKey: String = "",
)

/**
 * Asks the portal which signalling server to use.
 *
 * The phone half of `DeskPair.Core/Portal/NetworkDirectory.cs`, and deliberately the same rules — an app
 * and a desktop that disagree about which server they are on cannot reach each other, and that is a bug
 * nobody would think to look for here.
 *
 * This exists so a server can move without every installation needing an update. **It is not a secret and
 * not a gate.** A running app connects to the address, so anyone who wants it can read it off one
 * connection; and a check the app performs on itself proves nothing to a server, because whoever removed
 * the check is the one asking. What it buys is that somebody who does not run their own servers has
 * nothing to fill in, and that we can move one.
 */
public object NetworkDirectoryClient {

    /** What a phone with nothing configured asks. The desktop's `UpdateEndpoints.OfficialPortal`. */
    public const val OFFICIAL_PORTAL: String = "https://deskpair.app"

    /**
     * The directory this portal publishes, or [NetworkDirectory.None].
     *
     * Never throws. A portal that is unreachable, or answers something unexpected, leads to the same next
     * move as one that publishes nothing: carry on with direct connections. Making the caller handle an
     * exception for that would put a try/catch on the path that opens the app.
     */
    public suspend fun fetch(portalServer: String, timeoutMillis: Long = 8_000): NetworkDirectory {
        val base = PortalClient.normalise(portalServer).ifEmpty { OFFICIAL_PORTAL }
        val client = HttpClient {
            expectSuccess = false
            install(ContentNegotiation) { json(Json { ignoreUnknownKeys = true }) }
            install(HttpTimeout) {
                requestTimeoutMillis = timeoutMillis
                connectTimeoutMillis = timeoutMillis
            }
        }

        return try {
            val response: HttpResponse = client.get("$base/api/v1/network")
            if (!response.status.isSuccess()) {
                NetworkDirectory.None
            } else {
                validate(response.body())
            }
        } catch (_: Throwable) {
            NetworkDirectory.None
        } finally {
            client.close()
        }
    }

    /**
     * The signalling server to actually use, and the key to pin.
     *
     * A configured address always wins and is never overwritten: somebody who typed one meant it, and
     * quietly preferring a fetched one would move a self-hosted installation onto our servers without
     * telling them. The fetched address is used, never stored — writing it into the settings file would
     * freeze today's answer into this installation, which is what asking was meant to avoid.
     */
    public suspend fun resolve(settings: AppSettings): NetworkDirectory = when {
        settings.rendezvousServer.isNotEmpty() ->
            NetworkDirectory(settings.rendezvousServer, settings.serverPublicKeyBase64)
        !settings.useDirectoryServers -> NetworkDirectory.None
        else -> fetch(settings.portalServer)
    }

    /**
     * Everything here is somebody else's data.
     *
     * The address goes into a socket and the key into a signature check, and neither is a place to find out
     * that a string was not what it claimed. An absent key is allowed and means "pin nothing"; a malformed
     * one is not, and discards the whole answer — treating it as absent would respond to something going
     * wrong by verifying less.
     */
    private fun validate(payload: NetworkPayload): NetworkDirectory {
        val host = payload.rendezvous.trim()
        if (host.isEmpty() || host.length > 253 || host.any { it.isWhitespace() || it.isISOControl() }) {
            return NetworkDirectory.None
        }

        val key = payload.publicKey.trim()
        if (key.isNotEmpty() && !ServerKey.isValid(key)) {
            return NetworkDirectory.None
        }

        return NetworkDirectory(host, key)
    }
}
