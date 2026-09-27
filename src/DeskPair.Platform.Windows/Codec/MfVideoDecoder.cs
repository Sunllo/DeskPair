using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using SharpGen.Runtime;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Abstractions.Codec;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

namespace DeskPair.Platform.Windows.Codec;

public sealed class MfVideoDecoderFactory : IVideoDecoderFactory
{
    private readonly ILoggerFactory _logs;
    private SupportedCodecs? _probed;

    public MfVideoDecoderFactory(ILoggerFactory logs)
    {
        _logs = logs;
    }

    /// <summary>Hardware (DXVA through D3D11) decoding is attempted first and falls back to the software MFT per decoder.</summary>
    public bool PreferHardware { get; init; } = true;

    /// <summary>
    /// The codecs worth asking about. A decoder is useless without an encoder on the far side, so this is the
    /// same set the encoder factory offers.
    /// </summary>
    private static readonly VideoCodec[] Codecs = [VideoCodec.H264, VideoCodec.H265, VideoCodec.Av1];

    /// <summary>
    /// Asks Media Foundation what is actually registered rather than assuming. Windows N and KN carry no
    /// H.264 decoder until the Media Feature Pack is installed, and claiming one there sends the caller into
    /// a constructor that throws instead of letting it choose another codec. The same holds per codec: H.265
    /// needs the HEVC Video Extensions or a vendor MFT, and AV1 needs silicon that has it.
    /// </summary>
    public SupportedCodecs Probe()
    {
        if (_probed is null)
        {
            MediaFoundation.AddRef();
            try
            {
                SupportedCodecs found = SupportedCodecs.None;
                foreach (VideoCodec codec in Codecs)
                {
                    if (MfDecoderCandidate.Enumerate(codec).Count > 0)
                    {
                        found |= SupportedCodecsExtensions.Bit(codec, hardware: false);
                        if (PreferHardware)
                        {
                            found |= SupportedCodecsExtensions.Bit(codec, hardware: true);
                        }
                    }
                }

                _probed = found;
            }
            finally
            {
                MediaFoundation.Release();
            }
        }

        return _probed.Value;
    }

    public IVideoDecoder Create(VideoCodec codec, GpuApi preferredOutput, long adapterLuid) => Codecs.Contains(codec)
        ? new MfVideoDecoder(codec, _logs.CreateLogger<MfVideoDecoder>(), PreferHardware, adapterLuid)
        : throw new NotSupportedException($"{codec} is not supported by the Media Foundation decoder.");
}

/// <summary>
/// A registered decoder MFT that can be activated. The decoder used to reach straight for the Microsoft
/// H.264 CLSID, which works for H.264 and for nothing else: H.265 and AV1 arrive as vendor MFTs or as a
/// store-installed extension, with no CLSID anyone could name ahead of time. Enumerating asks the machine.
/// </summary>
internal sealed record MfDecoderCandidate(string Name, bool IsHardware, Func<IMFTransform> Activate)
{
    private static readonly Guid FriendlyName = new("314FFBAE-5B41-4C95-9C19-4E7D586FACE3");
    private static readonly Guid HardwareUrl = new("2FB866AC-B078-4942-AB6C-003D05CDA674");
    private const uint EnumSync = 0x1, EnumAsync = 0x2, EnumHardware = 0x4, EnumSortAndFilter = 0x40;

