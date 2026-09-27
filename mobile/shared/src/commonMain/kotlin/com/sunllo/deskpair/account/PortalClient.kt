package com.sunllo.deskpair.account

import io.ktor.client.HttpClient
import io.ktor.client.call.body
import io.ktor.client.plugins.HttpTimeout
import io.ktor.client.plugins.contentnegotiation.ContentNegotiation
import io.ktor.client.request.get
import io.ktor.client.request.header
import io.ktor.client.request.post
import io.ktor.client.request.setBody
import io.ktor.client.statement.HttpResponse
import io.ktor.http.ContentType
import io.ktor.http.HttpStatusCode
import io.ktor.http.contentType
import io.ktor.serialization.kotlinx.json.json
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json

/** Anything the caller should show a person: [message] is already a sentence. */
public class PortalException(
    message: String,
    /** The machine-readable half, when the portal sent one: `bad_code`, `unlinked`. */
    public val code: String? = null,
    cause: Throwable? = null,
) : Exception(message, cause)

@Serializable
internal data class LinkRequest(
    val code: String,
    val publicKey: String,
    val signature: String,
    val peerId: String = "",
    val alias: String = "",
    val platform: String = "",
)

@Serializable
internal data class LinkResponse(
    val deviceId: String,
    val token: String,
    val account: String = "",
    val alias: String = "",
)

@Serializable
internal data class SignInRequest(val email: String, val password: String)

@Serializable
internal data class RegisterRequest(
    val email: String,
    val password: String,
    val name: String = "",
    /** What to write to this person in. Kept on the account, so later mail is in it too. */
    val language: String = "",
)

/**
 * What an email and a password buy: a linking code, not a session.
 *
 * The device is still linked by the one endpoint that checks a signature over the code, so signing in adds
 * a way of getting a code rather than a second way of becoming linked. [verify] is set instead of a code
 * when the account was made but its address has to be confirmed first.
 */
@Serializable
internal data class CodeResponse(val code: String = "", val verify: Boolean = false)

/** What this portal will let an app do, so it does not offer a button the portal would refuse. */
@Serializable
public data class AccountOptions(val selfRegistration: Boolean = false)

@Serializable
internal data class WhoAmIResponse(
    val deviceId: String,
    val account: String = "",
    val alias: String = "",
    val scopes: List<PortalScope> = emptyList(),
)

/** An address book this device may sync: the account's own list, or a team's. */
@Serializable
public data class PortalScope(val kind: String, val id: String, val name: String)

@Serializable
internal data class ApiError(val error: String = "", val message: String = "")

/** One saved computer on the wire. `folder` is what the desktop calls a group. */
@Serializable
public data class EntryChange(
    val target: String,
    val alias: String = "",
    val folder: String = "",
    val note: String = "",
    /** Unix milliseconds, 0 for never. */
    val lastConnected: Long = 0,
    val platform: String = "",
    /** A removal other devices still have to hear about. */
    val deleted: Boolean = false,
    /** The portal's revision. Set on the way out, ignored on the way in. */
    val rev: Long = 0,
)

@Serializable
public data class FolderChange(val name: String, val deleted: Boolean = false, val rev: Long = 0)

/**
 * One sync: what this client changed and the revision it last saw.
 *
 * `since` of 0 asks for the whole book, tombstones included — which is what a reinstall needs, or it would
 * quietly bring back everything the account had ever deleted.
 */
@Serializable
public data class SyncRequest(
    val scopeKind: String,
    val scopeId: String,
    val since: Long,
    val entries: List<EntryChange>,
    val folders: List<FolderChange>,
)

@Serializable
public data class SyncResponse(
    val rev: Long = 0,
    val entries: List<EntryChange> = emptyList(),
    val folders: List<FolderChange> = emptyList(),
)

/**
 * Talks to the portal's client API.
 *
 * Plain http until there is a certificate. Nothing sent here is a password: linking carries a signature
 * over a short-lived code, and everything after carries a revocable device token. That is not a substitute
 * for TLS and is not claimed to be one — it is what keeps an eavesdropper's winnings bounded to one
 * device's access, which the console can end.
 *
 * Android's network security config already allows cleartext, and this is the second reason it does.
 */
