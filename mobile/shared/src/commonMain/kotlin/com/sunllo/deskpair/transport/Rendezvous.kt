package com.sunllo.deskpair.transport

import com.sunllo.deskpair.crypto.Crypto
import com.sunllo.deskpair.crypto.HostIdentityVerifier
import com.sunllo.deskpair.crypto.HostIdentityMismatchException
import com.sunllo.deskpair.crypto.SignedIdentityVerifier
import com.sunllo.deskpair.framing.FramedStream
import com.sunllo.deskpair.framing.ProtocolException
import io.ktor.network.selector.SelectorManager
import io.ktor.network.sockets.InetSocketAddress
import io.ktor.network.sockets.aSocket
import io.ktor.network.sockets.openReadChannel
import io.ktor.network.sockets.openWriteChannel
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.withTimeout
import sunllo.rendezvous.ConnType
import sunllo.rendezvous.NatType
import sunllo.rendezvous.PeerIdentity
import sunllo.rendezvous.PunchHoleRequest
import sunllo.rendezvous.PunchHoleResponse
import sunllo.rendezvous.RendezvousMessage
import sunllo.rendezvous.RequestRelay
import sunllo.rendezvous.SignedPeerIdentity
import kotlin.random.Random
import kotlin.time.Duration.Companion.seconds

/** The well-known ports, matching `DeskPair.Protocol.ProtocolConstants`. */
internal object Ports {
    const val HTTP_API = 21114
    const val NAT_TEST = 21115
    const val RENDEZVOUS = 21116
    const val RELAY = 21117
    const val DIRECT_ACCESS = 21118
}

/**
 * Finds a host by its nine-digit id and opens a way to reach it.
 *
 * Phase 1 asks the rendezvous server to arrange a relay and takes it. That works from anywhere, including
 * behind the carrier-grade NAT a phone usually sits behind, and it costs a round trip through someone else's
 * server. Punching a hole straight through, which is faster when it works, comes later; asking for a relay
 * outright is a single boolean and skips NAT detection, port reuse and the listener race entirely.
 */
