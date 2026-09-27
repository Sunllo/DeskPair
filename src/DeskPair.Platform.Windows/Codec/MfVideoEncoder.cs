using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using SharpGen.Runtime;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Abstractions.Codec;
using Vortice.MediaFoundation;

namespace DeskPair.Platform.Windows.Codec;

public sealed class MfVideoEncoderFactory : IVideoEncoderFactory
{
    private readonly ILoggerFactory _logs;
    private SupportedCodecs? _probed;
    private IReadOnlyList<EncoderDescriptor>? _described;

    public MfVideoEncoderFactory(ILoggerFactory logs)
    {
        _logs = logs;
    }

    /// <summary>
    /// Which codecs Media Foundation is asked about. H.264 is everywhere; H.265 and AV1 appear as vendor MFTs
    /// on hardware that has them (NVIDIA registers nvEncMFThevcx.dll and nvEncMFTav1x.dll from the driver, and
    /// only the ones the silicon supports). VP8 and VP9 are not here: Windows registers no encoder for them
    /// that accepts an output type with a frame rate, which is measured in MfDiagnosticsTests.
    /// </summary>
    private static readonly VideoCodec[] Codecs = [VideoCodec.H264, VideoCodec.H265, VideoCodec.Av1];

    /// <summary>Every encoder MFT this machine has registered, hardware first. Cached: enumeration is not free.</summary>
    public IReadOnlyList<EncoderDescriptor> Describe()
    {
        if (_described is null)
        {
            MediaFoundation.AddRef();
            try
            {
                _described = Codecs
                    .SelectMany(codec => MfEncoderCandidate.Enumerate(codec, preferHardware: true)
                        .Select(c => (codec, c)))
                    .Select(pair => new EncoderDescriptor
                    {
                        Codec = pair.codec,
                        Backend = CodecBackend.MediaFoundation,
                        Vendor = MediaFoundation.VendorOf(pair.c.Name),
                        Name = pair.c.Name,
                        IsHardware = pair.c.IsHardware,
                        AcceptsGpuSurface = false,
                    })
                    .ToList();
            }
            finally
            {
                MediaFoundation.Release();
            }
        }

        return _described;
    }

    public SupportedCodecs Probe() => _probed ??= Describe().ToFlags();

    public IVideoEncoder Create(VideoEncoderConfig config) => Codecs.Contains(config.Codec)
        ? new MfVideoEncoder(config, _logs.CreateLogger<MfVideoEncoder>())
        : throw new NotSupportedException($"{config.Codec} is not supported by the Media Foundation encoder.");
}

/// <summary>A registered encoder MFT (hardware or software) that can be activated.</summary>
internal sealed record MfEncoderCandidate(string Name, bool IsHardware, Func<IMFTransform> Activate)
{
    private static readonly Guid FriendlyName = new("314FFBAE-5B41-4C95-9C19-4E7D586FACE3");
    private static readonly Guid HardwareUrl = new("2FB866AC-B078-4942-AB6C-003D05CDA674");
    private const uint EnumSync = 0x1, EnumAsync = 0x2, EnumHardware = 0x4, EnumSortAndFilter = 0x40;

    /// <summary>Hardware encoders first when preferred, then software ones, then the well-known Microsoft CLSID as a last resort.</summary>
    public static List<MfEncoderCandidate> Enumerate(VideoCodec codec, bool preferHardware)
    {
        var list = new List<MfEncoderCandidate>();
        if (MediaFoundation.SubtypeOf(codec) is not { } subtype)
        {
            return list;
        }

        try
        {
            var output = new RegisterTypeInfo { GuidMajorType = MediaFoundation.MediaTypeVideo, GuidSubtype = subtype };
            MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, EnumSync | EnumAsync | EnumHardware | EnumSortAndFilter, null, output, out nint activates, out uint count);
            try
            {
                for (uint i = 0; i < count; i++)
                {
                    nint ptr = Marshal.ReadIntPtr(activates, (int)(i * nint.Size));
                    var activate = new IMFActivate(ptr);
                    string name = $"{codec} encoder";
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

                    list.Add(new MfEncoderCandidate(name, hardware, () =>
                    {
                        activate.ActivateObject(MediaFoundation.IMFTransformIid, out IMFTransform? t).CheckError();
                        return t!;
                    }));
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(activates);
            }
        }
        catch (Exception)
        {
            // Enumeration unavailable; fall through to the CLSID below.
        }

        if (codec == VideoCodec.H264)
        {
            // Only H.264 has a CLSID worth naming: the Microsoft software MFT ships in every Windows edition
            // except N/KN. The other codecs have no in-box encoder, so an empty list is the honest answer.
            list.Add(new MfEncoderCandidate("Microsoft H264 Encoder MFT (CLSID)", false, () => MediaFoundation.CreateTransform(MediaFoundation.H264EncoderClsid)));
        }

        return list.OrderBy(c => preferHardware ? (c.IsHardware ? 0 : 1) : (c.IsHardware ? 1 : 0)).ToList();
    }
}

