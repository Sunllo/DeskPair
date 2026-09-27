using System.Runtime.InteropServices;
using DeskPair.Codec.Vpx;
using DeskPair.Codec.Vpx.Native;

namespace DeskPair.Codec.Vpx.Tests;

/// <summary>
/// The binding reaches into libvpx's structs by offset, so the offsets are the contract. These recompute them
/// from the C declarations rather than restating the constants, because a test that copies the number it is
/// checking proves nothing. The declarations are quoted from libvpx 1.15.2's public headers.
/// </summary>
public class VpxLayoutTests
{
    /// <summary>
    /// <code>
    /// typedef struct vpx_codec_enc_cfg {
    ///   unsigned int g_usage, g_threads, g_profile, g_w, g_h;
    ///   vpx_bit_depth_t g_bit_depth; unsigned int g_input_bit_depth;
    ///   struct vpx_rational g_timebase;            // int num, den
    ///   vpx_codec_er_flags_t g_error_resilient;    // uint32
    ///   enum vpx_enc_pass g_pass; unsigned int g_lag_in_frames;
    ///   unsigned int rc_dropframe_thresh, rc_resize_allowed,
    ///                rc_scaled_width, rc_scaled_height,
    ///                rc_resize_up_thresh, rc_resize_down_thresh;
    ///   enum vpx_rc_mode rc_end_usage;
    ///   vpx_fixed_buf_t rc_twopass_stats_in, rc_firstpass_mb_stats_in;   // { void *buf; size_t sz; }
    ///   unsigned int rc_target_bitrate, rc_min_quantizer, rc_max_quantizer, ...
    /// </code>
    /// Everything up to rc_end_usage is a four-byte int or enum, so the offsets simply count. The two
    /// <c>vpx_fixed_buf_t</c> then force eight-byte alignment, which is where the one hole in the prefix is.
    /// </summary>
    [Fact]
    public void The_encoder_config_prefix_is_a_run_of_four_byte_fields()
    {
        int[] inOrder =
        [
            VpxInterop.EncCfg.GUsage, VpxInterop.EncCfg.GThreads, VpxInterop.EncCfg.GProfile,
            VpxInterop.EncCfg.GW, VpxInterop.EncCfg.GH, VpxInterop.EncCfg.GBitDepth,
            VpxInterop.EncCfg.GInputBitDepth, VpxInterop.EncCfg.GTimebaseNum, VpxInterop.EncCfg.GTimebaseDen,
            VpxInterop.EncCfg.GErrorResilient, VpxInterop.EncCfg.GPass, VpxInterop.EncCfg.GLagInFrames,
            VpxInterop.EncCfg.RcDropframeThresh, VpxInterop.EncCfg.RcResizeAllowed,
            VpxInterop.EncCfg.RcScaledWidth, VpxInterop.EncCfg.RcScaledHeight,
            VpxInterop.EncCfg.RcResizeUpThresh, VpxInterop.EncCfg.RcResizeDownThresh,
            VpxInterop.EncCfg.RcEndUsage,
        ];

        for (int i = 0; i < inOrder.Length; i++)
        {
            inOrder[i].ShouldBe(i * 4, $"field {i} of the prefix");
        }
    }

    /// <summary>
    /// After rc_end_usage at 72 come two vpx_fixed_buf_t, a pointer and a size_t each, so they start at the next
    /// multiple of the pointer size and the rate-control run resumes four pointers later: 80 + 32 = 112 on 64-bit
    /// targets, 76 + 16 = 92 on 32-bit ARM and x86. Getting this wrong by one field writes the target bitrate into
    /// the tail of a pointer.
    /// </summary>
    [Theory]
    [InlineData(8, 8, 112)] // 64-bit Unix
    [InlineData(8, 4, 112)] // 64-bit Windows
    [InlineData(4, 4, 92)] // 32-bit ARM and x86
    public void The_two_fixed_buffers_push_the_rate_control_fields_past_four_pointers(int pointer, int cLong, int expected)
    {
        new VpxInterop.Abi(pointer, cLong).RcTargetBitrate.ShouldBe(expected);
    }

