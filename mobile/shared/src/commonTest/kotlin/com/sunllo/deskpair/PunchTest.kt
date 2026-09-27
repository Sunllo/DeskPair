package com.sunllo.deskpair

import com.sunllo.deskpair.transport.TcpPuncher
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.test.runTest
import kotlinx.coroutines.withContext
import kotlin.test.Test
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlin.time.Duration.Companion.seconds
import kotlin.time.TimeSource

/**
 * The socket a punch is built on, and what happens when the punch does not land.
 *
 * A punch that nobody answers has to give up in time for the relay behind it to still be worth trying.
 * That much needs no peer and no network. Whether a local port can actually be shared is a property of the
 * platform rather than of this code, so it is measured against a real server in LiveNatTest instead.
 */
class PunchTest {

    @Test
    fun a_punch_nobody_answers_gives_up_rather_than_hanging() = runTest(timeout = 60.seconds) {
        withContext(Dispatchers.Default) {
            val puncher = TcpPuncher()
            val started = TimeSource.Monotonic.markNow()

            // Nothing listens here. The relay is the fallback and it is only worth having if the punch
            // stops in seconds; a punch that hangs is worse than one that fails.
            val result = puncher.connect(0, "127.0.0.1", ECHOLESS_PORT, timeout = 3.seconds)
            val elapsed = started.elapsedNow()

            assertNull(result, "nothing was listening, so there was nothing to connect to")
            assertTrue(
                elapsed < 15.seconds,
                "gave up after $elapsed, which is long enough that the relay would be tried too late",
            )
            assertTrue(puncher.lastAttempts > 0, "a punch that made no attempts never tried")
        }
    }

    private companion object {
        /**
         * A port with nothing on it. High, fixed and outside the ephemeral range, so it neither collides
         * with the host under test nor changes what the test means between runs.
         */
        const val ECHOLESS_PORT = 39_517
    }
}
