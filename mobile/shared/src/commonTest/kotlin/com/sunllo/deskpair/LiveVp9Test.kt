package com.sunllo.deskpair

import com.sunllo.deskpair.crypto.EphemeralPinnedKeyStore
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.test.runTest
import kotlinx.coroutines.withContext
import okio.ByteString.Companion.decodeBase64
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.time.Duration.Companion.seconds

/**
 * A real host's VP9, read the way iOS reads it.
 *
 * [Vp9ConfigurationTest] checks the header reader against the specification; this checks it against what
 * libvpx actually writes, and that the host says which matrix it converted with. A session that lists VP9 as
 * the only format it decodes leaves the host nothing else to send, so it needs a host that can encode VP9.
 *
 *   SUNLLO_TEST_ID=<id> SUNLLO_TEST_RENDEZVOUS=<server> SUNLLO_TEST_SERVER_KEY=<base64 spki>
 *   SUNLLO_TEST_PASSWORD=...
 */
class LiveVp9Test {

    @Test
    fun a_hosts_vp9_keyframe_describes_its_stream() {
        val hostId = testEnv("SUNLLO_TEST_ID")?.takeIf { it.isNotEmpty() }
        val server = testEnv("SUNLLO_TEST_RENDEZVOUS")?.takeIf { it.isNotEmpty() }
        val serverKey = testEnv("SUNLLO_TEST_SERVER_KEY")?.takeIf { it.isNotEmpty() }

        if (hostId == null || server == null || serverKey == null) {
            println("SKIPPED: set SUNLLO_TEST_ID, SUNLLO_TEST_RENDEZVOUS and SUNLLO_TEST_SERVER_KEY to run this.")
            return
        }

        val key = requireNotNull(serverKey.decodeBase64()) { "SUNLLO_TEST_SERVER_KEY is not valid base64." }
        val sink = FirstVp9Keyframe()

        runTest(timeout = 120.seconds) {
            withContext(Dispatchers.Default) {
                val session = DeskPair(deviceName = "Kotlin VP9 test", platform = "Android", pinnedKeys = EphemeralPinnedKeyStore()).connect(
                    target = Target.ById(id = hostId, rendezvousServer = server, serverPublicKeySpki = key.toByteArray(), forceRelay = false),
                    password = testEnv("SUNLLO_TEST_PASSWORD"),
                    sink = sink,
                )

                var waited = 0
                while (sink.keyframe == null && waited < 300) {
                    kotlinx.coroutines.delay(100)
                    waited++
                }
                runCatching { session.close("test finished") }
            }
        }

        val (frame, width, height) = assertNotNull(sink.keyframe, "no VP9 keyframe arrived")
        println("VP9 keyframe ${width}x$height, ${frame.size} bytes, begins " + frame.take(16).joinToString(" ") { (it.toInt() and 0xFF).toString(16).padStart(2, '0') })

        val config = assertNotNull(Vp9Configuration.parse(frame), "the keyframe's header did not read")
        assertEquals(width, config.width, "the header's width is the frame's")
        assertEquals(height, config.height, "the header's height is the frame's")
        assertEquals(0, config.profile)
        assertEquals(8, config.bitDepth)
        assertEquals(0, config.chromaSubsampling, "4:2:0")
        assertEquals(false, config.fullRange, "studio range, as PixelConversion writes it")
        assertEquals(5, config.matrixCoefficients, "the host converts with BT.601 and says so")
    }

    private class FirstVp9Keyframe : VideoSink {
        var keyframe: Triple<ByteArray, Int, Int>? = null

        override val formats: Set<VideoFormat> = setOf(VideoFormat.VP9)

        override fun onFrame(data: ByteArray, isKeyFrame: Boolean, width: Int, height: Int, format: VideoFormat): Boolean {
            if (keyframe == null && isKeyFrame && format == VideoFormat.VP9) {
                keyframe = Triple(data, width, height)
            }
            return true
        }
    }
}
