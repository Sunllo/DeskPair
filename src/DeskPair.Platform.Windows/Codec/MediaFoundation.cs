using System.Runtime.InteropServices;
using DeskPair.Platform.Abstractions.Codec;
using Vortice.MediaFoundation;

namespace DeskPair.Platform.Windows.Codec;

#pragma warning disable SYSLIB1054

/// <summary>Media Foundation lifetime, COM activation and the attribute GUIDs the codecs need.</summary>
internal static partial class MediaFoundation
{
    private static readonly object Lock = new();
    private static int _refs;

    public static readonly Guid H264EncoderClsid = new("6CA50344-051A-4DED-9779-A43305165E35");   // CMSH264EncoderMFT
    public static readonly Guid H264DecoderClsid = new("62CE7E72-4C71-4D20-B15D-452831A87D9D");   // CMSH264DecoderMFT
    public static readonly Guid IMFTransformIid = new("BF94C121-5B05-4E6F-8000-BA598961414D");

    /// <summary>
    /// Whose silicon is behind an MFT, read from its friendly name. Media Foundation offers no vendor field,
    /// and the names are the vendors' own ("NVIDIA H.264 Encoder MFT", "Intel Quick Sync Video H.264 Encoder",
    /// "AMD H.264 Hardware MFT Encoder"), so this is the available signal. Unknown is a fine answer.
    /// </summary>
    public static GpuVendor VendorOf(string mftName)
    {
        if (mftName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
        {
            return GpuVendor.Nvidia;
        }

        if (mftName.Contains("Intel", StringComparison.OrdinalIgnoreCase) || mftName.Contains("Quick Sync", StringComparison.OrdinalIgnoreCase))
        {
            return GpuVendor.Intel;
        }

        if (mftName.Contains("AMD", StringComparison.OrdinalIgnoreCase) || mftName.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
        {
            return GpuVendor.Amd;
        }

        return GpuVendor.Unknown;
    }

    public static readonly Guid MediaTypeVideo = new("73646976-0000-0010-8000-00AA00389B71");
    public static readonly Guid FormatH264 = new("34363248-0000-0010-8000-00AA00389B71");
    public static readonly Guid FormatNv12 = new("3231564E-0000-0010-8000-00AA00389B71");

    /// <summary>VP9 ('VP90'), for probing whether this machine offers a royalty-free codec of its own.</summary>
    public static readonly Guid FormatVp90 = new("30395056-0000-0010-8000-00AA00389B71");

    /// <summary>VP8 ('VP80').</summary>
    public static readonly Guid FormatVp80 = new("30385056-0000-0010-8000-00AA00389B71");

    /// <summary>HEVC ('HEVC'), the subtype every vendor's H.265 MFT registers under.</summary>
    public static readonly Guid FormatHevc = new("43564548-0000-0010-8000-00AA00389B71");

    /// <summary>AV1 ('AV01').</summary>
    public static readonly Guid FormatAv1 = new("31305641-0000-0010-8000-00AA00389B71");

    /// <summary>
    /// The Media Foundation subtype for a codec, or null when Media Foundation has no place to put it.
    /// VP8 and VP9 have subtypes but no encoder that will accept our output type, so they are decode-only
    /// here; see the VP9 note in <c>docs/architecture.md</c>.
    /// </summary>
    public static Guid? SubtypeOf(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => FormatH264,
        VideoCodec.H265 => FormatHevc,
        VideoCodec.Av1 => FormatAv1,
        VideoCodec.Vp8 => FormatVp80,
        VideoCodec.Vp9 => FormatVp90,
        _ => null,
    };

    public static readonly Guid MtMajorType = new("48EBA18E-F8C9-4687-BF11-0A74C9F96A8F");
    public static readonly Guid MtSubtype = new("F7E34C9A-42E8-4714-B74B-CB29D72C35E5");
    public static readonly Guid MtFrameSize = new("1652C33D-D6B2-4012-B834-72030849A37D");
    public static readonly Guid MtFrameRate = new("C459A2E8-3D2C-4E44-B132-FEE5156C7BB0");
    public static readonly Guid MtAvgBitrate = new("20332580-3B70-4DBD-B5D3-4A23AFA4BA98");
    public static readonly Guid MtInterlaceMode = new("E2724BB8-E676-4806-B4B2-A8D6EFB44CCD");
    public static readonly Guid MtPixelAspectRatio = new("C6376A1E-8D0A-4027-BE45-6D9A0AD39BB6");
    public static readonly Guid MtMpeg2Profile = new("AD76A80B-2D5C-4E0B-B375-64E520137036");
    public static readonly Guid MtDefaultStride = new("644B4E48-1E02-4516-B0EB-C01CA9D49AC6");

    // Audio and container attributes, used when writing a recording.
    public static readonly Guid MediaTypeAudio = new("73647561-0000-0010-8000-00AA00389B71");
    public static readonly Guid FormatPcm = new("00000001-0000-0010-8000-00AA00389B71");
    public static readonly Guid FormatAac = new("00001610-0000-0010-8000-00AA00389B71");
    public static readonly Guid MtAudioSamplesPerSecond = new("5FAEEAE7-0290-4C31-9E8A-C534F68D9DBA");
    public static readonly Guid MtAudioNumChannels = new("37E48BF5-645E-4C5B-89DE-ADA9E29B696A");
    public static readonly Guid MtAudioBitsPerSample = new("F2DEB57F-40FA-4764-AA33-ED4F2D1FF669");
    public static readonly Guid MtAudioBlockAlignment = new("322DE230-9EEB-43BD-AB7A-FF412251541D");
    public static readonly Guid MtAudioAvgBytesPerSecond = new("1AAB75C8-CFEF-451C-AB95-AC034B8E1731");
    public static readonly Guid MtAacPayloadType = new("BFBABE79-7434-4D1C-94F0-72A3B9E17188");
    public static readonly Guid MtAacProfileLevel = new("9AA7E155-B64A-4C1D-A500-455D600B6560");

    /// <summary>MF_TRANSCODE_CONTAINERTYPE, set on the sink writer's attributes.</summary>
    public static readonly Guid TranscodeContainerType = new("150FF23F-4ABC-478B-AC4F-E1916FBA1CCA");

    /// <summary>MFTranscodeContainerType_MPEG4.</summary>
    public static readonly Guid ContainerTypeMpeg4 = new("DC6CD05D-B9D0-40EF-BD35-FA622C1AB28A");
    public static readonly Guid MtAllSamplesIndependent = new("C9173739-5E56-461C-B713-46FB995CB95F");
    public static readonly Guid SampleCleanPoint = new("9CDF01D8-A0F0-43BA-B077-EAA06CBD728A");

    public static readonly Guid CodecApiRateControlMode = new("1C0608E9-370C-4710-8A58-CB6181C42423");
    public static readonly Guid CodecApiMeanBitRate = new("F7222374-2144-4815-B550-A37F8E12EE52");
    public static readonly Guid CodecApiLowLatency = new("9D3ECD55-89E8-490A-970A-0C9548D5A56E");
    public static readonly Guid CodecApiGopSize = new("95F31B26-95A4-41AA-9303-246A7FC6EEF1");
    public static readonly Guid CodecApiForceKeyFrame = new("398C1B98-8353-475A-9EF2-8F265D260345");
    public static readonly Guid CodecApiLowLatencyMode = new("9C27891A-ED7A-40E1-88E8-B22727A024EE"); // == MF_LOW_LATENCY
    public static readonly Guid CodecApiDefaultBPictureCount = new("8D390AAC-DC5C-4200-B57F-814D04BABA7A");
    public static readonly Guid CodecApiMaxNumRefFrame = new("964829ED-94F9-43B4-B74D-EF40944B69A0");
    public static readonly Guid CodecApiQualityVsSpeed = new("98332DF8-03CD-476B-89FA-3F9E442DEC9F");

    /// <summary>CODECAPI_AVEncCommonMaxBitRate (UINT32, bits per second).</summary>
    public static readonly Guid CodecApiMaxBitRate = new("9651EAE4-39B9-4EBF-85EF-D7F444EC7465");

    /// <summary>CODECAPI_AVEncCommonBufferSize (UINT32): the rate-control (VBV/HRD) buffer, in bits.</summary>
    public static readonly Guid CodecApiBufferSize = new("0DB96574-B6A4-4C8B-8106-3773DE0310CD");

    public const uint InterlaceProgressive = 2;
    public const uint H264ProfileBase = 66;
    public const uint H264ProfileMain = 77;
    public const uint H264ProfileHigh = 100;

    /// <summary>eAVEncH265VProfile_Main_420_8. H.265 numbers its profiles from 1, not from H.264's scale.</summary>
    public const uint H265ProfileMain = 1;

    /// <summary>
    /// The profile to ask for, on the one attribute both codecs share. AV1 has no equivalent we need to set:
    /// its MFTs pick Main themselves, and naming a profile there is a way to be refused for no gain.
    /// </summary>
    public static uint? ProfileOf(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => H264ProfileHigh, // 8x8 transform + CABAC: same bits, sharper desktop
        VideoCodec.H265 => H265ProfileMain,
        _ => null,
    };
    public const uint RateControlCbr = 0;

    private const uint CLSCTX_INPROC_SERVER = 0x1;

    public static void AddRef()
    {
        lock (Lock)
        {
            if (_refs++ == 0)
            {
                MediaFactory.MFStartup(true).CheckError();
            }
        }
    }

    public static void Release()
    {
        lock (Lock)
        {
            if (--_refs == 0)
            {
                MediaFactory.MFShutdown();
            }
        }
    }

    public static IMFTransform CreateTransform(Guid clsid)
    {
        int hr = CoCreateInstance(clsid, 0, CLSCTX_INPROC_SERVER, IMFTransformIid, out nint ptr);
        Marshal.ThrowExceptionForHR(hr);
        return new IMFTransform(ptr);
    }

    public static ulong Pack(uint high, uint low) => ((ulong)high << 32) | low;

    /// <summary>Copies a managed span into a fresh MF memory buffer wrapped in a sample.</summary>
    public static IMFSample CreateSample(ReadOnlySpan<byte> data, long time, long duration)
    {
        IMFSample sample = MediaFactory.MFCreateSample();
        IMFMediaBuffer buffer = MediaFactory.MFCreateMemoryBuffer(Math.Max(data.Length, 1));
        try
        {
            buffer.Lock(out nint ptr, out _, out _);
            try
            {
                unsafe
                {
                    data.CopyTo(new Span<byte>((void*)ptr, data.Length));
                }
            }
            finally
            {
                buffer.Unlock();
            }

            buffer.CurrentLength = data.Length;
            sample.AddBuffer(buffer);
            sample.SampleTime = time;
            sample.SampleDuration = duration;
            return sample;
        }
        finally
        {
            buffer.Dispose(); // the sample holds its own reference
        }
    }

    /// <summary>A sample with one memory buffer of fixed capacity that is refilled every frame instead of reallocated.</summary>
    public sealed class ReusableSample : IDisposable
    {
        private readonly IMFMediaBuffer _buffer;

        public ReusableSample(int capacity)
        {
            Capacity = Math.Max(capacity, 1);
            Sample = MediaFactory.MFCreateSample();
            _buffer = MediaFactory.MFCreateMemoryBuffer(Capacity);
            Sample.AddBuffer(_buffer);
        }

        public IMFSample Sample { get; }

        public int Capacity { get; }

        public void Fill(ReadOnlySpan<byte> data, long time, long duration)
        {
            if (data.Length > Capacity)
            {
                throw new ArgumentException($"Sample holds {Capacity} bytes, got {data.Length}.", nameof(data));
            }

            _buffer.Lock(out nint ptr, out _, out _);
            try
            {
                unsafe
                {
                    data.CopyTo(new Span<byte>((void*)ptr, data.Length));
                }
            }
            finally
            {
                _buffer.Unlock();
            }

            _buffer.CurrentLength = data.Length;
            Sample.SampleTime = time;
            Sample.SampleDuration = duration;
        }

        /// <summary>Resets the payload length so the sample can receive encoder/decoder output.</summary>
        public void Clear() => _buffer.CurrentLength = 0;

        public void Dispose()
        {
            _buffer.Dispose();
            Sample.Dispose();
        }
    }

    /// <summary>Appends the sample's contiguous payload to <paramref name="target"/>, growing it as needed.</summary>
    public static int CopyOut(IMFSample sample, ref byte[] target, int offset)
    {
        using IMFMediaBuffer contiguous = sample.ConvertToContiguousBuffer();
        contiguous.Lock(out nint ptr, out _, out int length);
        try
        {
            if (target.Length < offset + length)
            {
                Array.Resize(ref target, Math.Max(target.Length * 2, offset + length));
            }

            unsafe
            {
                new ReadOnlySpan<byte>((void*)ptr, length).CopyTo(target.AsSpan(offset));
            }

            return length;
        }
        finally
        {
            contiguous.Unlock();
        }
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid rclsid, nint pUnkOuter, uint dwClsContext, in Guid riid, out nint ppv);
}

/// <summary>Minimal ICodecAPI (vtable order matters) used for rate control and keyframe requests.</summary>
[ComImport]
[Guid("901DB4C7-31CE-41A2-85DC-8FA0BF41B8DA")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICodecAPI
{
    [PreserveSig]
    int IsSupported(in Guid api);

    [PreserveSig]
    int IsModifiable(in Guid api);

    [PreserveSig]
    int GetParameterRange(in Guid api, [MarshalAs(UnmanagedType.Struct)] out object valueMin, [MarshalAs(UnmanagedType.Struct)] out object valueMax, [MarshalAs(UnmanagedType.Struct)] out object steppingDelta);

    [PreserveSig]
    int GetParameterValues(in Guid api, out nint values, out uint valuesCount);

    [PreserveSig]
    int GetDefaultValue(in Guid api, [MarshalAs(UnmanagedType.Struct)] out object value);

    [PreserveSig]
    int GetValue(in Guid api, [MarshalAs(UnmanagedType.Struct)] out object value);

    [PreserveSig]
    int SetValue(in Guid api, [MarshalAs(UnmanagedType.Struct)] in object value);
}

internal static class CodecApiExtensions
{
    public static ICodecAPI? TryGetCodecApi(this IMFTransform transform)
    {
        try
        {
            return (ICodecAPI)Marshal.GetTypedObjectForIUnknown(transform.NativePointer, typeof(ICodecAPI));
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static bool TrySet(this ICodecAPI api, Guid key, object value) => api.SetValue(key, value) >= 0;
}
