package com.sunllo.deskpair

/**
 * What a VP9 keyframe says about its stream, in the shape a decoder is configured with.
 *
 * VideoToolbox will not decode VP9 from the frames alone: its format description has to carry the VP Codec
 * Configuration Record of the ISO media binding ("vpcC"), which in a file comes from the container. A live
 * stream has no container, so the record is rebuilt from the frame's own uncompressed header -- the profile,
 * the bit depth, the chroma subsampling, the colour range and matrix, and the size. MediaCodec reads the same
 * facts from the frame itself, which is why only iOS asks for this.
 *
 * VP9 signals a matrix (`color_space`) and nothing about primaries or transfer, so those two are recorded as
 * unspecified, the way FFmpeg's vpcC writer records a raw VP9 stream.
 */
public class Vp9Configuration internal constructor(
    public val profile: Int,
    /** Ten times the level, as the record stores it (40 is level 4). Chosen from the size at 60 frames a second. */
    public val level: Int,
    public val bitDepth: Int,
    /** The record's code: 0 4:2:0, 2 4:2:2, 3 4:4:4. */
    public val chromaSubsampling: Int,
    public val fullRange: Boolean,
    /** ISO/IEC 23091-4 codes; 2 is "unspecified". */
    public val colourPrimaries: Int,
    public val transferCharacteristics: Int,
    public val matrixCoefficients: Int,
    public val width: Int,
    public val height: Int,
) {
    /** The vpcC box's contents: version 1 with no flags, the record, and no codec initialization data (VP9 has none). */
    public fun record(): ByteArray = byteArrayOf(
        1, 0, 0, 0,
        profile.toByte(),
        level.toByte(),
        ((bitDepth shl 4) or (chromaSubsampling shl 1) or (if (fullRange) 1 else 0)).toByte(),
        colourPrimaries.toByte(),
        transferCharacteristics.toByte(),
        matrixCoefficients.toByte(),
        0, 0,
    )

    override fun equals(other: Any?): Boolean =
        other is Vp9Configuration && record().contentEquals(other.record()) && width == other.width && height == other.height

    override fun hashCode(): Int = record().contentHashCode() * 31 + width * 7 + height

    public companion object {
        private const val FRAME_MARKER = 2
        private const val SYNC_CODE = 0x498342
        private const val CS_RGB = 7

        /**
         * The matrix for each VP9 `color_space`, as FFmpeg's VP9 decoder reads it: unknown, BT.601 (BT.470BG),
         * BT.709, SMPTE 170M, SMPTE 240M, BT.2020, reserved, and RGB (identity).
         */
        private val MATRIX = intArrayOf(2, 5, 1, 6, 7, 9, 2, 0)

        /**
         * VP9 levels as (level, largest picture in luma samples, luma samples a second), from the WebM
         * project's level definitions.
         */
        private val LEVELS = arrayOf(
            Triple(10, 36_864L, 829_440L),
            Triple(11, 73_728L, 2_764_800L),
            Triple(20, 122_880L, 4_608_000L),
            Triple(21, 245_760L, 9_216_000L),
            Triple(30, 552_960L, 20_736_000L),
            Triple(31, 983_040L, 36_864_000L),
            Triple(40, 2_228_224L, 83_558_400L),
            Triple(41, 2_228_224L, 160_432_128L),
            Triple(50, 8_912_896L, 311_951_360L),
            Triple(51, 8_912_896L, 588_251_136L),
            Triple(52, 8_912_896L, 1_176_502_272L),
            Triple(60, 35_651_584L, 1_176_502_272L),
            Triple(61, 35_651_584L, 2_353_004_544L),
            Triple(62, 35_651_584L, 4_706_009_088L),
        )

        /**
         * Reads the start of a keyframe's uncompressed header (VP9 bitstream specification, 6.2). Null for
         * anything that is not the start of a VP9 keyframe: an inter frame, a repeated frame, a broken sync
         * code, or too few bytes. A superframe starts with its first frame, so it reads the same way.
         */
        public fun parse(frame: ByteArray): Vp9Configuration? {
            val bits = BitReader(frame)
            return try {
                read(bits)
            } catch (_: IndexOutOfBoundsException) {
                null
            }
        }

        private fun read(bits: BitReader): Vp9Configuration? {
            if (bits.read(2) != FRAME_MARKER) return null
            val profileLow = bits.read(1)
            val profile = (bits.read(1) shl 1) or profileLow
            if (profile == 3 && bits.read(1) != 0) return null
            if (bits.read(1) == 1) return null // show_existing_frame: a repeat, with no header of its own
            if (bits.read(1) != 0) return null // frame_type: 0 is a keyframe
            bits.read(1) // show_frame
            bits.read(1) // error_resilient_mode
            if (bits.read(24) != SYNC_CODE) return null

            val bitDepth = if (profile >= 2) (if (bits.read(1) == 1) 12 else 10) else 8
            val colorSpace = bits.read(3)
            val fullRange: Boolean
            val subsamplingX: Int
            val subsamplingY: Int
            if (colorSpace != CS_RGB) {
                fullRange = bits.read(1) == 1
                if (profile == 1 || profile == 3) {
                    subsamplingX = bits.read(1)
                    subsamplingY = bits.read(1)
                    if (bits.read(1) != 0) return null
                } else {
                    subsamplingX = 1
                    subsamplingY = 1
                }
            } else {
                // RGB is 4:4:4 and full range by definition, and only the odd profiles can carry it.
                if (profile != 1 && profile != 3) return null
                if (bits.read(1) != 0) return null
                fullRange = true
                subsamplingX = 0
                subsamplingY = 0
            }

            val chroma = when {
                subsamplingX == 1 && subsamplingY == 1 -> 0 // where the chroma sits is not in the stream; FFmpeg says "vertical" too
                subsamplingX == 1 -> 2
                subsamplingY == 0 -> 3
                else -> return null // 4:4:0 has no code in the record
            }

            val width = bits.read(16) + 1
            val height = bits.read(16) + 1
            return Vp9Configuration(
                profile = profile,
                level = levelFor(width, height),
                bitDepth = bitDepth,
                chromaSubsampling = chroma,
                fullRange = fullRange,
                colourPrimaries = 2,
                transferCharacteristics = 2,
                matrixCoefficients = MATRIX[colorSpace],
                width = width,
                height = height,
            )
        }

        /** The lowest level that holds this picture at 60 frames a second, the most the host sends. */
        internal fun levelFor(width: Int, height: Int): Int {
            val picture = width.toLong() * height
            val rate = picture * 60
            return LEVELS.firstOrNull { (_, maxPicture, maxRate) -> picture <= maxPicture && rate <= maxRate }?.first ?: 62
        }
    }

    /** Most significant bit first, as every VP9 header field is written. */
    private class BitReader(private val data: ByteArray) {
        private var position = 0

        fun read(count: Int): Int {
            var value = 0
            repeat(count) {
                val byte = data[position ushr 3].toInt()
                value = (value shl 1) or ((byte ushr (7 - (position and 7))) and 1)
                position++
            }
            return value
        }
    }
}
