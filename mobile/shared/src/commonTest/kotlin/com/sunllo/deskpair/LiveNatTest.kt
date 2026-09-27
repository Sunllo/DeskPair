package com.sunllo.deskpair

import com.sunllo.deskpair.transport.NatTypeDetector
import com.sunllo.deskpair.transport.Ports
import com.sunllo.deskpair.transport.connectReusable
import com.sunllo.deskpair.transport.splitHostPort
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.test.runTest
import kotlinx.coroutines.withContext
import sunllo.rendezvous.NatType
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNotEquals
import kotlin.test.assertTrue
import kotlin.time.Duration.Companion.seconds

/**
 * Whether this platform can do what hole punching requires, measured rather than assumed.
 *
 * Two things, and the first is the one the plan flagged as the risk. Punching needs several live sockets
 * sharing one local port, which ktor's client cannot express and which every platform permits under a
 * slightly different flag — SO_REUSEADDR everywhere, SO_REUSEPORT not on Android. The only way to know is
 * to bind one and then bind it again while the first is still open, against a server that really answers.
 *
 * The second is the detection itself: two probes from that shared port to two server ports, and whether
 * the public ports the server reports agree. A network where they agree can be punched through; one where
 * they do not is symmetric and only a relay will cross it. Either answer is a pass — what would be a
 * failure is not being able to ask.
 *
 *   SUNLLO_TEST_RENDEZVOUS=<server> ./gradlew :shared:jvmTest
 */
class LiveNatTest {

    @Test
    fun one_local_port_can_carry_two_connections_at_once() {
        val server = testEnv("SUNLLO_TEST_RENDEZVOUS")?.takeIf { it.isNotEmpty() } ?: run {
            println("SKIPPED: set SUNLLO_TEST_RENDEZVOUS=host[:port] to measure port reuse on this platform.")
            return
        }

        val (host, port) = splitHostPort(server, Ports.RENDEZVOUS)

        runTest(timeout = 60.seconds) {
            withContext(Dispatchers.Default) {
                val first = connectReusable(0, host, port, 5_000)
                try {
                    assertNotEquals(0, first.localPort, "a bound socket that reports no port cannot be reused")

                    // The same local port again, while the first is still open. Without working reuse the
                    // bind fails here, and this platform cannot punch — which is a fact worth having
                    // rather than a bug to hide.
                    val second = connectReusable(first.localPort, host, Ports.NAT_TEST, 5_000)
                    second.use {
                        assertEquals(
                            first.localPort,
                            it.localPort,
                            "the second connection went out from a different port, so the server's " +
                                "observation of the first says nothing about it",
                        )
                    }
                } finally {
                    first.close()
                }
            }
        }
    }

    @Test
    fun the_server_can_tell_us_what_kind_of_nat_this_is() {
        val server = testEnv("SUNLLO_TEST_RENDEZVOUS")?.takeIf { it.isNotEmpty() } ?: run {
            println("SKIPPED: set SUNLLO_TEST_RENDEZVOUS=host[:port] to detect the NAT type.")
            return
        }

        val (host, port) = splitHostPort(server, Ports.RENDEZVOUS)

        runTest(timeout = 60.seconds) {
            withContext(Dispatchers.Default) {
                val detector = NatTypeDetector(host, port)
                val type = detector.detect(nowMillis = 0)
                println("NAT: $type, probed from local port ${detector.probedLocalPort}")

                assertNotEquals(
                    NatType.NAT_UNKNOWN,
                    type,
                    "the probe could not complete, so nothing is known about this network",
                )
                assertTrue(detector.probedLocalPort > 0, "a detection that names no port cannot inform a punch")
            }
        }
    }
}
