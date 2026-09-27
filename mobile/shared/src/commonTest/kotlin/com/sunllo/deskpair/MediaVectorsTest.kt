package com.sunllo.deskpair

import com.sunllo.deskpair.framing.hexToBytes
import com.sunllo.deskpair.framing.toHex
import com.sunllo.deskpair.media.MediaCipher
import com.sunllo.deskpair.media.MediaCommonHeader
import com.sunllo.deskpair.media.MediaFeedback
import com.sunllo.deskpair.media.MediaPacket
import com.sunllo.deskpair.media.MediaPacketFlags
import com.sunllo.deskpair.media.MediaPacketType
import com.sunllo.deskpair.media.MediaShardHeader
import com.sunllo.deskpair.media.ReedSolomon
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.int
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.long
import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * The UDP media format, against bytes the desktop produced.
 *
 * Every failure mode here is quiet. A header field at the wrong offset still round-trips through this code;
 * a cipher that derives its nonce differently still encrypts; a Reed-Solomon decode that is subtly wrong
 * still returns a shard. What they produce is a picture made of garbage, or a stream that stops, and
 * neither says why — which is exactly why none of it is tested against itself.
 */
class MediaVectorsTest {

    private val media: JsonObject =
        Json.parseToJsonElement(PROTOCOL_VECTORS_JSON).jsonObject["media"]!!.jsonObject

    private fun JsonObject.hex(field: String): ByteArray = this[field]!!.jsonPrimitive.content.hexToBytes()

    private fun JsonObject.int(field: String): Int = this[field]!!.jsonPrimitive.int

    private fun JsonObject.long(field: String): Long = this[field]!!.jsonPrimitive.long

    @Test
    fun the_common_header_is_written_where_the_desktop_writes_it() {
        val expected = media["commonHeader"]!!.jsonObject
        val header = MediaCommonHeader(
            type = MediaPacketType.of(expected.int("type"))!!,
            flags = expected.int("flags"),
            packetSeq = expected.long("packetSeq").toULong(),
        )

        val bytes = ByteArray(MediaPacket.COMMON_HEADER_BYTES)
        header.write(bytes)
        assertEquals(expected.hex("bytes").toHex(), bytes.toHex())

        val parsed = assertNotNull(MediaCommonHeader.read(expected.hex("bytes")))
        assertEquals(header, parsed)
        assertTrue(parsed.flags and MediaPacketFlags.KEY_FRAME != 0, "the keyframe flag was lost")
        assertTrue(parsed.flags and MediaPacketFlags.LAST_SHARD != 0, "the last-shard flag was lost")
    }

    @Test
    fun the_shard_header_is_written_where_the_desktop_writes_it() {
        val expected = media["shardHeader"]!!.jsonObject
        val header = MediaShardHeader(
            stream = expected.int("stream"),
            codec = expected.int("codec"),
            blockIndex = expected.int("blockIndex"),
            blockCount = expected.int("blockCount"),
            frameSeq = expected.long("frameSeq").toUInt(),
            ptsMs = expected.long("ptsMs"),
            shardIndex = expected.int("shardIndex"),
            k = expected.int("k"),
            m = expected.int("m"),
            shardLength = expected.int("shardLength"),
            frameLength = expected.long("frameLength").toUInt(),
            width = expected.int("width"),
            height = expected.int("height"),
        )

        val bytes = ByteArray(MediaPacket.SHARD_HEADER_BYTES)
        header.write(bytes)
        assertEquals(expected.hex("bytes").toHex(), bytes.toHex())
        assertEquals(header, assertNotNull(MediaShardHeader.read(expected.hex("bytes"))))
    }

    @Test
    fun the_feedback_report_is_written_where_the_desktop_writes_it() {
        val expected = media["feedback"]!!.jsonObject
        val feedback = MediaFeedback(
            stream = expected.int("stream"),
            lossPermille = expected.int("lossPermille"),
            highestDecodableFrameSeq = expected.long("highestDecodableFrameSeq").toUInt(),
            lastFrameSeqReceived = expected.long("lastFrameSeqReceived").toUInt(),
            echoPacketSeq = expected.long("echoPacketSeq").toULong(),
            echoDelayMicros = expected.long("echoDelayMicros").toUInt(),
            receivedBytes = expected.long("receivedBytes").toULong(),
            receiverClockMicros = expected.long("receiverClockMicros").toULong(),
            framesGivenUp = expected.long("framesGivenUp").toUInt(),
            shardsRecovered = expected.long("shardsRecovered").toUInt(),
        )

        val bytes = ByteArray(MediaPacket.FEEDBACK_BYTES)
        feedback.write(bytes)
        assertEquals(expected.hex("bytes").toHex(), bytes.toHex())
        assertEquals(feedback, assertNotNull(MediaFeedback.read(expected.hex("bytes"))))
    }

