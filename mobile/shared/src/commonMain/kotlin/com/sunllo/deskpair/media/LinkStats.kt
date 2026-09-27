package com.sunllo.deskpair.media

import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

/**
 * What the path delivered: bytes, and loss before FEC over the last second, read from gaps in the packet
 * sequence.
 *
 * A property of the link, not of any stream on it. Every stream and every control packet draws from the one
 * packet sequence, so a count kept per stream would read the other streams' packets as gaps and report a
 * plausible and wrong loss rate. The desktop's `LinkStats` is the same thing; keep the two alike.
 */
public class LinkStats(private val nowMillis: () -> Long) {

    private val guard = Mutex()

    private var windowFirstSeq: ULong = 0uL
    private var windowLastSeq: ULong = 0uL
    private var windowReceived: ULong = 0uL
    private var windowStartMs = 0L

    // A separate flag rather than windowStartMs == 0, because a monotonic clock starts at zero: using the
    // timestamp as its own "not started yet" marker restarts the window on every packet for the first
    // second of a session, and loss then reads as zero exactly when it matters most.
    private var windowStarted = false

    public var receivedBytes: ULong = 0uL
        private set

    /**
     * Loss before FEC over the last second, in parts per thousand, read from gaps in the packet sequence.
     *
     * Before FEC on purpose: the sender needs to know what the network is doing, not what the error
     * correction managed to hide. A link losing three per cent and recovering all of it is still a link
     * whose bitrate should come down.
     */
    public val lossPermille: Int
        get() {
            if (windowLastSeq <= windowFirstSeq || windowReceived == 0uL) {
                return 0
            }
            val expected = (windowLastSeq - windowFirstSeq + 1uL).toDouble()
            val ratio = 1.0 - windowReceived.toDouble() / expected
            return (ratio * 1000).toInt().coerceIn(0, 1000)
        }

    /** Every authenticated datagram, of any type, because loss is a property of the path and not of video. */
    public suspend fun notePacket(packetSeq: ULong, bytes: Int): Unit = guard.withLock {
        receivedBytes += bytes.toULong()

        val now = nowMillis()
        if (!windowStarted || now - windowStartMs > 1000) {
            windowStarted = true
            windowStartMs = now
            windowFirstSeq = packetSeq
            windowLastSeq = packetSeq
            windowReceived = 0uL
        }

        if (windowReceived == 0uL || packetSeq < windowFirstSeq) {
            windowFirstSeq = packetSeq
        }
        if (packetSeq > windowLastSeq) {
            windowLastSeq = packetSeq
        }
        windowReceived += 1uL
    }
}
