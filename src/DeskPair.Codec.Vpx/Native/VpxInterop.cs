using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DeskPair.Codec.Vpx.Native;

/// <summary>
/// libvpx's C API, loaded at run time. VP9 is the royalty-free codec of the set: the source is BSD-3 and
/// Google grants the patents, so unlike OpenH264 a built binary can be shipped by whoever wants to. Nothing
/// is bundled here yet, so the loader looks the library up the same way the OpenH264 one does and reports
/// itself unavailable when there is nothing to find.
/// </summary>
internal static unsafe partial class VpxInterop
{
    public const string LibraryName = "sunllo-vpx";

    private static nint _handle;

    static VpxInterop() => NativeLibrary.SetDllImportResolver(typeof(VpxInterop).Assembly, Resolve);

    /// <summary>True when a libvpx was found and loaded. Everything else is only valid once this is true.</summary>
    public static bool IsAvailable => Available.Value;

    private static readonly Lazy<bool> Available = new(() =>
    {
        try
        {
            return vpx_codec_version() != 0 && vpx_codec_vp9_cx() != 0 && vpx_codec_vp9_dx() != 0;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }
    });

    /// <summary>"v1.15.2" or similar, for the log line that says which codec is actually running.</summary>
    public static string VersionString()
    {
        try
        {
            return Marshal.PtrToStringUTF8(vpx_codec_version_str()) ?? "unknown";
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return "unavailable";
        }
    }

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != LibraryName)
        {
            return 0;
        }

        if (_handle != 0)
        {
            return _handle;
        }

        foreach (string candidate in CandidatePaths())
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out nint h))
            {
                return _handle = h;
            }
        }

        foreach (string bare in CandidateNames())
        {
            if (NativeLibrary.TryLoad(bare, out nint h))
            {
                return _handle = h;
            }
        }

        return 0;
    }

    /// <summary>
    /// What a libvpx shared library is called. The Windows build produces vpx.dll; the autotools builds on
    /// Linux and macOS carry the soname major version, which has been 11 since libvpx 1.15 and 9 before it,
    /// so both are tried before the unversioned development symlink.
    /// </summary>
    private static IEnumerable<string> CandidateNames()
    {
        if (OperatingSystem.IsWindows())
        {
            return ["vpx.dll", "libvpx.dll", "libvpx-1.dll"];
        }

        if (OperatingSystem.IsMacOS())
        {
            return ["libvpx.11.dylib", "libvpx.9.dylib", "libvpx.dylib"];
        }

        return ["libvpx.so.11", "libvpx.so.9", "libvpx.so"];
    }

    private static IEnumerable<string> CandidatePaths()
    {
        string? explicitPath = Environment.GetEnvironmentVariable("SUNLLO_LIBVPX_PATH");
        if (!string.IsNullOrEmpty(explicitPath))
        {
            yield return explicitPath;
        }

        string baseDir = AppContext.BaseDirectory;
        string rid = RuntimeInformation.RuntimeIdentifier;
        string arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        string osRid = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        foreach (string name in CandidateNames())
        {
            yield return Path.Combine(baseDir, name);
            yield return Path.Combine(baseDir, "runtimes", rid, "native", name);
            yield return Path.Combine(baseDir, "runtimes", $"{osRid}-{arch}", "native", name);
        }
    }

    // ---- constants (vpx_codec.h, vpx_encoder.h, vpx_image.h, vp8cx.h) ----

    public const int VpxCodecOk = 0;
    public const int VpxCodecAbiMismatch = 3;

    /// <summary>VPX_IMG_FMT_PLANAR (0x100) | 2.</summary>
    public const int VpxImgFmtI420 = 0x100 | 2;

    public const uint VpxErrorResilientDefault = 0x1;
    public const int VpxVbr = 0;
    public const int VpxCbr = 1;
    public const int VpxKfFixed = 0;
    public const int VpxKfAuto = 1;
    public const int VpxKfDisabled = 0;
    public const uint VpxFrameIsKey = 0x1;
    public const int VpxCodecCxFramePkt = 0;

    public const int Vp8eSetCpuUsed = 13;
    public const int Vp8eSetStaticThreshold = 17;
    public const int Vp9eSetTileColumns = 33;
    public const int Vp9eSetAqMode = 36;
    public const int Vp9eSetTuneContent = 43;
    public const int Vp9eSetColorSpace = 46;
    public const int Vp9eSetRowMt = 55;

    /// <summary>VPX_CS_BT_601: the matrix <c>PixelConversion</c> converts with. The range is left at the default, studio.</summary>
    public const int VpxCsBt601 = 1;

    /// <summary>VP9E_CONTENT_SCREEN: what a desk session is, and what the encoder should be told it is.</summary>
    public const int Vp9ContentScreen = 1;

    /// <summary>VPX_DL_REALTIME. Declared unsigned long in C, hence <see cref="CULong"/> at the call.</summary>
    public const uint VpxDlRealtime = 1;

    /// <summary>
    /// The ABI versions this binding was written against (libvpx 1.15.2): VPX_IMAGE_ABI_VERSION 5, so
    /// VPX_CODEC_ABI_VERSION is 9, VPX_DECODER_ABI_VERSION 12, and VPX_ENCODER_ABI_VERSION
    /// 18 + 9 + VPX_EXT_RATECTRL_ABI_VERSION (10) = 37. These are compile-time constants in C that move with
    /// the library, and a wrong one is rejected cleanly with VPX_CODEC_ABI_MISMATCH rather than crashing, so
    /// the callers walk a small range around these instead of insisting on one build.
    /// </summary>
    public const int EncoderAbiVersion = 37;

    public const int DecoderAbiVersion = 12;

    // ---- struct layout ----

    /// <summary>
    /// Offsets into <c>vpx_codec_enc_cfg_t</c>. The struct is written by
    /// <c>vpx_codec_enc_config_default</c> and only a handful of fields are then changed, so the binding
    /// holds a zeroed buffer and reaches in by offset rather than mirroring 60 fields. The tail of the struct
    /// (the vizier rate-control rationals) has grown between releases; the prefix up to kf_max_dist has not
    /// moved since libvpx 1.8, and nothing here touches anything past it. <see cref="EncCfg.Disagreement"/>
    /// checks the layout against the known defaults before a single field is written.
    /// </summary>
    public static class EncCfg
    {
        /// <summary>Comfortably larger than the 496 bytes libvpx 1.15 uses, so a future field cannot overrun.</summary>
        public const int Size = 1024;

        public const int GUsage = 0;
        public const int GThreads = 4;
        public const int GProfile = 8;
        public const int GW = 12;
        public const int GH = 16;
        public const int GBitDepth = 20;
        public const int GInputBitDepth = 24;
        public const int GTimebaseNum = 28;
        public const int GTimebaseDen = 32;
        public const int GErrorResilient = 36;
        public const int GPass = 40;
        public const int GLagInFrames = 44;
        public const int RcDropframeThresh = 48;
        public const int RcResizeAllowed = 52;
        public const int RcScaledWidth = 56;
        public const int RcScaledHeight = 60;
        public const int RcResizeUpThresh = 64;
        public const int RcResizeDownThresh = 68;
        public const int RcEndUsage = 72;

        // 76 is padding: the two vpx_fixed_buf_t that follow hold a pointer and a size_t, so they are
        // 8-aligned, and occupy 80..111.
        public const int RcTargetBitrate = 112;
        public const int RcMinQuantizer = 116;
        public const int RcMaxQuantizer = 120;
        public const int RcUndershootPct = 124;
        public const int RcOvershootPct = 128;
        public const int RcBufSz = 132;
        public const int RcBufInitialSz = 136;
        public const int RcBufOptimalSz = 140;
        public const int Rc2PassVbrMaxsectionPct = 152;
        public const int KfMode = 160;
        public const int KfMinDist = 164;
        public const int KfMaxDist = 168;

        /// <summary>
        /// Which of libvpx's own defaults did not land where this binding expects it, or null when they all
        /// did. Distinctive values spread across the whole prefix, including past the 8-aligned hole at
        /// offset 76: if the struct changes shape, this names the field instead of letting the encoder
        /// quietly configure the wrong one.
        /// </summary>
        public static string? Disagreement(ReadOnlySpan<byte> cfg)
        {
            // vp9_cx_iface.c's encoder_usage_cfg_map[0], which is what enc_config_default just copied.
            (string Name, int Offset, uint Expected)[] known =
            [
                ("g_w", GW, 320),
                ("g_h", GH, 240),
                ("g_timebase.den", GTimebaseDen, 30),
                ("g_lag_in_frames", GLagInFrames, 25),
                ("rc_target_bitrate", RcTargetBitrate, 256),
                ("rc_max_quantizer", RcMaxQuantizer, 63),
                ("rc_buf_sz", RcBufSz, 6000),
                ("rc_2pass_vbr_maxsection_pct", Rc2PassVbrMaxsectionPct, 2000),
                ("kf_max_dist", KfMaxDist, 128),
            ];

            foreach ((string name, int offset, uint expected) in known)
            {
                uint actual = Get(cfg, offset);
                if (actual != expected)
                {
                    return $"{name} at +{offset} is {actual}, expected {expected}";
                }
            }

            return null;
        }

        public static uint Get(ReadOnlySpan<byte> cfg, int offset) =>
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(cfg[offset..]);

        public static void Set(Span<byte> cfg, int offset, uint value) =>
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(cfg[offset..], value);
    }

    /// <summary>
    /// Offsets into <c>vpx_codec_cx_pkt_t</c>. The union member before <c>flags</c> is an
    /// <c>unsigned long duration</c>, which is four bytes on Windows and eight everywhere else, so the one
    /// field that matters most — whether this packet is a keyframe — sits at a different offset per platform.
    /// Reading it at the wrong one returns the low half of a partition id and every frame looks like a delta.
    /// </summary>
    public static class CxPkt
    {
        public const int Kind = 0;
        public const int FrameBuf = 8;
        public const int FrameSz = 16;
        public const int FramePts = 24;
        public const int FrameDuration = 32;

        public static int FrameFlags => OperatingSystem.IsWindows() ? 36 : 40;
    }

    /// <summary>
    /// Offsets into <c>vpx_image_t</c>; every field before the planes is a four-byte int or enum. The encoder
    /// fills one of these in by hand rather than calling <c>vpx_img_wrap</c>, because wrap derives the chroma
    /// plane offsets with <c>h &gt;&gt; y_chroma_shift</c> while everything else in libvpx — and this project's
    /// own I420 buffers — sizes them with <c>(h + 1) / 2</c>. The two agree on every even height and differ by
    /// one chroma row on every odd one, which would read the V plane a row early.
    /// </summary>
    public static class Img
    {
        /// <summary>Bigger than the 136 bytes libvpx uses, so the encoder can keep one on the stack.</summary>
        public const int Size = 192;

        public const int Fmt = 0;
        public const int W = 12;
        public const int H = 16;
        public const int BitDepth = 20;
        public const int DW = 24;
        public const int DH = 28;
        public const int XChromaShift = 40;
        public const int YChromaShift = 44;
        public const int Planes = 48;
        public const int Stride = 80;
        public const int Bps = 96;
        public const int ImgData = 112;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VpxCodecCtx
    {
        public nint Name;
        public nint Iface;
        public int Err;
        private readonly int _pad;
        public nint ErrDetail;
        public nint InitFlags;
        public nint Config;
        public nint Priv;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VpxCodecDecCfg
    {
        public uint Threads;
        public uint W;
        public uint H;
    }

    // ---- C entry points ----

    [LibraryImport(LibraryName)]
    public static partial int vpx_codec_version();

    [LibraryImport(LibraryName)]
    public static partial nint vpx_codec_version_str();

    [LibraryImport(LibraryName)]
    public static partial nint vpx_codec_err_to_string(int err);

    [LibraryImport(LibraryName)]
    public static partial nint vpx_codec_vp9_cx();

    [LibraryImport(LibraryName)]
    public static partial nint vpx_codec_vp9_dx();

    [LibraryImport(LibraryName)]
    public static partial int vpx_codec_enc_config_default(nint iface, byte* cfg, uint usage);

    [LibraryImport(LibraryName)]
    public static partial int vpx_codec_enc_init_ver(VpxCodecCtx* ctx, nint iface, byte* cfg, CLong flags, int version);

    [LibraryImport(LibraryName)]
    public static partial int vpx_codec_enc_config_set(VpxCodecCtx* ctx, byte* cfg);

    [LibraryImport(LibraryName)]
    public static partial int vpx_codec_dec_init_ver(VpxCodecCtx* ctx, nint iface, VpxCodecDecCfg* cfg, CLong flags, int version);

    /// <summary>
    /// Declared variadic in C (<c>vpx_codec_control_(ctx, id, ...)</c>). Every control this binding uses
    /// takes a single int, which arrives in an integer register under both the Windows and the System V
    /// x86-64 conventions, so a fixed-signature call reaches it correctly. A control taking a pointer or a
    /// struct would need more care than this.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial int vpx_codec_control_(VpxCodecCtx* ctx, int ctrlId, int value);

    [LibraryImport(LibraryName)]
    public static partial int vpx_codec_encode(VpxCodecCtx* ctx, byte* img, long pts, CULong duration, CLong flags, CULong deadline);

    [LibraryImport(LibraryName)]
    public static partial byte* vpx_codec_get_cx_data(VpxCodecCtx* ctx, nint* iter);

    [LibraryImport(LibraryName)]
    public static partial int vpx_codec_decode(VpxCodecCtx* ctx, byte* data, uint dataSize, nint userPriv, CLong deadline);

    [LibraryImport(LibraryName)]
    public static partial byte* vpx_codec_get_frame(VpxCodecCtx* ctx, nint* iter);

    [LibraryImport(LibraryName)]
    public static partial int vpx_codec_destroy(VpxCodecCtx* ctx);

    public static string Describe(int err)
    {
        try
        {
            return Marshal.PtrToStringUTF8(vpx_codec_err_to_string(err)) ?? $"error {err}";
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return $"error {err}";
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Check(int err, string what)
    {
        if (err != VpxCodecOk)
        {
            throw new InvalidOperationException($"{what} failed: {Describe(err)} ({err}).");
        }
    }
}