/// <summary>
/// One codec through the first Media Foundation encoder that accepts our configuration. Handles both the
/// synchronous model (software MFTs) and the event-driven asynchronous model (hardware MFTs).
/// Input is NV12; output is Annex-B access units with SPS/PPS inline on keyframes.
/// </summary>
/// <remarks>
/// Asynchronous MFTs are driven as a pipeline: a background thread turns the MFT's events into
/// semaphore counts, <see cref="TryEncode(ReadOnlySpan{byte}, int, long, out EncodedPacket)"/> submits
/// frame N and returns whichever access unit is ready, normally frame N itself when the encoder finishes
/// within <see cref="OutputWait"/>, otherwise frame N-1 on the next call. The capture loop never spins or
/// sleeps on a coarse timer for encoder output.
/// </remarks>
public sealed class MfVideoEncoder : IVideoEncoder
{
    private const int EventTransformNeedInput = 601;
    private const int EventTransformHaveOutput = 602;
    private const int InputRingSize = 4;
    private static readonly Guid TransformAsync = new("F81A699A-649A-497D-8C73-29F8FED6AD7A");
    private static readonly Guid TransformAsyncUnlock = new("E5666D6B-3422-4EB6-A421-DA7DB1F8E207");
    private static readonly TimeSpan OutputWait = TimeSpan.FromMilliseconds(6);
    private static readonly TimeSpan NeedInputWait = TimeSpan.FromMilliseconds(250);

    private readonly VideoEncoderConfig _config;
    private readonly ILogger _log;
    private readonly IMFTransform _transform;
    private readonly IMFMediaEventGenerator? _events;
    private readonly ICodecAPI? _codecApi;
    private readonly int _inputBytes;
    private readonly MediaFoundation.ReusableSample[] _inputRing;
    private readonly SemaphoreSlim _needInput = new(0);
    private readonly SemaphoreSlim _haveOutput = new(0);
    private readonly Thread? _pump;
    private MediaFoundation.ReusableSample? _outputSample;
    private byte[] _output = new byte[1 << 20];
    private long _frameDuration;
    private long _frameIndex;
    private int _ringIndex;
    private long _pendingPts; // the frame last submitted: whatever comes out without being waited for is its packet
    private bool _forceKey;
    private volatile bool _stopping;
    private bool _disposed;

