package com.sunllo.deskpair.transport

import kotlinx.coroutines.delay
import kotlin.time.Duration
import kotlin.time.Duration.Companion.milliseconds
import kotlin.time.Duration.Companion.seconds
import kotlin.time.TimeSource

/**
 * The controller's half of a TCP hole punch.
 *
 * Only half, deliberately. A phone is never a host here, so it never listens and never answers a punch; it
 * asks the rendezvous server to poke the host, and then knocks on the address it is given until something
 * answers or the clock runs out.
 *
 * Every attempt leaves from the same local port. That is the point: the server observed a public port for
 * that local port, told the host about it, and the host's own outgoing packet has opened its NAT for
 * exactly that pairing. Connecting from anywhere else arrives at a hole that was never made.
 *
 * Repeated attempts are not impatience. Both sides are opening their NATs at once and neither knows when
 * the other got there, so the first few connects legitimately fail; the retry is what makes the timing
 * work out.
 */
internal class TcpPuncher {

    private companion object {
        val ATTEMPT_TIMEOUT = 400.milliseconds
        val BACKOFF = 150.milliseconds
    }

    /**
     * Knocks until [timeout] runs out. Returns null rather than throwing when nothing answered: a punch
     * that fails is an ordinary outcome with a relay waiting behind it, not an error.
     */
    suspend fun connect(
        localPort: Int,
        host: String,
        port: Int,
        timeout: Duration = 8.seconds,
    ): PeerConnection? {
        val deadline = TimeSource.Monotonic.markNow() + timeout
        var attempts = 0

        while (deadline.hasNotPassedNow()) {
            attempts++
            val connection = try {
                connectReusable(localPort, host, port, ATTEMPT_TIMEOUT.inWholeMilliseconds)
            } catch (_: SocketException) {
                null
            } catch (e: Throwable) {
                if (e is kotlinx.coroutines.CancellationException) {
                    throw e
                }
                null
            }

            if (connection != null) {
                return PeerConnection(connection.stream, TransportKind.PUNCHED_TCP)
            }

            delay(BACKOFF)
        }

        lastAttempts = attempts
        return null
    }

    /** How many knocks the last attempt took, so a caller can say whether it was close or hopeless. */
    var lastAttempts: Int = 0
        private set
}
