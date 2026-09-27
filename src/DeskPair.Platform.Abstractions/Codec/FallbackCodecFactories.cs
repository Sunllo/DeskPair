using DeskPair.Platform.Abstractions.Capture;

namespace DeskPair.Platform.Abstractions.Codec;

/// <summary>Tries factories in order (native first, software fallback last); the union of their codecs is advertised.</summary>
public sealed class FallbackVideoEncoderFactory : IVideoEncoderFactory
{
    private readonly IReadOnlyList<IVideoEncoderFactory> _factories;

    public FallbackVideoEncoderFactory(params IVideoEncoderFactory[] factories)
    {
        _factories = factories;
    }

    /// <summary>
    /// Everything every factory in the chain offers, in order, so the best encoder of each codec is still the
    /// first one named. Without this the chain would fall back to the interface default and report nothing,
    /// which is what the login response and the diagnostics verb read.
    /// </summary>
    public IReadOnlyList<EncoderDescriptor> Describe()
    {
        var all = new List<EncoderDescriptor>();
        foreach (IVideoEncoderFactory f in _factories)
        {
            try
            {
                all.AddRange(f.Describe());
            }
            catch (Exception)
            {
            }
        }

        return all;
    }

    public SupportedCodecs Probe()
    {
        SupportedCodecs all = SupportedCodecs.None;
        foreach (IVideoEncoderFactory f in _factories)
        {
            try
            {
                all |= f.Probe();
            }
            catch (Exception)
            {
            }
        }

        return all;
    }

    public IVideoEncoder Create(VideoEncoderConfig config)
    {
        Exception? last = null;
        foreach (IVideoEncoderFactory f in _factories)
        {
            try
            {
                if (!f.Probe().Supports(config.Codec))
                {
                    continue;
                }

                return f.Create(config);
            }
            catch (Exception e)
            {
                last = e;
            }
        }

        throw new NotSupportedException($"No encoder could be created for {config.Codec}.", last);
    }
}

public sealed class FallbackVideoDecoderFactory : IVideoDecoderFactory
{
    private readonly IReadOnlyList<IVideoDecoderFactory> _factories;

    public FallbackVideoDecoderFactory(params IVideoDecoderFactory[] factories)
    {
        _factories = factories;
    }

    public SupportedCodecs Probe()
    {
        SupportedCodecs all = SupportedCodecs.None;
        foreach (IVideoDecoderFactory f in _factories)
        {
            try
            {
                all |= f.Probe();
            }
            catch (Exception)
            {
            }
        }

        return all;
    }

    public IVideoDecoder Create(VideoCodec codec, GpuApi preferredOutput, long adapterLuid)
    {
        Exception? last = null;
        foreach (IVideoDecoderFactory f in _factories)
        {
            try
            {
                if (!f.Probe().Supports(codec))
                {
                    continue;
                }

                return f.Create(codec, preferredOutput, adapterLuid);
            }
            catch (Exception e)
            {
                last = e;
            }
        }

        throw new NotSupportedException($"No decoder could be created for {codec}.", last);
    }
}
