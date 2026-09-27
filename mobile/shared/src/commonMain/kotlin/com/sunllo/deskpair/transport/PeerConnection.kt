package com.sunllo.deskpair.transport

import io.ktor.network.selector.SelectorManager
import io.ktor.network.sockets.InetSocketAddress
import io.ktor.network.sockets.aSocket
import kotlinx.coroutines.Dispatchers

/** How the peer was reached. The user is shown this, because a relayed session behaves differently. */
internal enum class TransportKind {
    /** Straight to an address the user typed. */
    DIRECT_TCP,

    /** The host turned out to be on this network. */
    LAN,

    /** A hole was punched through both NATs. */
    PUNCHED_TCP,

    /** Everything goes through the relay server. Works anywhere; costs latency and someone's bandwidth. */
    RELAY,
}

/** An open, framed-stream-ready connection to a host, however it was reached. */
internal class PeerConnection(
    val stream: ByteStream,
    val kind: TransportKind,
) : AutoCloseable {
    override fun close(): Unit = stream.close()
}

/** How a host was reached, and who vouched for its identity along the way. */
internal class LocatedHost(
    val connection: PeerConnection,
    val verifier: com.sunllo.deskpair.crypto.HostIdentityVerifier,
)

/**
 * Opens connections to hosts: straight to an address, or by nine-digit id through the rendezvous server.
 */
internal class PeerConnector(
    private val selector: SelectorManager = SelectorManager(Dispatchers.Default),
) {

    suspend fun connectDirect(host: String, port: Int): PeerConnection {
        val socket = aSocket(selector).tcp().connect(InetSocketAddress(host, port)) {
            // Interactive input: a frame delayed to fill a segment is a cursor that lags.
            noDelay = true
        }

        return PeerConnection(KtorByteStream(socket), TransportKind.DIRECT_TCP)
    }

    /**
     * Reaches a host by id, preferring a hole punched straight through and settling for a relay.
     *
     * The order is the whole strategy. Detecting the NAT first is what decides whether a punch is worth
     * attempting at all: behind a symmetric NAT the public port the server sees belongs to a mapping the
     * host can never use, so asking for a punch would only cost eight seconds before the relay it was
     * always going to be. Where the NAT is punchable, the request is sent from the port the punch will
     * knock from, because the server reports what it observed and that observation has to be about the
     * right mapping.
     *
     * A failed punch is not an error. It ends in the relay, which is where a forced relay starts.
     */
    suspend fun connectById(
        hostId: String,
        rendezvousServer: String,
        serverPublicKeySpki: ByteArray,
        clientVersion: String,
        nowMillis: Long,
        forceRelay: Boolean = false,
        onProgress: (com.sunllo.deskpair.ConnectProgress) -> Unit = {},
        connType: sunllo.rendezvous.ConnType = sunllo.rendezvous.ConnType.CONN_REMOTE,
    ): LocatedHost {
        val (serverHost, serverPort) = splitHostPort(rendezvousServer, Ports.RENDEZVOUS)
        val client = RendezvousClient(
            serverHost = serverHost,
            serverPort = serverPort,
            serverPublicKeySpki = serverPublicKeySpki,
            selector = selector,
            clientVersion = clientVersion,
            connType = connType,
        )

        var natType = sunllo.rendezvous.NatType.NAT_UNKNOWN
        if (!forceRelay) {
            onProgress(com.sunllo.deskpair.ConnectProgress.MEASURING_NETWORK)
            natType = NatTypeDetector(serverHost, serverPort).detect(nowMillis)
        }

        // Punching is only worth the eight seconds it costs where the NAT maps a local port to one public
        // port whatever the destination. A symmetric NAT gives the host a port that was only ever meant for
        // the rendezvous server, and the knock would arrive nowhere.
        val punchable = !forceRelay && natType == sunllo.rendezvous.NatType.NAT_ASYMMETRIC

        // The port that matters is the one this very request goes out from, not the one the NAT probe used.
        // The server reports the mapping it observes here, and that is what the host is told to expect —
        // and reusing the probe's port would also mean repeating a connection the network still remembers,
        // which a TCP stack refuses outright.
        onProgress(com.sunllo.deskpair.ConnectProgress.LOOKING_UP)
        val (located, localPort) = client.locateFrom(
            hostId = hostId,
            forceRelay = !punchable,
            natType = natType,
            localPort = if (punchable) 0 else -1,
        )

        when (located) {
            is RendezvousResult.Direct -> {
                // The server points at the host: either it is on this network, or both NATs were opened.
                if (punchable) {
                    onProgress(com.sunllo.deskpair.ConnectProgress.PUNCHING)
                    TcpPuncher().connect(localPort, located.host, located.port)?.let {
                        return LocatedHost(it, located.verifier)
                    }
                    onProgress(com.sunllo.deskpair.ConnectProgress.CONNECTING)
                } else {
                    onProgress(com.sunllo.deskpair.ConnectProgress.CONNECTING)
                    val kind = if (located.isLocal) TransportKind.LAN else TransportKind.DIRECT_TCP
                    val socket = aSocket(selector).tcp().connect(
                        InetSocketAddress(located.host, located.port),
                    ) { noDelay = true }
                    return LocatedHost(PeerConnection(KtorByteStream(socket), kind), located.verifier)
                }
            }

            is RendezvousResult.Relayed -> {
                onProgress(com.sunllo.deskpair.ConnectProgress.CONNECTING)
                return LocatedHost(
                    RelayClient(clientId = "", connType = connType).join(located.relayServer, located.uuid, located.hostId, located.ticket),
                    located.verifier,
                )
            }
        }

        // The punch was tried and did not take. Asking again with force_relay is what the server expects.
        val relayed = client.locate(hostId, forceRelay = true)
        val relay = relayed as? RendezvousResult.Relayed
            ?: throw HostUnreachableException(hostId, "the server offered no relay after the punch failed", UnreachableCause.NO_RELAY)

        return LocatedHost(
            RelayClient(clientId = "", connType = connType).join(relay.relayServer, relay.uuid, relay.hostId, relay.ticket),
            relay.verifier,
        )
    }
}
