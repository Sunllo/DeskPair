using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Abstractions.Codec;
using DeskPair.Platform.MacOS.Native;

namespace DeskPair.Platform.MacOS.Codec;

/// <summary>
/// VideoToolbox H.264/HEVC decoding for a Mac controlling a remote host, the hardware counterpart of the
/// encoder. It takes the Annex B the hosts emit and returns BGRA. VP8/VP9 are not VideoToolbox codecs, so
/// they fall to the software decoder in the fallback chain.
/// </summary>
public sealed class MacVideoDecoderFactory : IVideoDecoderFactory
{
    private readonly ILoggerFactory _logs;

    public MacVideoDecoderFactory(ILoggerFactory logs)
    {
        _logs = logs;
    }

    public SupportedCodecs Probe() => MacShim.IsAvailable
        ? SupportedCodecs.H264Hardware | SupportedCodecs.H265Hardware
        : SupportedCodecs.None;

    public IVideoDecoder Create(VideoCodec codec, GpuApi preferredOutput, long adapterLuid)
    {
        if (codec is not (VideoCodec.H264 or VideoCodec.H265))
        {
            throw new NotSupportedException($"VideoToolbox decoder does not support {codec}.");
        }

        return new MacVideoDecoder(codec, _logs.CreateLogger<MacVideoDecoder>());
    }
}

public sealed unsafe class MacVideoDecoder : IVideoDecoder
{
    private readonly ILogger _log;
    private nint _handle;
    private byte[] _frame = [];
    private bool _disposed;

    public MacVideoDecoder(VideoCodec codec, ILogger log)
    {
        _log = log;
        Codec = codec;
        _handle = MacShim.fd_decoder_create(codec == VideoCodec.H265 ? 1 : 0);
        if (_handle == 0)
        {
            throw new InvalidOperationException("VideoToolbox could not create a decompression session.");
        }
    }

    public VideoCodec Codec { get; }

    public bool OutputsGpuSurface => false;

    public bool TryDecode(ReadOnlySpan<byte> packet, bool isKeyFrame, out DecodedFrame frame)
    {
        frame = default;
        if (_disposed || _handle == 0 || packet.IsEmpty)
        {
            return false;
        }

        int rc;
        MacShim.FdDecodedFrame f;
        fixed (byte* p = packet)
        {
            rc = MacShim.fd_decoder_decode(_handle, p, packet.Length, out f);
        }

        if (rc != 1 || f.Data == 0 || f.Width <= 0 || f.Height <= 0)
        {
            return false;
        }

        try
        {
            int tightStride = f.Width * 4;
            int needed = tightStride * f.Height;
            if (_frame.Length < needed)
            {
                _frame = new byte[needed];
            }

            byte* src = (byte*)f.Data;
            for (int y = 0; y < f.Height; y++)
            {
                Marshal.Copy((nint)(src + (long)y * f.Stride), _frame, y * tightStride, tightStride);
            }

            frame = new DecodedFrame
            {
                Width = f.Width,
                Height = f.Height,
                Format = PixelFormat.Bgra32,
                Stride = tightStride,
                Cpu = _frame.AsMemory(0, needed),
            };
            return true;
        }
        finally
        {
            MacShim.fd_decoder_release(_handle);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (_handle != 0)
            {
                MacShim.fd_decoder_destroy(_handle);
                _handle = 0;
            }
        }

        return ValueTask.CompletedTask;
    }
}