    /// <summary>Every decoder registered for a codec, hardware first, plus the Microsoft H.264 CLSID last.</summary>
    public static List<MfDecoderCandidate> Enumerate(VideoCodec codec)
    {
        var list = new List<MfDecoderCandidate>();
        if (MediaFoundation.SubtypeOf(codec) is not { } subtype)
        {
            return list;
        }

        try
        {
            var input = new RegisterTypeInfo { GuidMajorType = MediaFoundation.MediaTypeVideo, GuidSubtype = subtype };
            MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoDecoder, EnumSync | EnumAsync | EnumHardware | EnumSortAndFilter, input, null, out nint activates, out uint count);
            try
            {
                for (uint i = 0; i < count; i++)
                {
                    nint ptr = Marshal.ReadIntPtr(activates, (int)(i * nint.Size));
                    if (ptr == 0)
                    {
                        continue;
                    }

                    var activate = new IMFActivate(ptr);
                    string name = $"{codec} decoder";
                    bool hardware = false;
                    try
                    {
                        name = activate.GetString(FriendlyName);
                    }
                    catch (SharpGenException)
                    {
                    }

                    try
                    {
                        hardware = activate.GetString(HardwareUrl).Length > 0;
                    }
                    catch (SharpGenException)
                    {
                    }

                    list.Add(new MfDecoderCandidate(name, hardware, () =>
                    {
                        activate.ActivateObject(MediaFoundation.IMFTransformIid, out IMFTransform? transform).CheckError();
                        return transform!;
                    }));
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(activates);
            }
        }
        catch (SharpGenException)
        {
            // Enumeration unavailable; fall through to the CLSID below.
        }

        if (codec == VideoCodec.H264)
        {
            list.Add(new MfDecoderCandidate("Microsoft H264 Decoder MFT (CLSID)", false, () => MediaFoundation.CreateTransform(MediaFoundation.H264DecoderClsid)));
        }

        return list.OrderBy(c => c.IsHardware ? 0 : 1).ToList();
    }
}

/// <summary>
/// One codec through the first Media Foundation decoder MFT that accepts it. With a D3D11 device manager
/// attached the MFT decodes on the GPU (DXVA) and hands back NV12 textures, which are read back through a
/// staging texture and converted to BGRA with the vector kernels; without one the MFT decodes on the CPU into
/// system memory. Output is BGRA for the CPU rendering path either way.
/// </summary>
public sealed class MfVideoDecoder : IVideoDecoder
{
    private static readonly Guid SaD3D11Aware = new("206B4FC8-FCF9-4C51-AFE3-9764369E33A0"); // MF_SA_D3D11_AWARE

    private readonly VideoCodec _codec;
    private readonly ILogger _log;
    private readonly IMFTransform _transform;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IMFDXGIDeviceManager? _manager;
    private ID3D11Texture2D? _staging;
    private MediaFoundation.ReusableSample? _output;
    private byte[] _nv12 = [];
    private byte[] _bgra = [];
    private byte[] _tight = [];
    private int _width;
    private int _height;
    private int _codedHeight;
    private int _offsetX;
    private int _offsetY;
    private int _stride;
    private bool _streaming;
    private long _frameIndex;
    private bool _disposed;

    public MfVideoDecoder(VideoCodec codec, ILogger log, bool preferHardware = true, long adapterLuid = 0)
    {
        _codec = codec;
        _log = log;
        MediaFoundation.AddRef();
        Exception? last = null;
        foreach (MfDecoderCandidate candidate in MfDecoderCandidate.Enumerate(codec))
        {
            try
            {
                _transform = candidate.Activate();
            }
            catch (Exception e) when (e is SharpGenException or COMException or InvalidOperationException)
            {
                _log.LogDebug(e, "Decoder {Name} could not be activated", candidate.Name);
                last = e;
                continue;
            }

            try
            {
                try
                {
                    using IMFAttributes attributes = _transform.Attributes;
                    attributes.Set(MediaFoundation.CodecApiLowLatencyMode, 1u);
                    if (preferHardware && attributes.GetUInt32(SaD3D11Aware) == 1)
                    {
                        IsHardware = TryAttachD3D11(adapterLuid);
                    }
                }
                catch (Exception)
                {
                }

                using (IMFMediaType input = MediaFactory.MFCreateMediaType())
                {
                    input.Set(MediaFoundation.MtMajorType, MediaFoundation.MediaTypeVideo);
                    input.Set(MediaFoundation.MtSubtype, MediaFoundation.SubtypeOf(codec)!.Value);

                    // A nominal size, because the real one is in the stream and we have not seen it yet. The
                    // H.264 MFT invents 1920x1080 when the input type carries no size, but the H.265 and AV1
                    // ones do not: they then offer an output type that SetOutputType rejects, which reads as
                    // "no decoder for this codec" when the truth is "you did not say how big". The first
                    // keyframe corrects it through MF_E_TRANSFORM_STREAM_CHANGE, which re-runs SelectOutputType.
                    input.Set(MediaFoundation.MtFrameSize, MediaFoundation.Pack(1920, 1080));
                    input.Set(MediaFoundation.MtInterlaceMode, MediaFoundation.InterlaceProgressive);
                    _transform.SetInputType(0, input, 0);
                }

                SelectOutputType();
                _log.LogInformation("{Codec} decoder: {Name} ({Mode})", codec, candidate.Name, IsHardware ? "DXVA/D3D11" : "software");
                return;
            }
            catch (Exception e) when (e is SharpGenException or COMException or InvalidOperationException or NotSupportedException)
            {
                _log.LogDebug(e, "Decoder {Name} rejected {Codec}", candidate.Name, codec);
                last = e;
                DisposeD3D();
                _transform.Dispose();
                _transform = null!;
            }
        }

        MediaFoundation.Release();
        throw new NotSupportedException($"No Media Foundation {codec} decoder accepted the stream.", last);
    }