    public MfVideoEncoder(VideoEncoderConfig config, ILogger log)
    {
        _config = config;
        _log = log;
        _frameDuration = 10_000_000L / Math.Max(1, config.Fps);
        _inputBytes = PixelConversion.Nv12Size(config.Width, config.Height);
        MediaFoundation.AddRef();
        Exception? last = null;
        foreach (MfEncoderCandidate candidate in MfEncoderCandidate.Enumerate(config.Codec, config.PreferHardware))
        {
            if (config.Exclude.Contains(candidate.Name))
            {
                _log.LogDebug("Skipping {Name}: it failed earlier in this session", candidate.Name);
                continue;
            }

            IMFTransform? transform = null;
            try
            {
                transform = candidate.Activate();
                bool isAsync = IsAsync(transform);
                try
                {
                    using IMFAttributes attrs = transform.Attributes;
                    if (isAsync)
                    {
                        attrs.Set(TransformAsyncUnlock, 1u);
                    }

                    attrs.Set(MediaFoundation.CodecApiLowLatencyMode, 1u); // MF_LOW_LATENCY: no reordering, no B-frames
                }
                catch (SharpGenException)
                {
                }

                ConfigureTypes(transform);
                _transform = transform;
                _codecApi = transform.TryGetCodecApi();
                ApplyCodecApiDefaults();
                _events = isAsync ? transform.QueryInterface<IMFMediaEventGenerator>() : null;
                IsHardware = candidate.IsHardware;
                Name = candidate.Name;
                _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, 0);
                _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, 0);
                _inputRing = new MediaFoundation.ReusableSample[isAsync ? InputRingSize : 1];
                for (int i = 0; i < _inputRing.Length; i++)
                {
                    _inputRing[i] = new MediaFoundation.ReusableSample(_inputBytes);
                }

                if (_events is not null)
                {
                    _pump = new Thread(PumpEvents) { IsBackground = true, Name = "mf-video-events" };
                    _pump.Start();
                }

                _log.LogInformation("Encoder: {Descriptor} ({Model}) {W}x{H} @ {Fps} fps, {Kbps} kbps", Descriptor, isAsync ? "async" : "sync", config.Width, config.Height, config.Fps, config.BitrateKbps);
                return;
            }
            catch (Exception e) when (e is SharpGenException or COMException or InvalidOperationException or NotSupportedException)
            {
                _log.LogDebug(e, "Encoder {Name} rejected the configuration", candidate.Name);
                last = e;
                transform?.Dispose();
            }
        }

        MediaFoundation.Release();
        throw new NotSupportedException($"No Media Foundation {config.Codec} encoder accepted the configuration.", last);
    }

    public string Name { get; } = string.Empty;

    public VideoCodec Codec => _config.Codec;

    /// <summary>Which MFT this turned out to be, and whose silicon is behind it.</summary>
    public EncoderDescriptor Descriptor => field ??= new EncoderDescriptor
    {
        Codec = Codec,
        Backend = CodecBackend.MediaFoundation,
        Vendor = MediaFoundation.VendorOf(Name),
        Name = Name,
        IsHardware = IsHardware,
        AcceptsGpuSurface = false,
        AdapterLuid = _config.AdapterLuid,
    };

    public bool IsHardware { get; }

    /// <summary>Hardware encoders prefer a steady feed; the caller repeats the last frame while the screen is static.</summary>
    public bool IsLatencyFree => !IsHardware;

    public PixelFormat RequiredInputFormat => PixelFormat.Nv12;

    public int Bitrate { get; private set; }

    private int _rateBufferKbps;

    /// <summary>Frames submitted whose access unit was not ready by the time the call returned (diagnostics).</summary>
    public long DeferredOutputs { get; private set; }

    public void SetBitrate(int kbps)
    {
        Bitrate = kbps;
        if (_codecApi is not null && !_codecApi.TrySet(MediaFoundation.CodecApiMeanBitRate, (uint)(kbps * 1000)))
        {
            _log.LogDebug("Encoder rejected bitrate change to {Kbps} kbps", kbps);
        }

    }

    /// <summary>
    /// Sizes the rate-control buffer from what the link carries, not from the (frame-rate compensated) bitrate the
    /// encoder is asked for: a buffer of a fifth of a second keeps any single frame, keyframes included, inside
    /// about 200 ms of wire time. Hardware encoders insert a keyframe when the rate control changes this much
    /// anyway, so it is only re-applied on a large change.
    /// </summary>
    public void SetLinkBitrate(int kbps)
    {
        if (_rateBufferKbps > 0 && kbps < _rateBufferKbps * 2 && kbps > _rateBufferKbps / 2)
        {
            return;
        }

        ApplyRateBuffer(kbps, log: false);
    }

    /// <summary>Only the sample timestamps follow the new cadence; MFTs do not renegotiate the frame rate mid-stream.</summary>
    public void SetFrameRate(int fps) => _frameDuration = 10_000_000L / Math.Max(1, fps);

    public void RequestKeyFrame() => _forceKey = true;

    public bool TryEncode(ReadOnlySpan<byte> input, int stride, long ptsTicks, out EncodedPacket packet)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (input.Length < _inputBytes)
        {
            throw new ArgumentException($"Expected {_inputBytes} bytes of NV12.", nameof(input));
        }

        if (_forceKey)
        {
            _forceKey = false;
            if (_codecApi is null || !_codecApi.TrySet(MediaFoundation.CodecApiForceKeyFrame, 1u))
            {
                _log.LogDebug("Force keyframe unsupported by this encoder");
            }
        }

        int total = 0;
        bool key = false;
        long pts = ptsTicks;
        MediaFoundation.ReusableSample slot = _inputRing[_ringIndex];
        _ringIndex = (_ringIndex + 1) % _inputRing.Length;
        slot.Fill(input[.._inputBytes], _frameIndex++ * _frameDuration, _frameDuration);
        if (_events is null)
        {
            _transform.ProcessInput(0, slot.Sample, 0);
            DrainSync(ref total, ref key);
        }
        else
        {
            pts = FeedAsync(slot.Sample, ptsTicks, ref total, ref key);
        }

        if (total == 0)
        {
            packet = default;
            return false;
        }

        packet = new EncodedPacket { Data = new ReadOnlyMemory<byte>(_output, 0, total), IsKeyFrame = key, PtsTicks = pts };
        return true;
    }

    public bool TryCollect(out EncodedPacket packet)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        packet = default;
        if (_events is null || !_haveOutput.Wait(0))
        {
            return false;
        }

        int total = 0;
        bool key = false;
        TryProcessOutput(ref total, ref key);
        if (total == 0)
        {
            return false;
        }

        packet = new EncodedPacket { Data = new ReadOnlyMemory<byte>(_output, 0, total), IsKeyFrame = key, PtsTicks = _pendingPts };
        return true;
    }

    public bool TryEncode(in GpuSurfaceHandle texture, long ptsTicks, out EncodedPacket packet)
    {
        packet = default;
        return false;
    }

    // ---- synchronous model ----

    private void DrainSync(ref int total, ref bool key)
    {
        while (TryProcessOutput(ref total, ref key))
        {
        }
    }

    // ---- asynchronous model ----

    private void PumpEvents()
    {
        while (!_stopping)
        {
            IMFMediaEvent ev;
            try
            {
                ev = _events!.GetEvent(0); // blocks until the MFT raises something (or is shut down)
            }
            catch (Exception)
            {
                break;
            }

            using (ev)
            {
                switch ((int)ev.EventType)
                {
                    case EventTransformNeedInput:
                        _needInput.Release();
                        break;
                    case EventTransformHaveOutput:
                        _haveOutput.Release();
                        break;
                }
            }
        }
    }

    /// <returns>The timestamp of the frame whose access unit came out: an earlier frame's when that one was still waiting.</returns>
    private long FeedAsync(IMFSample sample, long ptsTicks, ref int total, ref bool key)
    {
        // An access unit from the previous submission may be waiting; take exactly one so packets stay one frame each.
        long produced = ptsTicks;
        if (_haveOutput.Wait(0))
        {
            TryProcessOutput(ref total, ref key);
            produced = _pendingPts;
        }

        if (!_needInput.Wait(NeedInputWait))
        {
            _log.LogWarning("Encoder {Name} did not ask for input within {Ms} ms", Name, NeedInputWait.TotalMilliseconds);
            return produced;
        }

        _transform.ProcessInput(0, sample, 0);
        _pendingPts = ptsTicks;
        if (total > 0)
        {
            DeferredOutputs++;
            return produced; // this frame's output is collected later (TryCollect, or the next call)
        }

        if (!_haveOutput.Wait(OutputWait))
        {
            DeferredOutputs++;
            return ptsTicks;
        }

        TryProcessOutput(ref total, ref key);
        return ptsTicks;
    }

    private bool TryProcessOutput(ref int total, ref bool key)
    {
        OutputStreamInfo info = _transform.GetOutputStreamInfo(0);
        bool providesSamples = ((OutputStreamInfoFlags)info.Flags & (OutputStreamInfoFlags.OutputStreamProvidesSamples | OutputStreamInfoFlags.OutputStreamCanProvideSamples)) != 0;
        IMFSample? outSample = null;
        if (!providesSamples)
        {
            int capacity = Math.Max(info.Size, 1 << 16);
            if (_outputSample is null || _outputSample.Capacity < capacity)
            {
                _outputSample?.Dispose();
                _outputSample = new MediaFoundation.ReusableSample(capacity);
            }

            _outputSample.Clear();
            outSample = _outputSample.Sample;
        }

        var data = new OutputDataBuffer { StreamID = 0, Sample = outSample };
        Result hr = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref data, out _);
        if (hr == ResultCode.TransformNeedMoreInput)
        {
            return false;
        }

        hr.CheckError();
        // When we supplied the sample, data.Sample is a second wrapper over the same native object: release only that.
        IMFSample produced = outSample ?? data.Sample ?? throw new InvalidOperationException("Encoder produced no sample.");
        try
        {
            total += MediaFoundation.CopyOut(produced, ref _output, total);
            key |= IsCleanPoint(produced) || LooksLikeKeyframe(_config.Codec, _output.AsSpan(0, total));
            return true;
        }
        finally
        {
            // Our own sample is not AddRef'd by ProcessOutput: the wrapper in data.Sample must not be released.
            if (outSample is null)
            {
                produced.Dispose();
            }

            data.Events?.Dispose();
        }
    }

    private static bool IsCleanPoint(IMFSample sample)
    {
        try
        {
            return sample.GetUInt32(MediaFoundation.SampleCleanPoint) != 0;
        }
        catch (SharpGenException)
        {
            return false;
        }
    }

    /// <summary>
    /// A second opinion on whether a packet starts a decodable sequence, for encoders that do not set the
    /// clean-point attribute. H.264 and H.265 both use Annex-B start codes but number their NAL units
    /// differently, and H.265 puts the type in a different place, so reading an H.265 stream with H.264's
    /// mask silently reports the wrong answer rather than failing. AV1 is not Annex-B at all (it is a
    /// sequence of OBUs with no start codes), so there it returns false and the clean-point flag decides.
    /// </summary>
    public static bool LooksLikeKeyframe(VideoCodec codec, ReadOnlySpan<byte> bitstream)
    {
        if (codec is not (VideoCodec.H264 or VideoCodec.H265))
        {
            return false;
        }

        for (int i = 0; i + 3 < bitstream.Length; i++)
        {
            if (bitstream[i] != 0 || bitstream[i + 1] != 0 || bitstream[i + 2] != 1)
            {
                continue;
            }

            if (codec == VideoCodec.H264)
            {
                // nal_unit_type is the low 5 bits: 5 = IDR, 7 = SPS.
                if ((bitstream[i + 3] & 0x1F) is 5 or 7)
                {
                    return true;
                }
            }
            else
            {
                // H.265's header is two bytes and the type is bits 1..6 of the first: 16-23 are the IRAP
                // pictures (BLA through CRA), 32-34 are VPS/SPS/PPS, which precede every keyframe.
                int type = (bitstream[i + 3] >> 1) & 0x3F;
                if (type is (>= 16 and <= 23) or 32 or 33 or 34)
                {
                    return true;
                }
            }
        }

        return false;
    }

    // ---- configuration ----

    private static bool IsAsync(IMFTransform transform)
    {
        try
        {
            using IMFAttributes attrs = transform.Attributes;
            return attrs.GetUInt32(TransformAsync) == 1;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void ConfigureTypes(IMFTransform transform)
    {
        using (IMFMediaType output = MediaFactory.MFCreateMediaType())
        {
            output.Set(MediaFoundation.MtMajorType, MediaFoundation.MediaTypeVideo);
            output.Set(MediaFoundation.MtSubtype, MediaFoundation.SubtypeOf(_config.Codec)!.Value);
            output.Set(MediaFoundation.MtAvgBitrate, (uint)(_config.BitrateKbps * 1000));
            output.Set(MediaFoundation.MtFrameSize, MediaFoundation.Pack((uint)_config.Width, (uint)_config.Height));
            output.Set(MediaFoundation.MtFrameRate, MediaFoundation.Pack((uint)Math.Max(1, _config.Fps), 1));
            output.Set(MediaFoundation.MtInterlaceMode, MediaFoundation.InterlaceProgressive);
            output.Set(MediaFoundation.MtPixelAspectRatio, MediaFoundation.Pack(1, 1));
            if (MediaFoundation.ProfileOf(_config.Codec) is { } profile)
            {
                output.Set(MediaFoundation.MtMpeg2Profile, profile);
            }

            transform.SetOutputType(0, output, 0);
        }

        using (IMFMediaType input = MediaFactory.MFCreateMediaType())
        {
            input.Set(MediaFoundation.MtMajorType, MediaFoundation.MediaTypeVideo);
            input.Set(MediaFoundation.MtSubtype, MediaFoundation.FormatNv12);
            input.Set(MediaFoundation.MtFrameSize, MediaFoundation.Pack((uint)_config.Width, (uint)_config.Height));
            input.Set(MediaFoundation.MtFrameRate, MediaFoundation.Pack((uint)Math.Max(1, _config.Fps), 1));
            input.Set(MediaFoundation.MtInterlaceMode, MediaFoundation.InterlaceProgressive);
            input.Set(MediaFoundation.MtPixelAspectRatio, MediaFoundation.Pack(1, 1));
            input.Set(MediaFoundation.MtDefaultStride, (uint)_config.Width);
            transform.SetInputType(0, input, 0);
        }
    }

    private void ApplyCodecApiDefaults()
    {
        Bitrate = _config.BitrateKbps;
        if (_codecApi is null)
        {
            return;
        }

        _codecApi.TrySet(MediaFoundation.CodecApiRateControlMode, MediaFoundation.RateControlCbr);
        _codecApi.TrySet(MediaFoundation.CodecApiLowLatency, true);
        _codecApi.TrySet(MediaFoundation.CodecApiGopSize, 0xFFFFFFFFu); // keyframes only on request
        _codecApi.TrySet(MediaFoundation.CodecApiDefaultBPictureCount, 0u); // no reordering delay
        _codecApi.TrySet(MediaFoundation.CodecApiMaxNumRefFrame, 1u); // one reference: cheaper, and a lost frame costs less
        _codecApi.TrySet(MediaFoundation.CodecApiQualityVsSpeed, 33u); // lean towards speed for the real-time budget
        ApplyRateBuffer(_config.BitrateKbps, log: true);
    }

    /// <summary>
    /// Rate-control buffer of about a fifth of a second (sized for at least 8 Mbps): large enough
    /// that the first frames of a window drag and keyframes can borrow bits instead of collapsing to a blur, small
    /// enough that no single frame bursts far beyond what the pacer spreads. Changing it alters the HRD parameters
    /// (a new SPS, i.e. a keyframe), so only <see cref="SetLinkBitrate"/> re-applies it, on a large change.
    /// </summary>
    private void ApplyRateBuffer(int kbps, bool log)
    {
        if (_codecApi is null)
        {
            return;
        }

        _rateBufferKbps = kbps;
        long bps = Math.Max(8_000_000L, kbps * 1000L);
        uint buffer = (uint)Math.Min(uint.MaxValue, bps / 5);
        bool vbv = _codecApi.TrySet(MediaFoundation.CodecApiBufferSize, buffer);
        if (log)
        {
            _log.LogInformation("{Codec} encoder rate buffer: {Buffer} bits ({Vbv})", _config.Codec, buffer, vbv ? "set" : "not supported");
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _stopping = true;
        try
        {
            _transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, 0);
        }
        catch (SharpGenException)
        {
        }

        if (_pump is not null)
        {
            try
            {
                // Asynchronous MFTs implement IMFShutdown; shutting down fails the blocking GetEvent in the pump thread.
                using IMFShutdown? shutdown = _transform.QueryInterfaceOrNull<IMFShutdown>();
                shutdown?.Shutdown();
            }
            catch (Exception)
            {
            }

            _pump.Join(TimeSpan.FromSeconds(2));
        }

        foreach (MediaFoundation.ReusableSample s in _inputRing)
        {
            s.Dispose();
        }

        _outputSample?.Dispose();
        if (_codecApi is not null)
        {
            Marshal.FinalReleaseComObject(_codecApi);
        }

        _events?.Dispose();
        _transform.Dispose();
        _needInput.Dispose();
        _haveOutput.Dispose();
        MediaFoundation.Release();
        return ValueTask.CompletedTask;
    }
}
