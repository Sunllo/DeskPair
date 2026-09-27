package com.sunllo.deskpair.transport

import com.sunllo.deskpair.framing.FramedStream
import com.sunllo.deskpair.framing.ProtocolException
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.withTimeout
import sunllo.rendezvous.NatType
import sunllo.rendezvous.RendezvousMessage
import sunllo.rendezvous.TestNatRequest
import kotlin.time.Duration
import kotlin.time.Duration.Companion.minutes
import kotlin.time.Duration.Companion.seconds

/**
 * Works out whether this network can be punched through, by asking the same question from one local port to
 * two different server ports and seeing whether the answers agree.
 *
 * A NAT that maps a local port to the same public port whatever the destination is endpoint-independent,
 * and a peer told that public port can reach us on it. A symmetric NAT picks a fresh public port per
 * destination, so the port the server saw is worthless to anyone else and only a relay will do.
 *
 * Both probes must leave from the same local port or the comparison means nothing, which is the whole
 * reason the sockets underneath can bind one.
 */
internal class NatTypeDetector(
    private val serverHost: String,
    private val serverPort: Int = Ports.RENDEZVOUS,
    private val natTestPort: Int = Ports.NAT_TEST,
) {

    private companion object {
        val PROBE_TIMEOUT = 4.seconds
    }

    /** The last answer, kept because the network rarely changes and each probe costs two round trips. */
    var cached: NatType = NatType.NAT_UNKNOWN
        private set

    private var detectedAtMillis = 0L
    private var serial = 0

    var cacheFor: Duration = 10.minutes

    /**
     * The local port both probes went out from, which is also the port a punch must use. Comparing
     * observed ports proves nothing unless the punch then reuses the port that was observed.
     */
    var probedLocalPort: Int = 0
        private set

    suspend fun detect(nowMillis: Long): NatType {
        if (cached != NatType.NAT_UNKNOWN && nowMillis - detectedAtMillis < cacheFor.inWholeMilliseconds) {
            return cached
        }

        return try {
            val (first, localPort) = probe(localPort = 0, port = natTestPort)
            val (second, _) = probe(localPort = localPort, port = serverPort)

            probedLocalPort = localPort
            detectedAtMillis = nowMillis
            cached = if (first == second) NatType.NAT_ASYMMETRIC else NatType.NAT_SYMMETRIC
            cached
        } catch (e: Throwable) {
            if (e is kotlinx.coroutines.CancellationException && e !is TimeoutCancellationException) {
                throw e
            }
            // Not knowing is an answer the caller can act on: it means "do not gamble on a punch".
            NatType.NAT_UNKNOWN
        }
    }

    private suspend fun probe(localPort: Int, port: Int): Pair<Int, Int> {
        val connection = connectReusable(localPort, serverHost, port, PROBE_TIMEOUT.inWholeMilliseconds)
        connection.use {
            val stream = FramedStream(it.stream, FramedStream.MAX_CONTROL_FRAME_BYTES)
            stream.send(RendezvousMessage(test_nat_request = TestNatRequest(serial = ++serial)).encode())

            val reply = withTimeout(PROBE_TIMEOUT) {
                stream.receive() ?: throw ProtocolException("The NAT test connection closed without answering.")
            }

            val observed = RendezvousMessage.ADAPTER.decode(reply).test_nat_response
                ?: throw ProtocolException("The rendezvous server answered a NAT test with something else.")

            return observed.observed_port.toInt() to it.localPort
        }
    }
}