    [Fact]
    public void The_rate_control_run_follows_the_fixed_buffers_in_order()
    {
        VpxInterop.EncCfg.RcTargetBitrate.ShouldBe(VpxInterop.Abi.Current.RcTargetBitrate);

        int[] inOrder =
        [
            VpxInterop.EncCfg.RcTargetBitrate, VpxInterop.EncCfg.RcMinQuantizer, VpxInterop.EncCfg.RcMaxQuantizer,
            VpxInterop.EncCfg.RcUndershootPct, VpxInterop.EncCfg.RcOvershootPct,
            VpxInterop.EncCfg.RcBufSz, VpxInterop.EncCfg.RcBufInitialSz, VpxInterop.EncCfg.RcBufOptimalSz,
        ];
        for (int i = 0; i < inOrder.Length; i++)
        {
            inOrder[i].ShouldBe(VpxInterop.EncCfg.RcTargetBitrate + (i * 4));
        }

        // Then four rc_2pass_vbr_* fields this binding never touches, and the keyframe run.
        VpxInterop.EncCfg.KfMode.ShouldBe(VpxInterop.EncCfg.RcBufOptimalSz + 4 + (4 * 4));
        VpxInterop.EncCfg.KfMinDist.ShouldBe(VpxInterop.EncCfg.KfMode + 4);
        VpxInterop.EncCfg.KfMaxDist.ShouldBe(VpxInterop.EncCfg.KfMinDist + 4);
    }

    /// <summary>libvpx 1.15 uses 496 bytes; the buffer must be comfortably larger, never smaller.</summary>
    [Fact]
    public void The_config_buffer_is_larger_than_any_libvpx_has_needed()
    {
        const int Libvpx115Size = 496;

        VpxInterop.EncCfg.Size.ShouldBeGreaterThan(Libvpx115Size);
        VpxInterop.EncCfg.KfMaxDist.ShouldBeLessThan(Libvpx115Size);
    }

    /// <summary>
    /// The packet's frame is <c>{ void *buf; size_t sz; int64 pts; unsigned long duration; uint32 flags; }</c>
    /// in an eight-aligned union after the four-byte kind. The size is pointer-sized and the duration a C long --
    /// four bytes on Windows and on 32-bit targets, eight on 64-bit Unix -- so the keyframe flag moves with both.
    /// Reading it at the wrong offset does not fail: it returns other data, and every frame looks like a delta
    /// frame, which a viewer experiences as a picture that never starts.
    /// </summary>
    [Theory]
    [InlineData(8, 8, 16, 24, 32, 40)] // 64-bit Unix
    [InlineData(8, 4, 16, 24, 32, 36)] // 64-bit Windows
    [InlineData(4, 4, 12, 16, 24, 28)] // 32-bit ARM and x86
    public void The_keyframe_flag_moves_with_the_size_of_a_pointer_and_a_c_long(int pointer, int cLong, int sz, int pts, int duration, int flags)
    {
        var abi = new VpxInterop.Abi(pointer, cLong);

        (abi.FrameSz, abi.FramePts, abi.FrameDuration, abi.FrameFlags).ShouldBe((sz, pts, duration, flags));
    }

    [Fact]
    public void This_build_reads_packets_with_its_own_abi()
    {
        VpxInterop.CxPkt.FrameBuf.ShouldBe(8, "the union is eight-aligned, after the four-byte kind");
        VpxInterop.CxPkt.FrameFlags.ShouldBe(VpxInterop.Abi.Current.FrameFlags);

        int cLongSize = OperatingSystem.IsWindows() || nint.Size == 4 ? 4 : 8;
        VpxInterop.Abi.Current.CLongSize.ShouldBe(cLongSize, "and CLong must agree, since the calls pass one");
    }

    /// <summary>
    /// <code>
    /// typedef struct vpx_image {
    ///   vpx_img_fmt_t fmt; vpx_color_space_t cs; vpx_color_range_t range;
    ///   unsigned int w, h, bit_depth, d_w, d_h, r_w, r_h, x_chroma_shift, y_chroma_shift;
    ///   unsigned char *planes[4]; int stride[4]; ...
    /// </code>
    /// Twelve four-byte fields, then the plane pointers. <c>d_w</c>/<c>d_h</c> are the displayed size, which
    /// is what the viewer wants; <c>w</c>/<c>h</c> are rounded up to whole macroblocks.
    /// </summary>
    [Fact]
    public void The_image_planes_follow_twelve_four_byte_fields()
    {
        VpxInterop.Img.Fmt.ShouldBe(0);
        VpxInterop.Img.W.ShouldBe(3 * 4);
        VpxInterop.Img.H.ShouldBe(4 * 4);
        VpxInterop.Img.DW.ShouldBe(6 * 4);
        VpxInterop.Img.DH.ShouldBe(7 * 4);
        VpxInterop.Img.BitDepth.ShouldBe(5 * 4);
        VpxInterop.Img.XChromaShift.ShouldBe(10 * 4);
        VpxInterop.Img.YChromaShift.ShouldBe(11 * 4);
        VpxInterop.Img.Planes.ShouldBe(12 * 4);
        VpxInterop.Img.Stride.ShouldBe(VpxInterop.Abi.Current.Stride);
        VpxInterop.Img.Size.ShouldBeGreaterThan(136, "libvpx 1.15 uses 136 bytes");
    }

