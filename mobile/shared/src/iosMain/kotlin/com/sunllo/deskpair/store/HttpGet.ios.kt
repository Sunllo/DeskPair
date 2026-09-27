package com.sunllo.deskpair.store

import kotlinx.coroutines.suspendCancellableCoroutine
import platform.Foundation.NSData
import platform.Foundation.NSHTTPURLResponse
import platform.Foundation.NSString
import platform.Foundation.NSURL
import platform.Foundation.NSURLSession
import platform.Foundation.NSURLSessionConfiguration
import platform.Foundation.NSUTF8StringEncoding
import platform.Foundation.create
import platform.Foundation.dataTaskWithURL
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException

/**
 * NSURLSession, which is in the system and needs no dependency.
 *
 * The task is cancelled if the coroutine is, so a settings screen dismissed mid-fetch does not leave a
 * request running against a server that may be unreachable for the whole timeout.
 */
internal actual suspend fun httpGetText(url: String, timeoutMillis: Int): String =
    suspendCancellableCoroutine { continuation ->
        val target = NSURL.URLWithString(url)
        if (target == null) {
            continuation.resumeWithException(IllegalArgumentException("Not a usable address: $url"))
            return@suspendCancellableCoroutine
        }

        val seconds = timeoutMillis / 1000.0
        val configuration = NSURLSessionConfiguration.ephemeralSessionConfiguration().apply {
            setTimeoutIntervalForRequest(seconds)
            setTimeoutIntervalForResource(seconds)
        }

        // No request object: GET is the default, and the timeouts are already on the configuration.
        val task = NSURLSession.sessionWithConfiguration(configuration)
            .dataTaskWithURL(target) { data, response, error ->
                if (error != null) {
                    continuation.resumeWithException(IllegalStateException(error.localizedDescription))
                    return@dataTaskWithURL
                }

                val status = (response as? NSHTTPURLResponse)?.statusCode?.toInt() ?: 0
                if (status !in 200..299) {
                    continuation.resumeWithException(IllegalStateException("The server answered $status."))
                    return@dataTaskWithURL
                }

                continuation.resume(decodeUtf8(data))
            }

        continuation.invokeOnCancellation { task.cancel() }
        task.resume()
    }

@OptIn(kotlinx.cinterop.BetaInteropApi::class)
private fun decodeUtf8(data: NSData?): String {
    if (data == null) {
        return ""
    }

    return NSString.create(data = data, encoding = NSUTF8StringEncoding)?.toString() ?: ""
}