internal class RendezvousClient(
    private val serverHost: String,
    private val serverPort: Int = Ports.RENDEZVOUS,
    private val serverPublicKeySpki: ByteArray,
    private val selector: SelectorManager = SelectorManager(Dispatchers.Default),
    private val clientVersion: String,
    private val connType: ConnType = ConnType.CONN_REMOTE,
) {

    private companion object {
        val REQUEST_TIMEOUT = 10.seconds

        /** The server says this when it is overloaded, and means "ask again", not "give up". */
        const val SERVER_BUSY = "SERVER_BUSY"
        const val ATTEMPTS = 3
    }

    /**
     * Asks where the host is. Returns everything needed to open the session, including the verifier that
     * binds the host's key to what the rendezvous server signed for that id.
     */
    suspend fun locate(
        hostId: String,
        forceRelay: Boolean = true,
        natType: NatType = NatType.NAT_UNKNOWN,
        /**
         * The port to ask from. Zero lets the system choose, which is right for a relay; a punch must ask
         * from the port it will later knock from, or the public port the server observes belongs to a
         * mapping nobody will use again.
         */
        localPort: Int = -1,
    ): RendezvousResult = locateFrom(hostId, forceRelay, natType, localPort).first

    /**
     * The same request, also reporting the local port it went out from.
     *
     * A punch needs that port. The server tells the host what public port it observed, and the host opens
     * its NAT toward it — so the knock has to come from the same local port, or it arrives at a mapping
     * the server never described.
     */
    suspend fun locateFrom(
        hostId: String,
        forceRelay: Boolean,
        natType: NatType,
        localPort: Int,
    ): Pair<RendezvousResult, Int> {
        var lastFailure: String? = null
        var usedPort = 0

        repeat(ATTEMPTS) { attempt ->
            val (reply, port) = exchange(
                RendezvousMessage(
                    punch_hole_request = PunchHoleRequest(
                        id = hostId,
                        nat_type = natType,
                        conn_type = connType,
                        force_relay = forceRelay,
                        version = clientVersion,
                    ),
                ),
                timeout = REQUEST_TIMEOUT * (attempt + 1),
                localPort = localPort,
            )
            usedPort = port

            reply.relay_response?.let { relay ->
                val identity = relay.identity
                    ?: throw ProtocolException("The rendezvous server offered a relay without a signed identity.")
                return RendezvousResult.Relayed(
                    relayServer = relay.relay_server.ifEmpty { serverHost },
                    uuid = relay.uuid,
                    hostId = hostId,
                    verifier = verifierFor(hostId, identity),
                    ticket = relay.ticket,
                ) to usedPort
            }

            reply.punch_hole_response?.let { punch ->
                if (punch.failure != PunchHoleResponse.Failure.NONE) {
                    lastFailure = punch.failure.name
                    // Only this one is worth another go; the others are answers, not accidents.
                    if (punch.failure.name != SERVER_BUSY) {
                        val cause = when (punch.failure) {
                            PunchHoleResponse.Failure.ID_NOT_EXIST -> UnreachableCause.UNKNOWN_ID
                            PunchHoleResponse.Failure.OFFLINE -> UnreachableCause.OFFLINE
                            else -> UnreachableCause.SERVER_BUSY
                        }
                        throw HostUnreachableException(hostId, punch.failure.name, cause)
                    }
                    return@repeat
                }

                val identity = punch.identity
                    ?: throw ProtocolException("The rendezvous server located the host but signed no identity for it.")

                // With force_relay the server should have answered with a relay; if it points at the host
                // directly, take that — it is strictly better.
                val address = punch.host_addr
                return RendezvousResult.Direct(
                    host = address?.let { formatIp(it.ip.toByteArray()) }
                        ?: throw ProtocolException("The rendezvous server located the host but gave no address."),
                    port = address.port,
                    isLocal = punch.is_local,
                    verifier = verifierFor(hostId, identity),
                ) to usedPort
            }

            throw ProtocolException("The rendezvous server sent something that is neither an address nor a relay.")
        }

        throw HostUnreachableException(
            hostId,
            lastFailure ?: "no answer",
            if (lastFailure != null) UnreachableCause.SERVER_BUSY else UnreachableCause.NO_ANSWER,
        )
    }

    /**
     * The rendezvous stream is plaintext and short-lived: one request, one reply, then it closes. It is also
     * capped far below a peer stream, because nothing that belongs on it is large.
     */
    private suspend fun exchange(
        message: RendezvousMessage,
        timeout: kotlin.time.Duration,
        localPort: Int = 0,
    ): Pair<RendezvousMessage, Int> {
        // A chosen local port needs the socket that can bind one; anything else is better served by ktor,
        // which handles name resolution and dual-stack fallback on its own.
        val socket: AutoCloseable
        val bytes: ByteStream
        val usedPort: Int
        if (localPort < 0) {
            // Negative means "ktor's choice"; nothing downstream needs the port and ktor resolves names
            // and falls back across address families better than the socket written here does.
            val ktor = aSocket(selector).tcp().connect(InetSocketAddress(serverHost, serverPort))
            socket = AutoCloseable { ktor.close() }
            bytes = KtorByteStream(ktor)
            usedPort = 0
        } else {
            // Zero still binds explicitly, because the caller wants to be told which port it got.
            val reusable = connectReusable(localPort, serverHost, serverPort, timeout.inWholeMilliseconds)
            socket = reusable
            bytes = reusable.stream
            usedPort = reusable.localPort
        }

        try {
            val stream = FramedStream(bytes, FramedStream.MAX_CONTROL_FRAME_BYTES)
            stream.send(message.encode())

            val reply = try {
                withTimeout(timeout) { stream.receive() }
            } catch (e: TimeoutCancellationException) {
                throw ProtocolException("The rendezvous server at $serverHost:$serverPort did not answer within $timeout.")
            } ?: throw ProtocolException("The rendezvous server closed the connection without answering.")

            return RendezvousMessage.ADAPTER.decode(reply) to usedPort
        } finally {
            socket.close()
        }
    }

    /**
     * The server's signature over the host's identity is what makes an id trustworthy. Checked here, before
     * a connection is even opened, so a wrong answer costs nothing.
     */
    private fun verifierFor(hostId: String, signed: SignedPeerIdentity): HostIdentityVerifier {
        val payload = signed.payload.toByteArray()
        if (!Crypto.ecdsaVerify(serverPublicKeySpki, payload, signed.server_signature.toByteArray())) {
            // In practice this is not an attack but a rotated server key held against a pinned old one: a
            // server redeployed with a fresh key failed every phone that had its address typed in, while
            // the desktops, which ask the portal's directory on every start, carried on. The pin is right
            // to refuse; the sentence has to say what to do about it.
            throw HostIdentityMismatchException(
                "The rendezvous server's signature over the identity of $hostId does not verify. " +
                    "The server's key has probably changed since it was saved: in Settings, update the " +
                    "server public key, or clear the server address to use the portal's directory.",
            )
        }

        val identity = PeerIdentity.ADAPTER.decode(payload)
        if (identity.id != hostId) {
            throw HostIdentityMismatchException(
                "Asked for host $hostId but the server signed an identity for ${identity.id}.",
            )
        }

        return SignedIdentityVerifier(hostId, identity.identity_pk.toByteArray())
    }
}