    /// <summary>
    /// After the four plane pointers: <c>int stride[4]; int bps; void *user_priv; unsigned char *img_data;</c>.
    /// On 64-bit targets user_priv has to be eight-aligned, so there is a hole after bps; on 32-bit ones there is not.
    /// </summary>
    [Theory]
    [InlineData(8, 80, 96, 112)]
    [InlineData(4, 64, 80, 88)]
    public void The_strides_and_image_data_follow_the_plane_pointers(int pointer, int stride, int bps, int imgData)
    {
        var abi = new VpxInterop.Abi(pointer, 4);

        (abi.Stride, abi.Bps, abi.ImgData).ShouldBe((stride, bps, imgData));
    }

    /// <summary>
    /// <c>vpx_codec_ctx_t</c> holds a <c>vpx_codec_flags_t</c>, which is a C <c>long</c>. On Windows it is
    /// four bytes followed by four of padding before the eight-aligned union; on LP64 it is eight with the
    /// padding earlier. The two arrangements happen to give the same size and the same offsets for
    /// everything after, which is why one declaration serves both. On a 32-bit process every one of the seven
    /// members is four bytes and there is no padding at all, so it is seven pointers wide everywhere: 56 or 28.
    /// </summary>
    [Fact]
    public void The_codec_context_is_the_same_size_on_both_conventions()
    {
        Marshal.SizeOf<VpxInterop.VpxCodecCtx>().ShouldBe(7 * nint.Size);
    }

    [Fact]
    public void The_image_format_is_planar_i420()
    {
        const int VpxImgFmtPlanar = 0x100;

        VpxInterop.VpxImgFmtI420.ShouldBe(VpxImgFmtPlanar | 2);
    }

    /// <summary>
    /// libvpx 1.15.2: VPX_IMAGE_ABI_VERSION 5, so VPX_CODEC_ABI_VERSION is 4 + 5 = 9;
    /// VPX_DECODER_ABI_VERSION is 3 + 9 = 12; VPX_TPL_ABI_VERSION 4 gives
    /// VPX_EXT_RATECTRL_ABI_VERSION 6 + 4 = 10, so VPX_ENCODER_ABI_VERSION is 18 + 9 + 10 = 37.
    /// </summary>
    [Fact]
    public void The_abi_versions_are_the_ones_libvpx_computes()
    {
        const int Image = 5;
        const int Codec = 4 + Image;
        const int Tpl = 4;
        const int ExtRateCtrl = 6 + Tpl;

        VpxInterop.DecoderAbiVersion.ShouldBe(3 + Codec);
        VpxInterop.EncoderAbiVersion.ShouldBe(18 + Codec + ExtRateCtrl);
    }

    /// <summary>The expected version is tried first, then a band either side, so a nearby release still works.</summary>
    [Fact]
    public void The_abi_search_starts_at_the_expected_version_and_widens()
    {
        int[] tried = [.. VpxVideoEncoder.AbiCandidates(37)];

        tried[0].ShouldBe(37);
        tried.ShouldContain(33);
        tried.ShouldContain(41);
        tried.Distinct().Count().ShouldBe(tried.Length, "a repeated candidate is wasted work");
    }

