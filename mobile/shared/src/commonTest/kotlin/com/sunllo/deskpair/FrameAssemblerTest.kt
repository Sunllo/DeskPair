package com.sunllo.deskpair

import com.sunllo.deskpair.media.FrameAssembler
import com.sunllo.deskpair.media.LinkStats
import com.sunllo.deskpair.media.GaloisField
import com.sunllo.deskpair.media.MediaCommonHeader
import com.sunllo.deskpair.media.MediaPacket
import com.sunllo.deskpair.media.MediaPacketFlags
import com.sunllo.deskpair.media.MediaPacketType
import com.sunllo.deskpair.media.MediaShardHeader
import com.sunllo.deskpair.media.ReedSolomon
import com.sunllo.deskpair.media.StreamAssemblers
import kotlinx.coroutines.test.runTest
import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * Putting frames back together out of order, with pieces missing.
 *
 * The clock is a variable rather than a real one, because every rule worth testing here is about time:
 * how long a frame still arriving deserves, when one is abandoned, and what happens to the frames behind
 * it. With a real clock those tests are either slow or flaky, and usually both.
 */
class FrameAssemblerTest {

    private var now = 0L

    // Through the streams, as the channel uses them: the ordering and give-up rules are one stream's, the
    // display-switch rules are the set's, and both are worth checking as the phone actually meets them.
    private val assembler = StreamAssemblers { now }
    private val link = LinkStats { now }

    /** The one stream being shown. */
    private suspend fun shown(): FrameAssembler = assembler.live().single()

    /**
     * The bug this guards: after six hundred frames of display 0, display 1's frame 1 looked "already
     * delivered" and the phone kept the old display's last picture for ever.
     */
    @Test
    fun a_keyframe_from_another_display_starts_its_own_sequence() = runTest {
        feed(frameSeq = 600u, payload = ByteArray(300) { 1 }, k = 1, m = 0, keyFrame = true)
        feed(frameSeq = 601u, payload = ByteArray(300) { 2 }, k = 1, m = 0, keyFrame = false)
        assertEquals(600u, assertNotNull(assembler.poll()).frameSeq)
        assertEquals(601u, assertNotNull(assembler.poll()).frameSeq)

        feed(frameSeq = 1u, payload = ByteArray(300) { 3 }, k = 1, m = 0, keyFrame = true, stream = 1)
        feed(frameSeq = 2u, payload = ByteArray(300) { 4 }, k = 1, m = 0, keyFrame = false, stream = 1)

        val first = assertNotNull(assembler.poll(), "the new display's frame 1 must not be taken for an old one")
        assertEquals(1, first.stream)
        assertEquals(1u, first.frameSeq)
        assertEquals(2u, assertNotNull(assembler.poll()).frameSeq)
    }

    /**
     * The report that acknowledges datagram frames names this stream. It said 0 whatever was on screen, so
     * the host never saw a frame of display 1 acknowledged and stopped sending it after a handful.
     */
    @Test
    fun the_stream_being_assembled_is_the_one_reported() = runTest {
        feed(frameSeq = 600u, payload = ByteArray(300) { 1 }, k = 1, m = 0, keyFrame = true)
        assertNotNull(assembler.poll())

        feed(frameSeq = 1u, payload = ByteArray(300) { 1 }, k = 1, m = 0, keyFrame = true, stream = 1)
        assertNotNull(assembler.poll())
        val reported = shown()
        assertEquals(1, reported.stream)
        assertEquals(1u, reported.highestDecodable, "display 1's report is its own, not display 0's 600")
    }

    /** The set's rules are the single display's, for more than one: both come out, each in its own order. */
    @Test
    fun two_confirmed_displays_both_deliver() = runTest {
        assembler.expect(setOf(0, 1))
        feed(frameSeq = 1u, payload = ByteArray(300) { 1 }, k = 1, m = 0, keyFrame = true)
        feed(frameSeq = 1u, payload = ByteArray(300) { 2 }, k = 1, m = 0, keyFrame = true, stream = 1)
        feed(frameSeq = 2u, payload = ByteArray(300) { 3 }, k = 1, m = 0, keyFrame = false)
        feed(frameSeq = 2u, payload = ByteArray(300) { 4 }, k = 1, m = 0, keyFrame = false, stream = 1)

        val got = mutableListOf<Pair<Int, UInt>>()
        while (true) {
            val frame = assembler.poll() ?: break
            got += frame.stream to frame.frameSeq
        }
        assertEquals(listOf(0 to 1u, 0 to 2u), got.filter { it.first == 0 })
        assertEquals(listOf(1 to 1u, 1 to 2u), got.filter { it.first == 1 })

        feed(frameSeq = 1u, payload = ByteArray(300) { 5 }, k = 1, m = 0, keyFrame = true, stream = 2)
        assertNull(assembler.poll(), "a display outside the set is a straggler, even a keyframe of it")
    }

