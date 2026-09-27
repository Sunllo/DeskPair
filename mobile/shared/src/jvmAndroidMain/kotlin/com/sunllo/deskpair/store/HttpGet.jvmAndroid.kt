package com.sunllo.deskpair.store

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URI

/**
 * HttpURLConnection, which is in the platform on both the JVM and Android and needs no dependency.
 *
 * Runs on the IO dispatcher because every call on it blocks; on Android the main thread would throw
 * NetworkOnMainThreadException rather than merely stutter.
 */
internal actual suspend fun httpGetText(url: String, timeoutMillis: Int): String = withContext(Dispatchers.IO) {
    val connection = URI(url).toURL().openConnection() as HttpURLConnection
    try {
        connection.requestMethod = "GET"
        connection.connectTimeout = timeoutMillis
        connection.readTimeout = timeoutMillis
        connection.instanceFollowRedirects = true

        val status = connection.responseCode
        if (status !in 200..299) {
            throw IOException("The server answered $status ${connection.responseMessage.orEmpty()}.")
        }

        connection.inputStream.bufferedReader().use { it.readText() }
    } finally {
        connection.disconnect()
    }
}
