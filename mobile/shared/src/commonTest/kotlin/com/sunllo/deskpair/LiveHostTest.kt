package com.sunllo.deskpair

import com.sunllo.deskpair.crypto.EphemeralPinnedKeyStore
import com.sunllo.deskpair.crypto.TofuIdentityVerifier
import com.sunllo.deskpair.session.ClientCapabilities
import com.sunllo.deskpair.session.ControllerIdentity
import com.sunllo.deskpair.session.ControllerSession
import com.sunllo.deskpair.transport.PeerConnector
import com.sunllo.deskpair.transport.Ports
import com.sunllo.deskpair.transport.RelayClient
import com.sunllo.deskpair.transport.RendezvousClient
import com.sunllo.deskpair.transport.RendezvousResult
import com.sunllo.deskpair.transport.splitHostPort
import okio.ByteString.Companion.decodeBase64
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.test.runTest
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeout
import sunllo.messages.Message
import kotlin.test.Test
import kotlin.test.assertTrue
import kotlin.time.Duration.Companion.seconds

/**
 * The Kotlin client against a real DeskPair host.
 *
 * The conformance vectors prove the two implementations agree about arithmetic. This proves they agree about
 * a conversation: a host that has never heard of this client accepts its hello, signs a transcript it
 * verifies, and hands it a video frame.
 *
 * It needs a host to talk to, so it is skipped rather than failed when there is none — but it says so out
 * loud, because a skipped test that reports as passed is indistinguishable from a working one.
 *
 *   SUNLLO_TEST_HOST=127.0.0.1:21118 SUNLLO_TEST_PASSWORD=xxxxxx ./gradlew :shared:jvmTest
 */
class LiveHostTest {

    private val target: Pair<String, Int>? = testEnv("SUNLLO_TEST_HOST")?.takeIf { it.isNotEmpty() }?.let {
        val at = it.lastIndexOf(':')
        if (at < 0) null else it.substring(0, at) to it.substring(at + 1).toInt()
    }

    private val password: String? = testEnv("SUNLLO_TEST_PASSWORD")

    @Test
    fun the_client_logs_in_and_receives_a_video_frame() {
        val (host, port) = target ?: run {
            println("SKIPPED: set SUNLLO_TEST_HOST=host:port and SUNLLO_TEST_PASSWORD to run this against a live host.")
            return
        }

        // runTest gives a common entry point; the work itself is real network IO on a real dispatcher.
        runTest(timeout = 90.seconds) {
          withContext(Dispatchers.Default) {
            PeerConnector().connectDirect(host, port).use { connection ->
                val session = ControllerSession(
                    connection = connection,
                    identity = ControllerIdentity(
                        id = "kmp-test",
                        name = "Kotlin conformance client",
                        // The desktop already knows these platform names and draws an icon for them.
                        platform = "Android",
                        version = "0.1.0",
                    ),
                    capabilities = ClientCapabilities(),
                )

                session.handshake(TofuIdentityVerifier(EphemeralPinnedKeyStore(), "$host:$port"))
                val challenge = requireNotNull(session.challenge)
                println("challenge: salt=${challenge.salt.size}B challenge=${challenge.challenge.size}B approve=${challenge.approve_mode}")

                val peer = session.login(password) { println("waiting for someone at the host to approve...") }
                println("logged in to ${peer.hostname} (${peer.platform}, ${peer.username}), ${peer.displays.size} display(s)")

                assertTrue(peer.displays.isNotEmpty(), "A host with no displays cannot be viewed")

                // Everything above is the handshake. This is the part that proves the session actually runs:
                // the host only sends video to a subscriber it has authorised.
                var videoFrames = 0
                var bytes = 0L
                withTimeout(20.seconds) {
                    while (videoFrames < 3) {
                        val message = session.receive() ?: break
                        when {
                            message.video_frame != null -> {
                                val frame = message.video_frame!!
                                videoFrames++
                                bytes += frame.frame?.data_?.size?.toLong() ?: 0L
                                frame.frame?.let { session.acknowledgeVideo(frame.display, it.seq) }
                            }
                            // Not echoing this degrades the host's own bitrate control, so do it from the start.
                            message.test_delay != null -> session.echo(message.test_delay!!)
                            else -> Unit
                        }
                    }
                }

                println("received $videoFrames video frame(s), $bytes byte(s) of encoded video")
                assertTrue(videoFrames >= 3, "Expected video from an authorised session, got $videoFrames frames")
            }
          }
        }
    }
}

/** Kept out of the assertion path; it exists so a failure message can say what arrived instead. */
internal fun Message.kind(): String = when {
    video_frame != null -> "VideoFrame"
    misc != null -> "Misc"
    test_delay != null -> "TestDelay"
    cursor_data != null -> "CursorData"
    cursor_position != null -> "CursorPosition"
    else -> "other"
}

/**
 * The same session, reached the way a phone on a mobile network has to reach it: by nine-digit id, through
 * the rendezvous server, over a relay.
 *
 * This exercises what the direct test cannot — the rendezvous exchange, the server's signature over the
 * host's identity, and the relay handshake — and it is the path that will actually be used in the field,
 * because a phone is almost never reachable directly.
 *
 *   SUNLLO_TEST_ID=<id> SUNLLO_TEST_RENDEZVOUS=<server> SUNLLO_TEST_SERVER_KEY=<base64 spki>
 */
class LiveRelayTest {

