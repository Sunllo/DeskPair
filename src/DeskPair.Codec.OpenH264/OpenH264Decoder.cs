using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using DeskPair.Codec.OpenH264.Native;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Abstractions.Codec;

namespace DeskPair.Codec.OpenH264;

public sealed class OpenH264DecoderFactory : IVideoDecoderFactory
{
    private readonly ILoggerFactory _logs;

    public OpenH264DecoderFactory(ILoggerFactory logs)
    {
        _logs = logs;
    }

    public SupportedCodecs Probe() => OpenH264EncoderFactory.IsAvailable ? SupportedCodecs.H264Software : SupportedCodecs.None;

    public IVideoDecoder Create(VideoCodec codec, GpuApi preferredOutput, long adapterLuid)
    {
        if (codec != VideoCodec.H264)
        {
            throw new NotSupportedException($"{codec} is not supported by OpenH264.");
        }

        if (!OpenH264EncoderFactory.IsAvailable)
        {
            throw new NotSupportedException(Native.OpenH264.LoadError ?? "OpenH264 is not available.");
        }

        return new OpenH264Decoder(_logs.CreateLogger<OpenH264Decoder>());
    }
}

/// <summary>Decodes Annex-B H.264 with OpenH264; the I420 output is converted to BGRA for the CPU renderer.</summary>
public sealed unsafe class OpenH264Decoder : IVideoDecoder
{
    private readonly ILogger _log;
    private nint _decoder;
    private byte[] _bgra = [];
    private bool _disposed;

    public OpenH264Decoder(ILogger log)
    {
        _log = log;
        nint dec;
        CLong rv = Native.OpenH264.WelsCreateDecoder(&dec);
        if (rv.Value != 0 || dec == 0)
        {
            throw new NotSupportedException($"WelsCreateDecoder failed ({rv.Value}).");
        }

        _decoder = dec;
        var param = new SDecodingParam
        {
            uiTargetDqLayer = 0xFF, // all layers
            eEcActiveIdc = Native.OpenH264.ErrorConSliceCopyCrossIdrFreezeResChange,
            sVideoProperty = new SVideoProperty { size = (uint)sizeof(SVideoProperty), eVideoBsType = Native.OpenH264.VideoBitstreamAvc },
        };
        CLong init = Native.OpenH264.DecInitialize(dec, &param);
        if (init.Value != 0)
        {
            Native.OpenH264.WelsDestroyDecoder(dec);
            _decoder = 0;
            throw new InvalidOperationException($"OpenH264 decoder Initialize failed ({init.Value}).");
        }

    }

    public VideoCodec Codec => VideoCodec.H264;

    public bool OutputsGpuSurface => false;

    public bool TryDecode(ReadOnlySpan<byte> packet, bool isKeyFrame, out DecodedFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        frame = default;
        byte* y = null, u = null, v = null;
        byte** dst = stackalloc byte*[3];
        var info = new SBufferInfo();
        int state;
        fixed (byte* src = packet)
        {
            state = Native.OpenH264.DecDecodeFrameNoDelay(_decoder, src, packet.Length, dst, &info);
        }

        if (state != Native.OpenH264.DsErrorFree && state != Native.OpenH264.DsFramePending)
        {
            _log.LogDebug("OpenH264 decode state 0x{State:X}", state);
        }

        if (info.iBufferStatus != 1)
        {
            return false;
        }

        y = dst[0];
        u = dst[1];
        v = dst[2];
        int width = info.sSystemBuffer.iWidth;
        int height = info.sSystemBuffer.iHeight;
        int yStride = info.sSystemBuffer.iStride[0];
        int uvStride = info.sSystemBuffer.iStride[1];
        if (width <= 0 || height <= 0 || y == null || u == null || v == null)
        {
            return false;
        }

        int bgraStride = width * 4;
        if (_bgra.Length < bgraStride * height)
        {
            _bgra = new byte[bgraStride * height];
        }

        int chromaHeight = (height + 1) / 2;
        PixelConversion.I420ToBgra(
            new ReadOnlySpan<byte>(y, yStride * height), yStride,
            new ReadOnlySpan<byte>(u, uvStride * chromaHeight),
            new ReadOnlySpan<byte>(v, uvStride * chromaHeight), uvStride,
            width, height, _bgra, bgraStride);
        frame = new DecodedFrame { Width = width, Height = height, Format = PixelFormat.Bgra32, Cpu = new ReadOnlyMemory<byte>(_bgra, 0, bgraStride * height), Stride = bgraStride };
        return true;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        if (_decoder != 0)
        {
            Native.OpenH264.DecUninitialize(_decoder);
            Native.OpenH264.WelsDestroyDecoder(_decoder);
            _decoder = 0;
        }

        return ValueTask.CompletedTask;
    }
}