    @Test
    fun once_the_switch_is_confirmed_stragglers_from_the_old_display_are_ignored() = runTest {
        feed(frameSeq = 10u, payload = ByteArray(300) { 1 }, k = 1, m = 0, keyFrame = true)
        assertNotNull(assembler.poll())

        assembler.expect(1)
        feed(frameSeq = 11u, payload = ByteArray(300) { 2 }, k = 1, m = 0, keyFrame = true) // in flight at the switch
        assertNull(assembler.poll(), "a late keyframe of the display just left must not flip the session back")

        feed(frameSeq = 5u, payload = ByteArray(300) { 3 }, k = 1, m = 0, keyFrame = false, stream = 1)
        assertNull(assembler.poll(), "a delta without its keyframe has nothing to decode against")
        feed(frameSeq = 6u, payload = ByteArray(300) { 4 }, k = 1, m = 0, keyFrame = true, stream = 1)
        assertEquals(6u, assertNotNull(assembler.poll()).frameSeq)
    }

    @Test
    fun a_frame_in_one_shard_comes_straight_out() = runTest {
        val payload = ByteArray(300) { (it * 7).toByte() }
        feed(frameSeq = 1u, payload = payload, k = 1, m = 0, keyFrame = true)

        val frame = assertNotNull(assembler.poll(), "a complete frame should be ready immediately")
        assertContentEquals(payload, frame.data)
        assertTrue(frame.keyFrame)
        assertEquals(1u, frame.frameSeq)
    }

    @Test
    fun shards_that_arrive_backwards_still_make_a_frame() = runTest {
        val payload = ByteArray(1000) { (it % 251).toByte() }
        // Reversed, which a network will do and which says nothing about whether the frame is complete.
        feed(frameSeq = 1u, payload = payload, k = 4, m = 0, keyFrame = true, order = (3 downTo 0).toList())

        assertContentEquals(payload, assertNotNull(assembler.poll()).data)
    }

    @Test
    fun a_lost_shard_is_rebuilt_from_parity_and_nobody_notices() = runTest {
        val payload = ByteArray(1000) { (it % 251).toByte() }

        // Two of six shards dropped, which two parity shards exactly cover. The frame must come out whole:
        // this is the case FEC exists for, and the one that makes UDP worth using at all.
        feed(frameSeq = 1u, payload = payload, k = 4, m = 2, keyFrame = true, drop = setOf(0, 2))

        val frame = assertNotNull(assembler.poll(), "FEC should have covered two losses out of six")
        assertContentEquals(payload, frame.data)
        assertEquals(2u, shown().shardsRecovered)
    }

    @Test
    fun a_frame_that_never_completes_is_given_up_once_a_later_one_is_ready() = runTest {
        // One shard of a two-shard frame, with no parity: unrecoverable, and the frame behind it is waiting.
        feed(frameSeq = 1u, payload = ByteArray(600) { 1 }, k = 2, m = 0, keyFrame = true, drop = setOf(1))
        assertNull(assembler.poll(), "an incomplete frame must not be delivered")

        now += 10
        feed(frameSeq = 2u, payload = ByteArray(300) { 2 }, k = 1, m = 0, keyFrame = true)

        // Long enough that reordering is no longer a plausible explanation.
        now += 500
        assembler.tick()

        assertEquals(1u, shown().framesGivenUp)
        val frame = assertNotNull(assembler.poll(), "the keyframe behind it should be delivered")
        assertEquals(2u, frame.frameSeq)
    }

    @Test
    fun deltas_after_a_loss_are_dropped_until_a_keyframe() = runTest {
        feed(frameSeq = 1u, payload = ByteArray(600) { 1 }, k = 2, m = 0, keyFrame = true, drop = setOf(1))
        now += 10
        feed(frameSeq = 2u, payload = ByteArray(300) { 2 }, k = 1, m = 0, keyFrame = false)
        now += 500
        assembler.tick()

        // The delta arrived whole, but its reference never did. Drawing it would smear the picture.
        assertTrue(assembler.referenceBroken(), "a given-up frame should break the reference")
        assertNull(assembler.poll(), "a delta after a lost frame must not be delivered")

        now += 10
        feed(frameSeq = 3u, payload = ByteArray(300) { 3 }, k = 1, m = 0, keyFrame = true)

        val recovered = assertNotNull(assembler.poll(), "a keyframe should restart delivery")
        assertEquals(3u, recovered.frameSeq)
        assertTrue(!assembler.referenceBroken())
    }

