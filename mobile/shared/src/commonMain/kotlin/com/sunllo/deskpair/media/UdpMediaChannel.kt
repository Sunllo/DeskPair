package com.sunllo.deskpair.media

import com.sunllo.deskpair.crypto.SessionKeys
import io.ktor.network.selector.SelectorManager
import io.ktor.network.sockets.BoundDatagramSocket
import io.ktor.network.sockets.Datagram
import io.ktor.network.sockets.InetSocketAddress
import io.ktor.network.sockets.aSocket
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.io.readByteArray
import kotlinx.io.Buffer

/**
 * The relay's only vocabulary: a 20-byte plaintext pairing message.
 *
 * Two endpoints presenting the same token become a pair, and everything else between them is forwarded
 * untouched. Media datagrams start with a different magic byte, so the relay never mistakes one for the
 * other and never has to understand what it is carrying — which is the point, since it cannot decrypt it.
 */
internal object RelayDatagram {
    const val MAGIC: Byte = 0x52 // 'R'
    const val SIZE = 20
    const val TOKEN_BYTES = 16

    const val BIND: Byte = 1
    const val BIND_ACK: Byte = 2
    const val REJECT: Byte = 3

    /** A bind that carries the rendezvous server's relay ticket after the token; protocol 2. */
    const val BIND_TICKET: Byte = 4

    fun write(type: Byte, token: ByteArray): ByteArray {
        require(token.size == TOKEN_BYTES) { "A relay token is $TOKEN_BYTES bytes." }
        val out = ByteArray(SIZE)
        out[0] = MAGIC
        out[1] = type
        token.copyInto(out, 4)
        return out
    }

    /** The fixed part with the ticket's length in what protocol 1 left as padding, then the ticket. */
    fun writeBindTicket(token: ByteArray, ticket: ByteArray): ByteArray {
        require(token.size == TOKEN_BYTES) { "A relay token is $TOKEN_BYTES bytes." }
        require(ticket.isNotEmpty() && ticket.size <= 512) { "A relay ticket is 1 to 512 bytes." }
        val out = ByteArray(SIZE + ticket.size)
        out[0] = MAGIC
        out[1] = BIND_TICKET
        out[2] = (ticket.size and 0xff).toByte()
        out[3] = (ticket.size ushr 8).toByte()
        token.copyInto(out, 4)
        ticket.copyInto(out, SIZE)
        return out
    }

    fun typeOf(datagram: ByteArray, length: Int): Byte? {
        if (length != SIZE || datagram[0] != MAGIC) {
            return null
        }
        val type = datagram[1]
        return if (type in BIND..REJECT) type else null
    }

    fun tokenOf(datagram: ByteArray): ByteArray = datagram.copyOfRange(4, 4 + TOKEN_BYTES)
}

/** One address the media channel could use, and how good it is expected to be. */
internal data class MediaPath(val kind: Int, val host: String, val port: Int) {
    companion object {
        const val LOCAL = 0
        const val REFLEXIVE = 1
        const val RELAY = 2

        /** Local beats reflexive beats relay: fewer hops, less to go wrong, nobody else's bandwidth. */
        fun priorityOf(kind: Int): Int = when (kind) {
            LOCAL -> 300
            REFLEXIVE -> 200
            else -> 100
        }

        fun kindFromPriority(priority: Int): Int = when {
            priority >= 300 -> LOCAL
            priority >= 200 -> REFLEXIVE
            else -> RELAY
        }
    }
}

/**
 * Video over UDP, receive side.
 *
 * TCP is wrong for video and right for everything else, which is why both are here at once: input,
 * clipboard and control stay on the encrypted TCP session, and only the picture moves to datagrams. The
 * reason is head-of-line blocking — TCP will hold a frame that has already arrived behind the
 * retransmission of one that is no longer worth drawing, and on a phone's link that is the difference
 * between a session that feels immediate and one that feels broken.
 *
 * What TCP gave away has to be rebuilt. A packet sequence in the clear doubles as the encryption nonce and
 * the replay key; forward error correction repairs ordinary loss without asking; the assembler decides
 * when a frame is not coming; and a feedback report every fifty milliseconds tells the sender what the
 * path is really doing, since it can no longer infer it from acknowledgements it is not getting.
 *
 * This is the controller's half and nothing more. The estimator that turns feedback into a bitrate, the
 * pacer that spreads packets out, the planner that chooses how much parity to send — all of that runs on
 * the sending side, and none of it belongs on a phone.
 */