    public VideoCodec Codec => _codec;

    public bool OutputsGpuSurface => false;

    /// <summary>True when the MFT decodes through DXVA on a D3D11 device.</summary>
    public bool IsHardware { get; }

    public bool TryDecode(ReadOnlySpan<byte> packet, bool isKeyFrame, out DecodedFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        frame = default;
        if (!_streaming)
        {
            _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, 0);
            _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, 0);
            _streaming = true;
        }

        // The decoder keeps input samples until it has consumed them, so each packet gets its own (small) sample.
        const long Duration = 10_000_000L / 60;
        using (IMFSample sample = MediaFoundation.CreateSample(packet, _frameIndex++ * Duration, Duration))
        {
            _transform.ProcessInput(0, sample, 0);
        }

        bool got = false;
        while (true)
        {
            OutputStreamInfo info = _transform.GetOutputStreamInfo(0);
            bool providesSamples = ((OutputStreamInfoFlags)info.Flags & (OutputStreamInfoFlags.OutputStreamProvidesSamples | OutputStreamInfoFlags.OutputStreamCanProvideSamples)) != 0;
            IMFSample? outSample = null;
            if (!providesSamples)
            {
                int capacity = Math.Max(info.Size, 1 << 16);
                if (_output is null || _output.Capacity < capacity)
                {
                    _output?.Dispose();
                    _output = new MediaFoundation.ReusableSample(capacity);
                }

                _output.Clear();
                outSample = _output.Sample;
            }

            var data = new OutputDataBuffer { StreamID = 0, Sample = outSample };
            Result hr = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref data, out _);
            if (hr == Vortice.MediaFoundation.ResultCode.TransformNeedMoreInput)
            {
                break;
            }

            if (hr == Vortice.MediaFoundation.ResultCode.TransformStreamChange)
            {
                SelectOutputType();
                continue;
            }

            hr.CheckError();
            // Our own sample is not AddRef'd by ProcessOutput: the wrapper in data.Sample must not be released.
            IMFSample produced = outSample ?? data.Sample ?? throw new InvalidOperationException("Decoder produced no sample.");
            try
            {
                got = IsHardware ? ReadBackTexture(produced) : ReadBackMemory(produced);
            }
            finally
            {
                if (outSample is null)
                {
                    produced.Dispose();
                }

                data.Events?.Dispose();
            }
        }

        if (!got)
        {
            return false;
        }

        frame = new DecodedFrame { Width = _width, Height = _height, Format = PixelFormat.Bgra32, Cpu = new ReadOnlyMemory<byte>(_bgra, 0, _width * 4 * _height), Stride = _width * 4 };
        return true;
    }

    // ---- software path: NV12 in system memory ----

    private bool ReadBackMemory(IMFSample produced)
    {
        int need = PixelConversion.Nv12Size(_stride, _codedHeight);
        if (_nv12.Length < need)
        {
            _nv12 = new byte[need];
        }

        MediaFoundation.CopyOut(produced, ref _nv12, 0);
        EnsureBgra();
        int yOffset = _offsetY * _stride + _offsetX;
        int uvOffset = _stride * _codedHeight + (_offsetY / 2) * _stride + (_offsetX & ~1);
        if (yOffset == 0)
        {
            PixelConversion.Nv12ToBgra(_nv12, _stride, _stride * _codedHeight, _width, _height, _bgra, _width * 4);
        }
        else
        {
            CroppedNv12ToBgra(_nv12, _stride, yOffset, uvOffset);
        }

        return true;
    }

    // ---- hardware path: NV12 texture -> staging -> BGRA ----

    private unsafe bool ReadBackTexture(IMFSample produced)
    {
        using IMFMediaBuffer buffer = produced.GetBufferByIndex(0);
        using IMFDXGIBuffer dxgi = buffer.QueryInterface<IMFDXGIBuffer>();
        using ID3D11Texture2D texture = new(dxgi.GetResource(typeof(ID3D11Texture2D).GUID));
        uint subresource = dxgi.SubresourceIndex;
        Texture2DDescription desc = texture.Description;
        EnsureStaging(desc);
        _context!.CopySubresourceRegion(_staging!, 0, 0, 0, 0, texture, subresource);
        MappedSubresource map = _context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            int rowPitch = (int)map.RowPitch;
            int codedHeight = (int)desc.Height;
            var mapped = new ReadOnlySpan<byte>((void*)map.DataPointer, rowPitch * (codedHeight + (codedHeight + 1) / 2));
            EnsureBgra();
            int yOffset = _offsetY * rowPitch + _offsetX;
            int uvOffset = rowPitch * codedHeight + (_offsetY / 2) * rowPitch + (_offsetX & ~1);
            if (yOffset == 0)
            {
                // Convert straight out of the mapped staging memory: one sequential read, no intermediate copy.
                PixelConversion.Nv12ToBgra(mapped, rowPitch, uvOffset, _width, _height, _bgra, _width * 4);
            }
            else
            {
                CroppedNv12ToBgra(mapped, rowPitch, yOffset, uvOffset);
            }
        }
        finally
        {
            _context.Unmap(_staging!, 0);
        }

        return true;
    }

    private void EnsureStaging(Texture2DDescription source)
    {
        if (_staging is not null && _staging.Description.Width == source.Width && _staging.Description.Height == source.Height && _staging.Description.Format == source.Format)
        {
            return;
        }

        _staging?.Dispose();
        _staging = _device!.CreateTexture2D(new Texture2DDescription
        {
            Width = source.Width,
            Height = source.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = source.Format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
            BindFlags = BindFlags.None,
        });
    }

    /// <summary>
    /// Attaches a D3D11 device so the MFT can decode through DXVA. The adapter matters: on a machine with more
    /// than one GPU, <c>DriverType.Hardware</c> with no adapter lets D3D11 pick, which need not be the GPU the
    /// frames came from. The LUID travels with the stream for exactly this reason.
    /// </summary>
    private bool TryAttachD3D11(long adapterLuid)
    {
        try
        {
            FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];
            using IDXGIAdapter1? chosen = FindAdapter(adapterLuid);
            D3D11.D3D11CreateDevice(
                chosen,
                chosen is null ? DriverType.Hardware : DriverType.Unknown,
                DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                levels,
                out ID3D11Device? device).CheckError();
            _device = device!;
            _context = _device.ImmediateContext;
            using (ID3D11Multithread multithread = _device.QueryInterface<ID3D11Multithread>())
            {
                multithread.SetMultithreadProtected(true); // the MFT touches the device from its own threads
            }

            _manager = MediaFactory.MFCreateDXGIDeviceManager(); // Vortice keeps the reset token internally
            _manager.ResetDevice(_device).CheckError();
            _transform.ProcessMessage(TMessageType.MessageSetD3DManager, (nuint)(nint)_manager.NativePointer);
            return true;
        }
        catch (Exception e)
        {
            _log.LogInformation(e, "DXVA decoding unavailable; using the software decoder");
            DisposeD3D();
            return false;
        }
    }

    /// <summary>The adapter with this LUID, or null to let D3D11 choose (LUID 0 means nobody told us).</summary>
    private static IDXGIAdapter1? FindAdapter(long adapterLuid)
    {
        if (adapterLuid == 0)
        {
            return null;
        }

        try
        {
            using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint i = 0; factory.EnumAdapters1(i, out IDXGIAdapter1? adapter).Success; i++)
            {
                if (adapter!.Description1.Luid == adapterLuid)
                {
                    return adapter;
                }

                adapter.Dispose();
            }
        }
        catch (SharpGenException)
        {
        }

        return null;
    }

    private void EnsureBgra()
    {
        int need = _width * 4 * _height;
        if (_bgra.Length < need)
        {
            _bgra = new byte[need];
        }
    }

    private void CroppedNv12ToBgra(ReadOnlySpan<byte> nv12, int stride, int yOffset, int uvOffset)
    {
        // Repack the visible window into a tight NV12 buffer, then convert.
        int tightSize = PixelConversion.Nv12Size(_width, _height);
        if (_tight.Length < tightSize)
        {
            _tight = new byte[tightSize];
        }

        byte[] tight = _tight;
        for (int y = 0; y < _height; y++)
        {
            nv12.Slice(yOffset + y * stride, _width).CopyTo(tight.AsSpan(y * _width));
        }

        int uvRows = (_height + 1) / 2;
        for (int y = 0; y < uvRows; y++)
        {
            nv12.Slice(uvOffset + y * stride, _width & ~1).CopyTo(tight.AsSpan(_width * _height + y * _width));
        }

        PixelConversion.Nv12ToBgra(tight, _width, _width * _height, _width, _height, _bgra, _width * 4);
    }

    private void SelectOutputType()
    {
        for (int i = 0; ; i++)
        {
            IMFMediaType type;
            try
            {
                type = _transform.GetOutputAvailableType(0, i);
            }
            catch (SharpGenException)
            {
                break;
            }

            using (type)
            {
                if (type.GetGUID(MediaFoundation.MtSubtype) != MediaFoundation.FormatNv12)
                {
                    continue;
                }

                _transform.SetOutputType(0, type, 0);
                ulong size;
                try
                {
                    size = type.GetUInt64(MediaFoundation.MtFrameSize);
                }
                catch (SharpGenException)
                {
                    // The Microsoft H.264 MFT advertises a placeholder size before it has seen a stream; the
                    // H.265 one advertises none at all. Either way the real size arrives with the stream
                    // change this method is called again for, so an unknown size here is not a refusal.
                    _log.LogDebug("Decoder offers NV12 without a frame size yet; waiting for the stream change");
                    return;
                }

                _width = (int)(size >> 32);
                _height = (int)(size & 0xFFFFFFFF);
                _codedHeight = _height;
                _stride = _width;
                _offsetX = _offsetY = 0;
                try
                {
                    // The coded size is padded to macroblocks; the visible picture is the display aperture.
                    byte[] aperture = type.GetBlob(MediaTypeAttributeKeys.MinimumDisplayAperture);
                    if (aperture.Length >= 16)
                    {
                        int offsetX = BitConverter.ToInt16(aperture, 2);
                        int offsetY = BitConverter.ToInt16(aperture, 6);
                        int cx = BitConverter.ToInt32(aperture, 8);
                        int cy = BitConverter.ToInt32(aperture, 12);
                        if (cx > 0 && cy > 0 && offsetX >= 0 && offsetY >= 0 && offsetX + cx <= _width && offsetY + cy <= _height)
                        {
                            _offsetX = offsetX;
                            _offsetY = offsetY;
                            _width = cx;
                            _height = cy;
                        }
                    }
                }
                catch (SharpGenException)
                {
                }

                try
                {
                    int stride = (int)type.GetUInt32(MediaFoundation.MtDefaultStride);
                    if (stride > 0)
                    {
                        _stride = stride;
                    }
                }
                catch (SharpGenException)
                {
                }

                _log.LogDebug("Decoder output {W}x{H} (coded height {Coded}) NV12 stride {Stride}", _width, _height, _codedHeight, _stride);
                return;
            }
        }

        throw new NotSupportedException("Decoder offers no NV12 output.");
    }

    private void DisposeD3D()
    {
        _staging?.Dispose();
        _manager?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
        _staging = null;
        _manager = null;
        _context = null;
        _device = null;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _output?.Dispose();
        _transform.Dispose();
        DisposeD3D();
        MediaFoundation.Release();
        return ValueTask.CompletedTask;
    }
}