/** Where the rendezvous server says the host is, and how to prove it is the right one when we get there. */
internal sealed interface RendezvousResult {
    val verifier: HostIdentityVerifier

    data class Direct(
        val host: String,
        val port: Int,
        val isLocal: Boolean,
        override val verifier: HostIdentityVerifier,
    ) : RendezvousResult

    data class Relayed(
        val relayServer: String,
        val uuid: String,
        val hostId: String,
        override val verifier: HostIdentityVerifier,
        /** The server's permission to use its relay; a relay that checks tickets refuses a request without one. */
        val ticket: sunllo.rendezvous.RelayTicket? = null,
    ) : RendezvousResult
}

/**
 * Joins a relay session. The relay pairs the two sockets presenting the same uuid and then splices bytes
 * without looking at them — it holds no keys and could not read the session if it wanted to.
 */
internal class RelayClient(
    private val selector: SelectorManager = SelectorManager(Dispatchers.Default),
    private val clientId: String,
    private val connType: ConnType = ConnType.CONN_REMOTE,
) {

    suspend fun join(relayServer: String, uuid: String, hostId: String, ticket: sunllo.rendezvous.RelayTicket? = null): PeerConnection {
        val (host, port) = splitHostPort(relayServer, Ports.RELAY)
        val socket = aSocket(selector).tcp().connect(InetSocketAddress(host, port)) { noDelay = true }

        val stream = KtorByteStream(socket)

        // One plaintext frame, then the socket simply is the peer stream. The handshake that follows is the
        // same one a direct connection uses, which is why a relay never needs to be trusted.
        FramedStream(stream, FramedStream.MAX_CONTROL_FRAME_BYTES).send(
            RendezvousMessage(
                request_relay = RequestRelay(
                    uuid = uuid,
                    id = clientId,
                    conn_type = connType,
                    ticket = ticket,
                ),
            ).encode(),
        )

        return PeerConnection(stream, TransportKind.RELAY)
    }
}

/** Why a host could not be reached, for the app to say in the reader's language. */
enum class UnreachableCause { UNKNOWN_ID, OFFLINE, SERVER_BUSY, NO_RELAY, NO_ANSWER }

/** The rendezvous server knows the id but cannot reach the host, or does not know it at all. */
class HostUnreachableException(val hostId: String, val reason: String, val why: UnreachableCause) :
    Exception("The host $hostId could not be reached: $reason")

/**
 * SocketAddress.ip is raw bytes, four for IPv4 and sixteen for IPv6, not text. Anything else is the server
 * saying something this client does not understand, and guessing would connect somewhere unintended.
 */
internal fun formatIp(bytes: ByteArray): String = when (bytes.size) {
    4 -> bytes.joinToString(".") { (it.toInt() and 0xFF).toString() }
    16 -> (0 until 8).joinToString(":") { i ->
        val group = ((bytes[i * 2].toInt() and 0xFF) shl 8) or (bytes[i * 2 + 1].toInt() and 0xFF)
        group.toString(16)
    }
    else -> throw ProtocolException("An address of ${bytes.size} bytes is neither IPv4 nor IPv6.")
}

internal fun splitHostPort(value: String, defaultPort: Int): Pair<String, Int> {
    // An IPv6 literal is bracketed, so the last colon is only a port separator outside brackets.
    val close = value.lastIndexOf(']')
    val colon = value.lastIndexOf(':')
    return if (colon > close && colon >= 0) {
        value.substring(0, colon).trim('[', ']') to value.substring(colon + 1).toInt()
    } else {
        value.trim('[', ']') to defaultPort
    }
}

/** A session id the relay uses to pair two sockets. Must be 16–64 characters of [A-Za-z0-9-]. */
internal fun newRelayUuid(): String {
    val alphabet = "abcdefghijklmnopqrstuvwxyz0123456789"
    return (0 until 32).map { alphabet[Random.nextInt(alphabet.length)] }.joinToString("")
}
