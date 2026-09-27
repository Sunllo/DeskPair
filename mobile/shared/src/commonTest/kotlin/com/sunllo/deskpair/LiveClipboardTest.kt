package com.sunllo.deskpair

import com.sunllo.deskpair.crypto.EphemeralPinnedKeyStore
import com.sunllo.deskpair.clipboard.ClipboardBridge
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.test.runTest
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.time.Duration.Companion.seconds

/**
 * The host's clipboard, arriving unasked.
 *
 * A host subscribes a new session to its clipboard service and sends what it is holding straight away — but
 * only if the session asked for it at login, which is the part worth testing through the public API rather
 * than around it. A client that supplies nowhere to put the text declines the subscription; one that does
 * must ask, and forgetting to looks from the outside exactly like a feature that does not work.
 *
 *   SUNLLO_TEST_HOST=… SUNLLO_TEST_PASSWORD=… SUNLLO_TEST_EXPECT_CLIP="marker" ./gradlew :shared:jvmTest
 */
class LiveClipboardTest {

    @Test
    fun the_host_offers_its_clipboard_to_a_session_that_asked_for_it() {
        val host = testEnv("SUNLLO_TEST_HOST")?.takeIf { it.isNotEmpty() }
        val expected = testEnv("SUNLLO_TEST_EXPECT_CLIP")?.takeIf { it.isNotEmpty() }

        if (host == null || expected == null) {
            println("SKIPPED: set SUNLLO_TEST_HOST and SUNLLO_TEST_EXPECT_CLIP to the host's clipboard text.")
            return
        }

        val at = host.lastIndexOf(':')
        val target = Target.ByAddress(host.substring(0, at), host.substring(at + 1).toInt())

        runTest(timeout = 120.seconds) {
            withContext(Dispatchers.Default) {
                // Waits for the host's current content rather than taking the first thing that turns up.
                // A host whose clipboard service was idle can replay what it queued while nobody was
                // connected, and the newest of those arrives last; what the client owes the user is that
                // the host's clipboard as it stands reaches them, not that it is the only thing sent.
                val arrived = CompletableDeferred<String>()
                val session = DeskPair(deviceName = "Kotlin clipboard test", platform = "Android", pinnedKeys = EphemeralPinnedKeyStore()).connect(
                    target = target,
                    password = testEnv("SUNLLO_TEST_PASSWORD"),
                    sink = DiscardingSink,
                    clipboard = object : ClipboardBridge {
                        override fun onRemoteText(text: String) {
                            if (text == expected) {
                                arrived.complete(text)
                            } else {
                                println("ignoring an older clipboard the host still had queued: $text")
                            }
                        }
                    },
                )

                val text = withTimeoutOrNull(25.seconds) { arrived.await() }
                session.close("test finished")

                assertNotNull(
                    text,
                    "The host sent no clipboard. It only subscribes a session that asked at login, so the " +
                        "first thing to check is what ClientCapabilities put in disable_clipboard.",
                )
                assertEquals(expected, text)
            }
        }
    }
}

/** This test has no screen; it still has to accept frames or the host stops sending. */
private object DiscardingSink : VideoSink {
    override fun onFrame(data: ByteArray, isKeyFrame: Boolean, width: Int, height: Int, format: VideoFormat): Boolean = true
}
