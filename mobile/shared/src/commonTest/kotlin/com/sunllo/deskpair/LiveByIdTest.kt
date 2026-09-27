package com.sunllo.deskpair

import com.sunllo.deskpair.crypto.EphemeralPinnedKeyStore
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.test.runTest
import kotlinx.coroutines.withContext
import okio.ByteString.Companion.decodeBase64
import kotlin.test.Test
import kotlin.test.assertTrue
import kotlin.time.Duration.Companion.seconds

/**
 * Reaching a host by id, letting the client decide how.
 *
 * This is the path a phone actually takes, and the one with a decision in it: measure the network, ask the
 * rendezvous server, punch a hole if the NAT allows one, fall back to a relay if it does not or if the
 * punch does not land. Any of those outcomes is a pass — what is being asserted is that the session opens
 * and says honestly how it got there, because the choice is made from what the network turns out to be and
 * not from what anyone hoped.
 *
 *   SUNLLO_TEST_ID=<id> SUNLLO_TEST_RENDEZVOUS=<server> SUNLLO_TEST_SERVER_KEY=<base64 spki>
 */
class LiveByIdTest {

    @Test
    fun the_client_reaches_a_host_by_id_and_says_how() {
        val hostId = testEnv("SUNLLO_TEST_ID")?.takeIf { it.isNotEmpty() }
        val server = testEnv("SUNLLO_TEST_RENDEZVOUS")?.takeIf { it.isNotEmpty() }
        val serverKey = testEnv("SUNLLO_TEST_SERVER_KEY")?.takeIf { it.isNotEmpty() }

        if (hostId == null || server == null || serverKey == null) {
            println("SKIPPED: set SUNLLO_TEST_ID, SUNLLO_TEST_RENDEZVOUS and SUNLLO_TEST_SERVER_KEY to run this.")
            return
        }

        val key = requireNotNull(serverKey.decodeBase64()) { "SUNLLO_TEST_SERVER_KEY is not valid base64." }

        runTest(timeout = 180.seconds) {
            withContext(Dispatchers.Default) {
                val session = DeskPair(deviceName = "Kotlin by-id test", platform = "Android", pinnedKeys = EphemeralPinnedKeyStore()).connect(
                    target = Target.ById(
                        id = hostId,
                        rendezvousServer = server,
                        serverPublicKeySpki = key.toByteArray(),
                        // The point of the test: let it choose.
                        forceRelay = false,
                    ),
                    password = testEnv("SUNLLO_TEST_PASSWORD"),
                    sink = CountingSink,
                )

                println("reached ${session.host.hostname} over ${session.transport}")

                // A session that opened but carries nothing is not a session. Video is the first thing a
                // host sends unprompted, so waiting for it proves the transport works in both directions.
                var waited = 0
                while (session.videoFramesReceived == 0 && waited < 200) {
                    kotlinx.coroutines.delay(100)
                    waited++
                }

                val frames = session.videoFramesReceived
                println("state=${session.state.value} frames=$frames failure=${session.failure}")

                // Closing is its own step: a session that carried video and then refused to shut down
                // cleanly is a different fault from one that never carried any.
                runCatching { session.close("test finished") }
                    .onFailure { println("close failed: ${it::class.simpleName}: ${it.message}") }

                assertTrue(
                    frames > 0,
                    "reached the host over ${session.transport} but no video came back, so the path is " +
                        "open in one direction only",
                )
            }
        }
    }
}

private object CountingSink : VideoSink {
    override fun onFrame(data: ByteArray, isKeyFrame: Boolean, width: Int, height: Int, format: VideoFormat): Boolean = true
}
