package com.sunllo.deskpair

import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * Reading a VP9 keyframe's header into the record VideoToolbox is configured with.
 *
 * The first vector is written out by hand from the specification (6.2), so it does not depend on the writer
 * below agreeing with the parser; the rest use that writer to reach the profiles a desk never sends.
 */
class Vp9ConfigurationTest {

    @Test
    fun `a desk keyframe is read as the host encodes it`() {
        // The first bytes of a real keyframe: libvpx 1.14.0 on the Linux lab machine, 2026-09-26 (LiveVp9Test).
        // Profile 0 keyframe, shown, error resilient; the sync code; color_space 1 (BT.601), studio range;
        // 1280x800 (1279 = 0x04FF, 799 = 0x031F); then the rest of the header, which is not read.
        val frame = hex("83 49 83 42 20 4F F0 31 F0 38 E0 90 70 62 B8 60")

        val config = assertNotNull(Vp9Configuration.parse(frame))

        assertEquals(0, config.profile)
        assertEquals(8, config.bitDepth)
        assertEquals(0, config.chromaSubsampling, "4:2:0")
        assertEquals(false, config.fullRange)
        assertEquals(5, config.matrixCoefficients, "BT.601 is BT.470BG's matrix")
        assertEquals(2, config.colourPrimaries, "VP9 does not carry primaries")
        assertEquals(2, config.transferCharacteristics, "nor a transfer function")
        assertEquals(1280, config.width)
        assertEquals(800, config.height)
        assertEquals(40, config.level)
    }

    @Test
    fun `the record is the vpcC box as the ISO binding lays it out`() {
        val config = assertNotNull(Vp9Configuration.parse(hex("82 49 83 42 20 4F F0 31 F0 00 00")))

        // version 1, flags 0; profile; level; bitDepth 8 << 4 | 4:2:0 << 1 | studio; primaries, transfer,
        // matrix; no codec initialization data.
        assertContentEquals(hex("01 00 00 00 00 28 80 02 02 05 00 00"), config.record())
    }

    @Test
    fun `an unsignalled colour space is recorded as unspecified`() {
        val config = assertNotNull(Vp9Configuration.parse(hex("82 49 83 42 00 4F F0 31 F0 00 00")))

        assertEquals(2, config.matrixCoefficients)
    }

    @Test
    fun `profile 1 says its own subsampling and range`() {
        val config = assertNotNull(Vp9Configuration.parse(keyframe(profile = 1, colorSpace = 2, fullRange = true, subsampling = 0 to 0)))

        assertEquals(1, config.profile)
        assertEquals(3, config.chromaSubsampling, "4:4:4")
        assertEquals(true, config.fullRange)
        assertEquals(1, config.matrixCoefficients, "BT.709")
    }

    @Test
    fun `profile 1 at 4 2 2`() {
        val config = assertNotNull(Vp9Configuration.parse(keyframe(profile = 1, colorSpace = 1, subsampling = 1 to 0)))

        assertEquals(2, config.chromaSubsampling)
    }

    @Test
    fun `profiles 2 and 3 carry ten or twelve bits`() {
        assertEquals(10, Vp9Configuration.parse(keyframe(profile = 2, colorSpace = 5, twelveBit = false))?.bitDepth)
        assertEquals(12, Vp9Configuration.parse(keyframe(profile = 2, colorSpace = 5, twelveBit = true))?.bitDepth)

        val three = assertNotNull(Vp9Configuration.parse(keyframe(profile = 3, colorSpace = 2, twelveBit = true, subsampling = 1 to 0)))
        assertEquals(3, three.profile)
        assertEquals(12, three.bitDepth)
        assertEquals(2, three.chromaSubsampling)
    }

    @Test
    fun `rgb is full range 4 4 4 with the identity matrix`() {
        val config = assertNotNull(Vp9Configuration.parse(keyframe(profile = 1, colorSpace = 7)))

        assertEquals(3, config.chromaSubsampling)
        assertEquals(true, config.fullRange)
        assertEquals(0, config.matrixCoefficients)
    }

