package com.sunllo.deskpair.store

import com.sunllo.deskpair.transport.Ports
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okio.ByteString.Companion.decodeBase64
import kotlin.coroutines.cancellation.CancellationException

/**
 * The rendezvous server's signing key: cleaning it up, and fetching it when nobody wants to type it.
 *
 * Without this key `PeerConnector.BuildVerifier` falls back to accepting any identity at all, so a
 * connection by id verifies nothing. It matters more on a phone than on a desktop, because a ninety-one
 * byte SPKI is not something anyone is going to paste correctly with their thumbs — which is why the
 * desktop grew a fetch button (`NetworkSettingsViewModel.FetchKeyFromServerAsync`) and why the QR code in
 * Phase 3 carries this same value.
 */
public object ServerKey {

    /**
     * Tolerates the damage pasting does, exactly as `PeerSettings.CleanBase64` does.
     *
     * Surrounding quotes or backticks, a short `key:` style prefix, and all internal whitespace come off.
     * The prefix test deliberately only looks at the first twelve characters and only when what precedes
     * the colon contains no base64 punctuation, so a key that happens to contain a colon is left alone.
     */
    public fun clean(value: String): String {
        var s = value.trim().trim('"', '\'', '`')
        val colon = s.indexOf(':')
        if (colon in 0..11 && s.take(colon).none { it == '+' || it == '/' || it == '=' }) {
            s = s.substring(colon + 1).trim()
        }

        return s.filterNot { it.isWhitespace() }
    }

    /** The decoded SPKI, or null if nothing is configured. Throws if what is configured is not base64. */
    public fun decode(value: String): ByteArray? {
        val cleaned = clean(value)
        if (cleaned.isEmpty()) {
            return null
        }

        val bytes = cleaned.decodeBase64()
            ?: throw IllegalArgumentException(
                "The rendezvous server public key is not valid base64. " +
                    "Fetch it from http://<server>:${Ports.HTTP_API}/key.",
            )

        return bytes.toByteArray()
    }

    /** True if this could be stored as-is. Used to keep a half-typed key on screen without writing it. */
    public fun isValid(value: String): Boolean = try {
        decode(value)
        true
    } catch (_: IllegalArgumentException) {
        false
    }

    /**
     * Asks the server for its own public key over plain HTTP.
     *
     * Plain HTTP is not the weakness it looks like: the key is public, and a key fetched from an attacker
     * would only let that attacker impersonate hosts on their own server, which they could do anyway by
     * being the server. The QR path in Phase 3 is the one that carries it out of band.
     */
    @Throws(CancellationException::class, Throwable::class)
    public suspend fun fetch(rendezvousServer: String, timeoutMillis: Int = 8_000): String =
        withContext(Dispatchers.Default) {
            val (host, _) = Targets.splitAddress(rendezvousServer, Ports.RENDEZVOUS)
            require(host.isNotEmpty()) { "Name a rendezvous server first." }

            val body = httpGetText("http://$host:${Ports.HTTP_API}/key", timeoutMillis)
            val cleaned = clean(body)
            // Validate before handing it back, so a server that answers with an error page is caught here
            // rather than at the next connection attempt.
            decode(cleaned)
            cleaned
        }
}

/**
 * One HTTP GET, returning the body as text.
 *
 * Deliberately not ktor-client: the module already carries ktor-network for raw sockets, and pulling in an
 * HTTP client with its own engine per platform to make a single unauthenticated request to fetch a public
 * key would be a great deal of dependency for forty lines of platform code.
 */
internal expect suspend fun httpGetText(url: String, timeoutMillis: Int): String