internal class UdpMediaChannel(
    private val channelId: Int,
    keys: SessionKeys,
    private val nowMillis: () -> Long,
    private val onFrame: (AssembledFrame) -> Unit,
    private val onPathChosen: (MediaPath) -> Unit,
    private val onClosed: (String) -> Unit,
    private val selector: SelectorManager = SelectorManager(Dispatchers.Default),
) {

    private companion object {
        const val FEEDBACK_INTERVAL_MS = 50L
        const val KEEPALIVE_MS = 2000L
        const val DEAD_AFTER_MS = 4000L
        const val BIND_INTERVAL_MS = 100L
        const val MAX_DATAGRAM = 1500
    }

    private val cipher = MediaCipher(keys.mediaTxKey, keys.mediaTxIvPrefix, keys.mediaRxKey, keys.mediaRxIvPrefix)
    private val link = LinkStats(nowMillis)
    private val streams = StreamAssemblers(nowMillis)

    /**
     * The app is leaving the screen. Packets lost while the OS winds it down are not the network's doing,
     * and reporting them as loss had the host cut the bitrate to a fifth -- the keyframe asked for on the
     * way back was encoded at that rate and a still screen kept it. While set, feedback reports no loss.
     */
    public var backgrounded: Boolean = false
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)

    private var socket: BoundDatagramSocket? = null
    private var jobs = mutableListOf<Job>()

    /** Remote addresses worth probing, with the priority each was offered at. */
    private val remotes = LinkedHashMap<String, Pair<Int, Int>>()

    /**
     * Guards [remotes], which three coroutines touch.
     *
     * probeLoop reads it, receiveLoop writes to it when it learns a peer-reflexive candidate, and the
     * session writes to it when the host offers its own -- and on Darwin those run on a genuinely
     * multi-threaded dispatcher. Iterating while another thread inserted threw
     * ConcurrentModificationException out of a launch, which Kotlin/Native answers by killing the process.
     *
     * The window looked small and was not: the insert happens the moment a new candidate is learned, which
     * is during connection setup, which is exactly when probeLoop is running every 300 ms. The app died on
     * connecting, every time, and the picture never arrived because the channel it was arriving on was
     * gone. FrameAssembler took the same lesson from the same kind of crash.
     */
    private val remotesGuard = Mutex()

    private var chosen: MediaPath? = null
    private var lastHeardMs = 0L
    private var relay: MediaPath? = null
    private var relayToken: ByteArray? = null
    private var relayTicket: ByteArray? = null
    private var relayPaired = false

    /** Set once a frame was given up, so the channel can ask the host for a keyframe over TCP. */
    var wantsKeyframe: Boolean = false
        private set

    val path: MediaPath? get() = chosen

    /** The port this channel listens on, which is what goes into our own candidate list. */
    var localPort: Int = 0
        private set

    suspend fun open() {
        val bound = aSocket(selector).udp().bind(InetSocketAddress("0.0.0.0", 0))
        socket = bound
        localPort = (bound.localAddress as InetSocketAddress).port
        lastHeardMs = nowMillis()

        jobs += scope.launch { receiveLoop(bound) }
        jobs += scope.launch { probeLoop() }
        jobs += scope.launch { feedbackLoop() }
    }

    /** Adds a candidate the host offered. Peer-reflexive ones are learned from Bind probes instead. */
    suspend fun addRemote(kind: Int, host: String, port: Int) {
        remotesGuard.withLock { remotes["$host:$port"] = kind to MediaPath.priorityOf(kind) }
    }

    fun useRelay(host: String, port: Int, token: ByteArray, ticket: ByteArray? = null) {
        relay = MediaPath(MediaPath.RELAY, host, port)
        relayToken = token
        relayTicket = ticket
    }

    /** The display the session is now watching; see [StreamAssemblers.expect]. */
    suspend fun expectStream(stream: Int) {
        streams.expect(stream)
    }

    suspend fun close(reason: String) {
        jobs.forEach { it.cancel() }
        jobs.clear()
        runCatching { socket?.close() }
        scope.cancel()
        onClosed(reason)
    }

    private suspend fun receiveLoop(bound: BoundDatagramSocket) {
        while (scope.isActive) {
            val datagram = runCatching { bound.receive() }.getOrNull() ?: return
            val bytes = datagram.packet.readByteArray()
            val from = datagram.address as? InetSocketAddress ?: continue
            handle(bytes, from)
        }
    }

    private suspend fun handle(bytes: ByteArray, from: InetSocketAddress) {
        // The relay speaks first and speaks plaintext. Its pairing reply is the only unencrypted thing
        // this channel accepts, and it carries a token rather than any content.
        RelayDatagram.typeOf(bytes, bytes.size)?.let { type ->
            val token = relayToken ?: return
            if (RelayDatagram.tokenOf(bytes).contentEquals(token) && type == RelayDatagram.BIND_ACK) {
                relayPaired = true
            }
            return
        }

        val opened = cipher.open(bytes) ?: return
        lastHeardMs = nowMillis()
        link.notePacket(opened.header.packetSeq, bytes.size)

        when (opened.header.type) {
            MediaPacketType.VIDEO -> {
                streams.accept(opened.header, opened.plaintext)
                drainFrames()
            }

            MediaPacketType.BIND -> answerBind(opened.plaintext, from)
            MediaPacketType.BIND_ACK -> acceptBindAck(opened.plaintext, from)
            MediaPacketType.PING -> send(MediaPacketType.PONG, opened.plaintext, from)
            MediaPacketType.CLOSE -> close("the host closed the media channel")
            else -> Unit
        }
    }

    private suspend fun drainFrames() {
        while (true) {
            val frame = streams.poll() ?: break
            onFrame(frame)
        }

        // Asked for over TCP rather than here: a keyframe request that travels on the channel that just
        // lost a frame is the least likely thing to arrive.
        wantsKeyframe = streams.referenceBroken()
    }

    private suspend fun answerBind(payload: ByteArray, from: InetSocketAddress) {
        if (payload.size < 8 || payload.intLe(0) != channelId) {
            return
        }

        // Reply to wherever it came from, and remember that address: a NAT may have rewritten it, and the
        // rewritten one is the only address that actually works.
        val priority = payload.intLe(4)
        val key = "${from.hostname}:${from.port}"
        // One critical section, not a check and then an act: two of these can arrive at once, and
        // deciding outside the lock is how a fix for a race grows a smaller race of its own.
        remotesGuard.withLock {
            remotes.getOrPut(key) { MediaPath.kindFromPriority(priority) to priority }
        }

        val ack = ByteArray(8)
        ack.putIntLe(0, channelId)
        ack.putIntLe(4, priority)
        send(MediaPacketType.BIND_ACK, ack, from)
    }

    private fun acceptBindAck(payload: ByteArray, from: InetSocketAddress) {
        if (payload.size < 8 || payload.intLe(0) != channelId) {
            return
        }

        val priority = payload.intLe(4)
        val candidate = MediaPath(MediaPath.kindFromPriority(priority), from.hostname, from.port)
        val current = chosen

        // A better path replaces a working one, once. After that, switching costs more than it gains.
        if (current == null || MediaPath.priorityOf(candidate.kind) > MediaPath.priorityOf(current.kind)) {
            chosen = candidate
            onPathChosen(candidate)
        }
    }

    private suspend fun probeLoop() {
        while (scope.isActive) {
            val token = relayToken
            val relayAddress = relay
            if (token != null && !relayPaired && relayAddress != null) {
                val ticket = relayTicket
                val bind = if (ticket != null) RelayDatagram.writeBindTicket(token, ticket) else RelayDatagram.write(RelayDatagram.BIND, token)
                sendRaw(bind, relayAddress.host, relayAddress.port)
            }

            // The snapshot is taken under the lock; the sends happen outside it, because sending is the
            // slow part and holding a lock across it would stall whoever is learning a candidate.
            for ((address, info) in remotesGuard.withLock { remotes.toList() }) {
                val (host, port) = splitAddress(address) ?: continue
                val bind = ByteArray(8)
                bind.putIntLe(0, channelId)
                bind.putIntLe(4, info.second)
                send(MediaPacketType.BIND, bind, InetSocketAddress(host, port))
            }

            // Once a path is settled the probes become keepalives, because a NAT mapping that goes quiet
            // is a NAT mapping that closes.
            delay(if (chosen == null) BIND_INTERVAL_MS else KEEPALIVE_MS)

            if (nowMillis() - lastHeardMs > DEAD_AFTER_MS) {
                close("nothing arrived on the media channel for ${DEAD_AFTER_MS / 1000} seconds")
                return
            }
        }
    }

    private suspend fun feedbackLoop() {
        while (scope.isActive) {
            delay(FEEDBACK_INTERVAL_MS)
            val target = chosen ?: continue

            streams.tick()
            drainFrames()

            // One report per stream. Datagram frames are acknowledged by these reports alone, so each must
            // name its own stream: a report that said stream 0 while display 1 was on screen left every frame
            // of display 1 unacknowledged, and the host stopped sending it once its in-flight limit filled.
            // The link's fields go in every report but count once: all but the first say so.
            val to = InetSocketAddress(target.host, target.port)
            val loss = if (backgrounded) 0 else link.lossPermille
            val clock = (nowMillis() * 1000).toULong()
            val live = streams.live()
            if (live.isEmpty()) {
                report(MediaFeedback(0, loss, 0u, 0u, 0uL, 0u, link.receivedBytes, clock, 0u, 0u), to)
                continue
            }

            live.forEachIndexed { index, stream ->
                report(
                    MediaFeedback(
                        stream = stream.stream,
                        lossPermille = loss,
                        highestDecodableFrameSeq = stream.highestDecodable,
                        lastFrameSeqReceived = stream.lastFrameSeqReceived,
                        echoPacketSeq = 0uL,
                        echoDelayMicros = 0u,
                        receivedBytes = link.receivedBytes,
                        receiverClockMicros = clock,
                        framesGivenUp = stream.framesGivenUp,
                        shardsRecovered = stream.shardsRecovered,
                        flags = if (index == 0) MediaFeedback.NO_FLAGS else MediaFeedback.LINK_FIELDS_IGNORED,
                    ),
                    to,
                )
            }
        }
    }

    private suspend fun report(feedback: MediaFeedback, to: InetSocketAddress) {
        val payload = ByteArray(MediaPacket.FEEDBACK_BYTES)
        feedback.write(payload)
        send(MediaPacketType.FEEDBACK, payload, to)
    }

    private suspend fun send(type: MediaPacketType, payload: ByteArray, to: InetSocketAddress) {
        sendRaw(cipher.seal(type, MediaPacketFlags.NONE, payload), to.hostname, to.port)
    }

    private suspend fun sendRaw(bytes: ByteArray, host: String, port: Int) {
        val bound = socket ?: return
        val buffer = Buffer().apply { write(bytes) }
        runCatching { bound.send(Datagram(buffer, InetSocketAddress(host, port))) }
    }

    private fun splitAddress(value: String): Pair<String, Int>? {
        val at = value.lastIndexOf(':')
        if (at <= 0) {
            return null
        }
        val port = value.substring(at + 1).toIntOrNull() ?: return null
        return value.substring(0, at) to port
    }
}