    @Test
    fun a_datagram_the_desktop_sealed_opens_here() {
        val cipherVectors = media["cipher"]!!.jsonObject
        val key = cipherVectors.hex("key")
        val iv = cipherVectors.hex("ivPrefix")
        val cipher = MediaCipher(key, iv, key, iv)

        for (case in cipherVectors["datagrams"]!!.jsonArray) {
            val fields = case.jsonObject
            val datagram = fields.hex("datagram")
            val opened = assertNotNull(
                cipher.open(datagram),
                "a datagram the desktop sealed did not open; the nonce or the associated data differ",
            )

            assertEquals(fields.long("packetSeq").toULong(), opened.header.packetSeq)
            assertContentEquals(fields.hex("plaintext"), opened.plaintext)
        }
    }

    @Test
    fun a_datagram_that_arrives_twice_is_refused_the_second_time() {
        val cipherVectors = media["cipher"]!!.jsonObject
        val key = cipherVectors.hex("key")
        val iv = cipherVectors.hex("ivPrefix")
        val cipher = MediaCipher(key, iv, key, iv)

        val first = cipherVectors["datagrams"]!!.jsonArray.first().jsonObject.hex("datagram")
        assertNotNull(cipher.open(first))

        // UDP has no notion of "already delivered", so anyone who captured a packet could send it again.
        assertNull(cipher.open(first), "a replayed datagram was accepted")
    }

    @Test
    fun a_tampered_datagram_does_not_open() {
        val cipherVectors = media["cipher"]!!.jsonObject
        val key = cipherVectors.hex("key")
        val iv = cipherVectors.hex("ivPrefix")

        // The sequence is plaintext, so it is the obvious thing to alter — and altering it must fail,
        // because it is the associated data that chose the nonce.
        val original = cipherVectors["datagrams"]!!.jsonArray.last().jsonObject.hex("datagram")
        val tampered = original.copyOf()
        tampered[4] = (tampered[4] + 1).toByte()

        assertNull(MediaCipher(key, iv, key, iv).open(tampered), "a datagram with an altered sequence opened")

        val bodyTampered = original.copyOf()
        bodyTampered[MediaPacket.COMMON_HEADER_BYTES] = (bodyTampered[MediaPacket.COMMON_HEADER_BYTES] + 1).toByte()
        assertNull(MediaCipher(key, iv, key, iv).open(bodyTampered), "a datagram with an altered body opened")
    }

    @Test
    fun the_generator_coefficients_match_the_desktop() {
        val rs = media["reedSolomon"]!!.jsonObject
        val k = rs.int("k")
        val m = rs.int("m")
        val expected = rs["coefficients"]!!.jsonArray.map { it.jsonPrimitive.int }

        var index = 0
        for (row in 0 until m) {
            for (column in 0 until k) {
                assertEquals(
                    expected[index++],
                    ReedSolomon.coefficient(k, row, column),
                    "generator coefficient at row $row column $column",
                )
            }
        }
    }

    @Test
    fun two_lost_shards_are_rebuilt_exactly() {
        val rs = media["reedSolomon"]!!.jsonObject
        val k = rs.int("k")
        val m = rs.int("m")
        val shardLength = rs.int("shardLength")

        val data = rs["data"]!!.jsonArray.map { it.jsonPrimitive.content.hexToBytes() }
        val parity = rs["parity"]!!.jsonArray.map { it.jsonPrimitive.content.hexToBytes() }

        val shards = Array(k + m) { index ->
            if (index < k) data[index].copyOf() else parity[index - k].copyOf()
        }
        val present = BooleanArray(k + m) { true }

        // Two data shards go missing — the most that m = 2 parity can cover, which is the case worth
        // testing because it is where an off-by-one in the matrix stops being survivable.
        val lost = intArrayOf(1, 2)
        for (index in lost) {
            shards[index] = ByteArray(shardLength)
            present[index] = false
        }

        assertTrue(ReedSolomon.decode(shards, present, k, shardLength), "recovery should have been possible")
        for (index in lost) {
            assertContentEquals(data[index], shards[index], "shard $index came back wrong")
        }
    }

    @Test
    fun too_few_shards_is_reported_rather_than_guessed() {
        val rs = media["reedSolomon"]!!.jsonObject
        val k = rs.int("k")
        val m = rs.int("m")
        val shardLength = rs.int("shardLength")

        val shards = Array(k + m) { ByteArray(shardLength) }
        val present = BooleanArray(k + m) { true }

        // One more than the parity can cover. Returning something plausible here would put invented bytes
        // into a video frame; the honest answer is to say no and let the caller ask for a keyframe.
        for (index in 0 until m + 1) {
            present[index] = false
        }

        assertFalse(ReedSolomon.decode(shards, present, k, shardLength))
    }
}