    @Test
    fun `anything that is not the start of a keyframe is not a configuration`() {
        assertNull(Vp9Configuration.parse(hex("86 00 00 00")), "an inter frame")
        assertNull(Vp9Configuration.parse(hex("88")), "show_existing_frame: a repeat with no header of its own")
        assertNull(Vp9Configuration.parse(hex("42 49 83 42 20 4F F0 31 F0")), "frame marker 1")
        assertNull(Vp9Configuration.parse(hex("82 49 83 43 20 4F F0 31 F0")), "a broken sync code")
        assertNull(Vp9Configuration.parse(hex("82 49 83 42 20 4F")), "cut off inside the size")
        assertNull(Vp9Configuration.parse(ByteArray(0)), "nothing at all")
        assertNull(Vp9Configuration.parse(keyframe(profile = 0, colorSpace = 7)), "RGB needs an odd profile")
        assertNull(Vp9Configuration.parse(keyframe(profile = 1, colorSpace = 1, subsampling = 0 to 1)), "4:4:0 has no code")
    }

    @Test
    fun `the level is the lowest that holds the picture at sixty frames a second`() {
        assertEquals(30, Vp9Configuration.levelFor(640, 480))
        assertEquals(40, Vp9Configuration.levelFor(1280, 800))
        assertEquals(41, Vp9Configuration.levelFor(1920, 1080))
        assertEquals(50, Vp9Configuration.levelFor(2560, 1440))
        assertEquals(51, Vp9Configuration.levelFor(3840, 2160))
        assertEquals(61, Vp9Configuration.levelFor(7680, 4320))
        assertEquals(62, Vp9Configuration.levelFor(16384, 16384), "past the table is the top level, not a failure")
    }

    @Test
    fun `configurations of the same stream are equal`() {
        val one = Vp9Configuration.parse(hex("82 49 83 42 20 4F F0 31 F0 00 00"))
        val two = Vp9Configuration.parse(hex("82 49 83 42 20 4F F0 31 F0 FF FF"))
        val other = Vp9Configuration.parse(hex("82 49 83 42 20 77 F0 43 70 00 00"))

        assertEquals(one, two, "what follows the size is not part of the configuration")
        assertTrue(one != other, "a different size is a different stream")
    }

    /** A keyframe header as section 6.2 writes it, up to the frame size, then a byte of padding. */
    private fun keyframe(
        profile: Int,
        colorSpace: Int,
        fullRange: Boolean = false,
        subsampling: Pair<Int, Int>? = null,
        twelveBit: Boolean = false,
        width: Int = 1920,
        height: Int = 1080,
    ): ByteArray {
        val w = BitWriter()
        w.write(2, 2) // frame_marker
        w.write(profile and 1, 1)
        w.write(profile shr 1, 1)
        if (profile == 3) w.write(0, 1) // reserved_zero
        w.write(0, 1) // show_existing_frame
        w.write(0, 1) // frame_type: keyframe
        w.write(1, 1) // show_frame
        w.write(0, 1) // error_resilient_mode
        w.write(0x498342, 24)
        if (profile >= 2) w.write(if (twelveBit) 1 else 0, 1)
        w.write(colorSpace, 3)
        if (colorSpace != 7) {
            w.write(if (fullRange) 1 else 0, 1)
            if (profile == 1 || profile == 3) {
                val (x, y) = subsampling ?: (1 to 1)
                w.write(x, 1)
                w.write(y, 1)
                w.write(0, 1) // reserved_zero
            }
        } else if (profile == 1 || profile == 3) {
            w.write(0, 1) // reserved_zero
        }
        w.write(width - 1, 16)
        w.write(height - 1, 16)
        w.write(0, 8)
        return w.bytes()
    }

    private class BitWriter {
        private val bits = mutableListOf<Int>()

        fun write(value: Int, count: Int) {
            for (i in count - 1 downTo 0) bits += (value ushr i) and 1
        }

        fun bytes(): ByteArray = ByteArray((bits.size + 7) / 8) { index ->
            var byte = 0
            for (bit in 0 until 8) byte = (byte shl 1) or (bits.getOrNull(index * 8 + bit) ?: 0)
            byte.toByte()
        }
    }

    private fun hex(text: String): ByteArray =
        text.split(' ').filter { it.isNotEmpty() }.map { it.toInt(16).toByte() }.toByteArray()
}