    @Test
    fun loss_is_reported_before_the_error_correction_hides_it() = runTest {
        // The sender needs to know what the path is doing. A link losing packets and recovering all of them
        // is still a link whose bitrate should come down, so the figure counts gaps in the sequence.
        link.notePacket(1uL, 100)
        link.notePacket(2uL, 100)
        link.notePacket(4uL, 100)
        link.notePacket(5uL, 100)

        // One of five sequence numbers never arrived. 199 rather than 200 because the desktop truncates
        // this figure too, and 1.0 - 4.0/5.0 is not quite 0.2 in binary — matching it matters more than
        // rounding prettily, since both sides feed the same estimator.
        assertEquals(199, link.lossPermille)
    }

    /**
     * Builds the datagrams for one frame and feeds them in, minus [drop].
     *
     * Parity is generated the way the desktop generates it, from the same coefficients, so a frame repaired
     * here is repaired from bytes a real sender would have produced.
     */
    private suspend fun feed(
        frameSeq: UInt,
        payload: ByteArray,
        k: Int,
        m: Int,
        keyFrame: Boolean,
        drop: Set<Int> = emptySet(),
        order: List<Int>? = null,
        stream: Int = 0,
    ) {
        val shardLength = (payload.size + k - 1) / k
        val data = Array(k) { index ->
            val shard = ByteArray(shardLength)
            val from = index * shardLength
            val take = minOf(shardLength, maxOf(0, payload.size - from))
            if (take > 0) {
                payload.copyInto(shard, 0, from, from + take)
            }
            shard
        }

        val parity = Array(m) { row ->
            val out = ByteArray(shardLength)
            for (column in 0 until k) {
                GaloisField.multiplyAdd(out, data[column], ReedSolomon.coefficient(k, row, column), shardLength)
            }
            out
        }

        val all = data + parity
        val indices = order ?: all.indices.toList()
        for (index in indices) {
            if (index in drop) {
                continue
            }

            val header = MediaShardHeader(
                stream = stream,
                codec = 0,
                blockIndex = 0,
                blockCount = 1,
                frameSeq = frameSeq,
                ptsMs = now,
                shardIndex = index,
                k = k,
                m = m,
                shardLength = shardLength,
                frameLength = payload.size.toUInt(),
                width = 1920,
                height = 1080,
            )

            val plaintext = ByteArray(MediaPacket.SHARD_HEADER_BYTES + shardLength)
            header.write(plaintext)
            all[index].copyInto(plaintext, MediaPacket.SHARD_HEADER_BYTES)

            val common = MediaCommonHeader(
                type = MediaPacketType.VIDEO,
                flags = if (keyFrame) MediaPacketFlags.KEY_FRAME else MediaPacketFlags.NONE,
                packetSeq = (frameSeq.toULong() * 100uL) + index.toULong() + 1uL,
            )

            link.notePacket(common.packetSeq, plaintext.size)
            assembler.accept(common, plaintext)
        }
    }
}

/** The second byte of a report, which every earlier receiver wrote as zero. */
class MediaFeedbackFlagsTest {
    @Test
    fun flags_ride_in_the_byte_that_used_to_be_zero() {
        val bytes = ByteArray(com.sunllo.deskpair.media.MediaPacket.FEEDBACK_BYTES)
        val plain = com.sunllo.deskpair.media.MediaFeedback(1, 25, 7u, 7u, 0uL, 0u, 1uL, 2uL, 0u, 0u)
        plain.write(bytes)
        assertEquals(0, bytes[1].toInt())
        assertEquals(plain, com.sunllo.deskpair.media.MediaFeedback.read(bytes))

        val copy = plain.copy(stream = 2, flags = com.sunllo.deskpair.media.MediaFeedback.LINK_FIELDS_IGNORED)
        copy.write(bytes)
        assertEquals(1, bytes[1].toInt())
        assertEquals(copy, com.sunllo.deskpair.media.MediaFeedback.read(bytes))
    }
}
