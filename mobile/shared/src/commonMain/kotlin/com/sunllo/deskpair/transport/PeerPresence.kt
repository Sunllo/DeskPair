package com.sunllo.deskpair.transport

import com.sunllo.deskpair.framing.FramedStream
import com.sunllo.deskpair.store.Targets
import io.ktor.network.selector.SelectorManager
import io.ktor.network.sockets.InetSocketAddress
import io.ktor.network.sockets.aSocket
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.withTimeout
import sunllo.rendezvous.QueryOnline
import sunllo.rendezvous.RendezvousMessage

/** Whether a saved desk can be reached right now. */
public enum class OnlineState { UNKNOWN, ONLINE, OFFLINE }

/**
 * Answers "is this desk reachable" for the lists on the phone, the way the desktop's device list does.
 *
 * Ids are asked of the rendezvous server in one batch -- it keeps a heartbeat per host -- and addresses
 * are probed by opening a socket to the direct-access port, which is what a connection would do and
 * needs no server. A desk that cannot be judged (no server, the server unreachable) stays UNKNOWN rather
 * than being shown as offline: a grey dot is an honest answer, a red one is a claim.
 */
public class PeerPresence(private val selector: SelectorManager) {
    /** Swift does not see Kotlin default arguments, so the no-argument form is a constructor of its own. */
    public constructor() : this(SelectorManager(Dispatchers.Default))

    private companion object {
        /** The rendezvous server answers at most this many ids per query. */
        const val MAX_BATCH = 100
        const val QUERY_TIMEOUT_MS = 6_000L
        const val PROBE_TIMEOUT_MS = 1_500L
    }

    public suspend fun query(targets: List<String>, rendezvousServer: String): Map<String, OnlineState> {
        val distinct = targets.map { Targets.normalise(it) }.filter { it.isNotEmpty() }.distinct()
        val result = HashMap<String, OnlineState>()
        for (target in distinct) {
            result[target] = OnlineState.UNKNOWN
        }

        val ids = distinct.filter { !Targets.isDirect(it) }
        val addresses = distinct.filter { Targets.isDirect(it) }
        coroutineScope {
            val probes = addresses.map { address -> async { address to probe(address) } }
            val server = async { queryServer(ids, rendezvousServer) }
            for ((address, state) in probes.awaitAll()) {
                result[address] = state
            }
            result.putAll(server.await())
        }

        return result
    }

    /** One batch of ids over the rendezvous TCP port; the answer is positional, so order matters. */
    private suspend fun queryServer(ids: List<String>, rendezvousServer: String): Map<String, OnlineState> {
        val result = HashMap<String, OnlineState>()
        if (ids.isEmpty() || rendezvousServer.isEmpty()) {
            return result
        }

        val (host, port) = splitHostPort(rendezvousServer, Ports.RENDEZVOUS)
        for (batch in ids.chunked(MAX_BATCH)) {
            try {
                withTimeout(QUERY_TIMEOUT_MS) {
                    val socket = aSocket(selector).tcp().connect(InetSocketAddress(host, port))
                    try {
                        val stream = FramedStream(KtorByteStream(socket), FramedStream.MAX_CONTROL_FRAME_BYTES)
                        stream.send(RendezvousMessage(query_online = QueryOnline(ids = batch)).encode())
                        val reply = stream.receive()?.let { RendezvousMessage.ADAPTER.decode(it) }
                        val online = reply?.query_online_response?.online ?: return@withTimeout
                        for (i in batch.indices) {
                            if (i < online.size) {
                                result[batch[i]] = if (online[i]) OnlineState.ONLINE else OnlineState.OFFLINE
                            }
                        }
                    } finally {
                        socket.close()
                    }
                }
            } catch (_: Exception) {
                // The server is unreachable; these desks stay unknown rather than being shown as offline.
            }
        }

        return result
    }

    /** A short connect to the host's direct-access port: reachable means the desk is up and listening. */
    private suspend fun probe(target: String): OnlineState {
        val (host, port) = splitHostPort(target, Ports.DIRECT_ACCESS)
        return try {
            withTimeout(PROBE_TIMEOUT_MS) {
                aSocket(selector).tcp().connect(InetSocketAddress(host, port)).close()
            }
            OnlineState.ONLINE
        } catch (_: Exception) {
            OnlineState.OFFLINE
        }
    }
}
