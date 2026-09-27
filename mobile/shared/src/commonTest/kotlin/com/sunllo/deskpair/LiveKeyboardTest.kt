package com.sunllo.deskpair

import com.sunllo.deskpair.crypto.EphemeralPinnedKeyStore
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.test.runTest
import kotlinx.coroutines.withContext
import kotlin.test.Test
import kotlin.time.Duration.Companion.seconds

/**
 * The keyboard, through the public API, against a real host.
 *
 * The unit tests prove the key table maps every value the wire format has. They cannot prove a keystroke
 * arrives, because that depends on the host injecting it into whatever has focus — which is a property of
 * the machine, not of this code. So this test only sends, and the script that runs it asserts what happened
 * on the other side.
 *
 * Pick an assertion with no dialog in it. Typing into an editor and saving with Cmd+S looks binary and is
 * not: Cmd+Q on a modified document raises a save sheet and the app stays running, so the check reports
 * "the keystroke never arrived" when what happened is that it did. Typing, then selecting all and copying,
 * and then reading the Mac's pasteboard with pbpaste, has no such trap.
 *
 *   SUNLLO_TEST_HOST=… SUNLLO_TEST_PASSWORD=… SUNLLO_TEST_TYPE="hello"
 *   SUNLLO_TEST_SHORTCUT=meta+a,meta+c SUNLLO_TEST_DELAY=12 ./gradlew :shared:jvmTest
 */
class LiveKeyboardTest {

    @Test
    fun the_client_sends_what_the_key_bar_would_send() {
        val host = testEnv("SUNLLO_TEST_HOST")?.takeIf { it.isNotEmpty() }
        val shortcuts = testEnv("SUNLLO_TEST_SHORTCUT")?.takeIf { it.isNotEmpty() }

        if (host == null || shortcuts == null) {
            println("SKIPPED: set SUNLLO_TEST_HOST and SUNLLO_TEST_SHORTCUT to drive a live host's keyboard.")
            return
        }

        val at = host.lastIndexOf(':')
        val target = Target.ByAddress(host.substring(0, at), host.substring(at + 1).toInt())

        // The host raises its connection-manager window when the session lands, and it takes the keystrokes.
        // The script needs that long to put its own target back in front.
        val settle = testEnv("SUNLLO_TEST_DELAY")?.toIntOrNull() ?: 12

        runTest(timeout = 120.seconds) {
            withContext(Dispatchers.Default) {
                val session = DeskPair(deviceName = "Kotlin keyboard test", platform = "Android", pinnedKeys = EphemeralPinnedKeyStore()).connect(
                    target = target,
                    password = testEnv("SUNLLO_TEST_PASSWORD"),
                    sink = DiscardingVideoSink,
                )

                println("connected to ${session.host.hostname}; waiting ${settle}s for the script to take focus")
                delay(settle.seconds)

                // Text first, then the shortcuts that act on it: select-all and copy only mean something
                // once there is something to select.
                testEnv("SUNLLO_TEST_TYPE")?.takeIf { it.isNotEmpty() }?.let {
                    session.sendText(it)
                    println("sent text '$it'")
                    delay(2.seconds)
                }

                for (shortcut in shortcuts.split(",")) {
                    val parts = shortcut.trim().split("+")
                    val modifiers = parts.dropLast(1).mapNotNull {
                        when (it.lowercase()) {
                            "ctrl", "control" -> RemoteModifier.CONTROL
                            "alt" -> RemoteModifier.ALT
                            "shift" -> RemoteModifier.SHIFT
                            "meta", "cmd" -> RemoteModifier.META
                            else -> null
                        }
                    }.toSet()

                    session.sendShortcut(parts.last().first(), modifiers)
                    println("sent ${shortcut.trim()}")
                    delay(1.seconds)
                }

                // Give the host time to inject before the connection goes away underneath it.
                delay(3.seconds)
                session.close("test finished")
            }
        }
    }
}

/** The keyboard test has no screen; it still has to accept frames or the host stops sending. */
private object DiscardingVideoSink : VideoSink {
    override fun onFrame(data: ByteArray, isKeyFrame: Boolean, width: Int, height: Int, format: VideoFormat): Boolean = true
}