    @Test
    fun the_client_reaches_a_host_by_id_over_a_relay() {
        val hostId = testEnv("SUNLLO_TEST_ID")?.takeIf { it.isNotEmpty() }
        val server = testEnv("SUNLLO_TEST_RENDEZVOUS")?.takeIf { it.isNotEmpty() }
        val serverKey = testEnv("SUNLLO_TEST_SERVER_KEY")?.takeIf { it.isNotEmpty() }
        val password = testEnv("SUNLLO_TEST_PASSWORD")

        if (hostId == null || server == null || serverKey == null) {
            println("SKIPPED: set SUNLLO_TEST_ID, SUNLLO_TEST_RENDEZVOUS and SUNLLO_TEST_SERVER_KEY to run this.")
            return
        }

        runTest(timeout = 120.seconds) {
            withContext(Dispatchers.Default) {
                val (serverHost, serverPort) = splitHostPort(server, Ports.RENDEZVOUS)
                val located = RendezvousClient(
                    serverHost = serverHost,
                    serverPort = serverPort,
                    serverPublicKeySpki = serverKey.decodeBase64ToBytes(),
                    clientVersion = "0.1.0",
                ).locate(hostId, forceRelay = true)

                println("rendezvous says: $located")

                val connection = when (located) {
                    is RendezvousResult.Relayed ->
                        RelayClient(clientId = "kmp-test").join(located.relayServer, located.uuid, located.hostId)
                    is RendezvousResult.Direct ->
                        PeerConnector().connectDirect(located.host, located.port)
                }

                connection.use {
                    val session = ControllerSession(
                        connection = it,
                        identity = ControllerIdentity("kmp-test", "Kotlin conformance client", "Android", "0.1.0"),
                    )

                    // The verifier came from the rendezvous server's signature, so this connection is bound
                    // to the identity that server vouched for and not merely to whatever answered.
                    session.handshake(located.verifier)
                    val peer = session.login(password)
                    println("logged in over ${it.kind} to ${peer.hostname} (${peer.platform})")

                    var frames = 0
                    withTimeout(30.seconds) {
                        while (frames < 3) {
                            val message = session.receive() ?: break
                            message.video_frame?.let { frame ->
                                frames++
                                frame.frame?.let { f -> session.acknowledgeVideo(frame.display, f.seq) }
                            }
                            message.test_delay?.let { d -> session.echo(d) }
                        }
                    }

                    println("received $frames video frame(s) over ${it.kind}")
                    assertTrue(frames >= 3, "Expected video over the relay, got $frames frames")
                }
            }
        }
    }
}

/** The rendezvous server's public key is configured as base64 SPKI, the same way the desktop stores it. */
private fun String.decodeBase64ToBytes(): ByteArray =
    requireNotNull(decodeBase64()) { "SUNLLO_TEST_SERVER_KEY is not valid base64." }.toByteArray()

/**
 * Whether the host moves video onto UDP, and whether this client follows it there.
 *
 * The session starts on TCP and the host offers a datagram channel once it is up, so the answer takes a
 * few seconds to arrive. Both outcomes are informative: a path means the channel negotiated and packets
 * are being decrypted and assembled; no path means the offer never came or never completed, and the
 * picture is still on TCP, which is a working session rather than a broken one.
 *
 *   SUNLLO_TEST_HOST=127.0.0.1:21118 SUNLLO_TEST_PASSWORD=xxxxxx ./gradlew :shared:jvmTest
 */
class LiveUdpMediaTest {

    @Test
    fun the_host_offers_a_udp_channel_and_the_picture_moves_to_it() {
        val host = testEnv("SUNLLO_TEST_HOST")?.takeIf { it.isNotEmpty() } ?: run {
            println("SKIPPED: set SUNLLO_TEST_HOST=host:port to see whether video moves to UDP.")
            return
        }

        val at = host.lastIndexOf(':')

        runTest(timeout = 120.seconds) {
            withContext(Dispatchers.Default) {
                val session = DeskPair(deviceName = "Kotlin UDP test", platform = "Android", pinnedKeys = EphemeralPinnedKeyStore()).connect(
                    target = Target.ByAddress(host.substring(0, at), host.substring(at + 1).toInt()),
                    password = testEnv("SUNLLO_TEST_PASSWORD"),
                    sink = object : VideoSink {
                        override fun onFrame(data: ByteArray, isKeyFrame: Boolean, width: Int, height: Int, format: VideoFormat) = true
                    },
                )

                var waited = 0
                while (session.mediaPath == null && waited < 150) {
                    kotlinx.coroutines.delay(100)
                    waited++
                }

                val path = session.mediaPath
                val beforeSwitch = session.videoFramesReceived

                // The host stops sending video on the TCP session once the datagram path is up, so frames
                // that arrive after this point came over UDP — decrypted, reassembled and delivered by the
                // code under test rather than by the stream that was already working.
                kotlinx.coroutines.delay(3000)
                val afterSwitch = session.videoFramesReceived
                println("media path: ${path ?: "none, video is still on TCP"}; frames $beforeSwitch -> $afterSwitch")
                session.close("test finished")

                assertTrue(afterSwitch > 0, "no video arrived at all, over either transport")
                if (path != null) {
                    assertTrue(
                        afterSwitch > beforeSwitch,
                        "a UDP path was chosen but no frames followed it, so the picture stopped rather " +
                            "than moved",
                    )
                }
            }
        }
    }
}