    /// <summary>
    /// <code>
    /// enum vp8e_enc_control_id {
    ///   VP8E_SET_ROI_MAP = 8, VP8E_SET_ACTIVEMAP, VP8E_SET_SCALEMODE = 11,
    ///   VP8E_SET_CPUUSED = 13, VP8E_SET_ENABLEAUTOALTREF, VP8E_SET_NOISE_SENSITIVITY, VP8E_SET_SHARPNESS,
    ///   VP8E_SET_STATIC_THRESHOLD, ...                       // and on, one each, to
    ///   VP9E_SET_COLOR_SPACE,
    ///   VP9E_SET_MIN_GF_INTERVAL = 48, ...                   // and on again to VP9E_SET_ROW_MT
    /// </code>
    /// Control ids are enumerators, most of them implicit, so each is its position counted from the last
    /// explicit value. A wrong one is not an error: libvpx sets some other control to our value, or answers
    /// "invalid parameter" for a control that would have mattered, and the encoder runs on regardless.
    /// </summary>
    [Fact]
    public void The_control_ids_are_their_positions_in_vp8cx_h()
    {
        string[] fromCpuUsed =
        [
            "VP8E_SET_CPUUSED", "VP8E_SET_ENABLEAUTOALTREF", "VP8E_SET_NOISE_SENSITIVITY", "VP8E_SET_SHARPNESS",
            "VP8E_SET_STATIC_THRESHOLD", "VP8E_SET_TOKEN_PARTITIONS", "VP8E_GET_LAST_QUANTIZER",
            "VP8E_GET_LAST_QUANTIZER_64", "VP8E_SET_ARNR_MAXFRAMES", "VP8E_SET_ARNR_STRENGTH", "VP8E_SET_ARNR_TYPE",
            "VP8E_SET_TUNING", "VP8E_SET_CQ_LEVEL", "VP8E_SET_MAX_INTRA_BITRATE_PCT", "VP8E_SET_FRAME_FLAGS",
            "VP9E_SET_MAX_INTER_BITRATE_PCT", "VP9E_SET_GF_CBR_BOOST_PCT", "VP8E_SET_TEMPORAL_LAYER_ID",
            "VP8E_SET_SCREEN_CONTENT_MODE", "VP9E_SET_LOSSLESS", "VP9E_SET_TILE_COLUMNS", "VP9E_SET_TILE_ROWS",
            "VP9E_SET_FRAME_PARALLEL_DECODING", "VP9E_SET_AQ_MODE", "VP9E_SET_FRAME_PERIODIC_BOOST",
            "VP9E_SET_NOISE_SENSITIVITY", "VP9E_SET_SVC", "VP9E_SET_ROI_MAP", "VP9E_SET_SVC_PARAMETERS",
            "VP9E_SET_SVC_LAYER_ID", "VP9E_SET_TUNE_CONTENT", "VP9E_GET_SVC_LAYER_ID", "VP9E_REGISTER_CX_CALLBACK",
            "VP9E_SET_COLOR_SPACE",
        ];
        string[] fromMinGfInterval =
        [
            "VP9E_SET_MIN_GF_INTERVAL", "VP9E_SET_MAX_GF_INTERVAL", "VP9E_GET_ACTIVEMAP", "VP9E_SET_COLOR_RANGE",
            "VP9E_SET_SVC_REF_FRAME_CONFIG", "VP9E_SET_RENDER_SIZE", "VP9E_SET_TARGET_LEVEL", "VP9E_SET_ROW_MT",
        ];
        int Id(string name) => Array.IndexOf(fromCpuUsed, name) is >= 0 and int i ? 13 + i : 48 + Array.IndexOf(fromMinGfInterval, name);

        VpxInterop.Vp8eSetCpuUsed.ShouldBe(Id("VP8E_SET_CPUUSED"));
        VpxInterop.Vp8eSetStaticThreshold.ShouldBe(Id("VP8E_SET_STATIC_THRESHOLD"));
        VpxInterop.Vp9eSetTileColumns.ShouldBe(Id("VP9E_SET_TILE_COLUMNS"));
        VpxInterop.Vp9eSetAqMode.ShouldBe(Id("VP9E_SET_AQ_MODE"));
        VpxInterop.Vp9eSetTuneContent.ShouldBe(Id("VP9E_SET_TUNE_CONTENT"));
        VpxInterop.Vp9eSetColorSpace.ShouldBe(Id("VP9E_SET_COLOR_SPACE"));
        VpxInterop.Vp9eSetRowMt.ShouldBe(Id("VP9E_SET_ROW_MT"));
    }

    /// <summary>
    /// Asking whether libvpx is present must answer rather than throw, on a machine that has never had it:
    /// the factories are probed during negotiation, and an exception there would take the session with it.
    /// </summary>
    [Fact]
    public void Asking_whether_libvpx_is_present_never_throws()
    {
        bool available = VpxInterop.IsAvailable;

        VpxInterop.VersionString().ShouldNotBeNullOrEmpty();
        available.ShouldBe(VpxInterop.IsAvailable, "and the answer is cached, not re-probed");
    }
}
