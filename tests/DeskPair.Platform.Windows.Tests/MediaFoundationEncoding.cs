using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Abstractions.Codec;
using DeskPair.Platform.Windows.Codec;

namespace DeskPair.Platform.Windows.Tests;

/// <summary>
/// Whether this machine's Media Foundation can actually encode a codec, which listing an encoder does not prove.
///
/// GitHub's Windows runners list Microsoft's H.264 encoder and then refuse every configuration of it (E_FAIL): a
/// server without a working video stack, which is not a machine DeskPair runs on. The tests that need an encoder
/// skip there, as the ones that need a desktop skip where there is none (<see cref="InteractiveDesktop"/>). Asked by
/// building one, the only question the machine answers truthfully.
/// </summary>
internal static class MediaFoundationEncoding
{
    private static readonly ConcurrentDictionary<VideoCodec, bool> Answers = new();

    public static bool CanEncode(VideoCodec codec) => Answers.GetOrAdd(codec, TryBuild);

    private static bool TryBuild(VideoCodec codec)
    {
        var factory = new MfVideoEncoderFactory(NullLoggerFactory.Instance);
        if (!factory.Probe().Supports(codec))
        {
            return false;
        }

        try
        {
            IVideoEncoder encoder = factory.Create(new VideoEncoderConfig(codec, 320, 240, 30, 1000, false, PixelFormat.Nv12, GpuApi.None, 0));
            encoder.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return true;
        }
        catch (NotSupportedException)
        {
            return false; // "No Media Foundation ... encoder accepted the configuration"
        }
    }
}