public class PortalClient internal constructor(
    baseUrl: String,
    private val client: HttpClient,
    private val ownsClient: Boolean,
) : AutoCloseable {

    public constructor(baseUrl: String) : this(baseUrl, defaultClient(), ownsClient = true)

    /** The portal's address without a trailing slash: also the audience a link signature is bound to. */
    public val baseUrl: String = normalise(baseUrl)

    internal suspend fun link(request: LinkRequest): LinkResponse =
        send {
            client.post("$baseUrl/api/v1/device/link") {
                contentType(ContentType.Application.Json)
                setBody(request)
            }
        }

    internal suspend fun signIn(email: String, password: String): CodeResponse =
        send {
            client.post("$baseUrl/api/v1/account/signin") {
                contentType(ContentType.Application.Json)
                setBody(SignInRequest(email.trim(), password))
            }
        }

    internal suspend fun register(
        email: String,
        password: String,
        name: String,
        language: String,
    ): CodeResponse =
        send {
            client.post("$baseUrl/api/v1/account/register") {
                contentType(ContentType.Application.Json)
                setBody(RegisterRequest(email.trim(), password, name.trim(), language))
            }
        }

    public suspend fun accountOptions(): AccountOptions =
        send { client.get("$baseUrl/api/v1/account/options") }

    internal suspend fun whoAmI(deviceToken: String): WhoAmIResponse =
        send { client.get("$baseUrl/api/v1/device/me") { header("Authorization", "Bearer $deviceToken") } }

    internal suspend fun syncBook(deviceToken: String, request: SyncRequest): SyncResponse =
        send {
            client.post("$baseUrl/api/v1/book/sync") {
                header("Authorization", "Bearer $deviceToken")
                contentType(ContentType.Application.Json)
                setBody(request)
            }
        }

    internal suspend fun unlink(deviceToken: String) {
        val response = call { client.post("$baseUrl/api/v1/device/unlink") { header("Authorization", "Bearer $deviceToken") } }
        failIfNeeded(response)
    }

    private suspend inline fun <reified T> send(crossinline request: suspend () -> HttpResponse): T {
        val response = call(request)
        failIfNeeded(response)
        return try {
            response.body()
        } catch (e: Throwable) {
            throw PortalException("The portal sent something this app could not read.", cause = e)
        }
    }

    private suspend inline fun call(crossinline request: suspend () -> HttpResponse): HttpResponse =
        try {
            request()
        } catch (e: Throwable) {
            // Every transport failure reads the same to a person: it did not work and here is where.
            throw PortalException("Could not reach the portal at $baseUrl.", cause = e)
        }

    private suspend fun failIfNeeded(response: HttpResponse) {
        if (response.status.isSuccess()) {
            return
        }

        // A proxy or a stray 404 answers in HTML, so the status code is the fallback rather than the error.
        val error = runCatching { response.body<ApiError>() }.getOrNull()
        if (error != null && error.message.isNotEmpty()) {
            throw PortalException(error.message, error.error.ifEmpty { null })
        }

        throw PortalException(
            if (response.status == HttpStatusCode.Unauthorized) {
                "This device is not linked to an account."
            } else {
                "The portal answered ${response.status.value}."
            },
            if (response.status == HttpStatusCode.Unauthorized) "unlinked" else null,
        )
    }

    override fun close() {
        if (ownsClient) {
            client.close()
        }
    }

    public companion object {
        /** Accepts "portal.example.com", "portal.example.com:21120" or a full URL. */
        public fun normalise(baseUrl: String): String {
            val value = baseUrl.trim().trimEnd('/')
            if (value.isEmpty()) {
                return ""
            }

            return when {
                value.contains("://") -> value
                value.contains(':') -> "http://$value"
                else -> "http://$value:21120"
            }
        }

        private fun defaultClient(): HttpClient = HttpClient {
            expectSuccess = false
            install(ContentNegotiation) {
                // The portal may add fields; an app that refuses to parse them cannot be older than it.
                json(Json { ignoreUnknownKeys = true })
            }
            install(HttpTimeout) {
                requestTimeoutMillis = 15_000
                connectTimeoutMillis = 10_000
            }
        }
    }
}

private fun HttpStatusCode.isSuccess(): Boolean = value in 200..299
