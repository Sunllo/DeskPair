using Microsoft.Extensions.Logging.Abstractions;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Abstractions.Codec;
using DeskPair.Platform.Windows.Codec;

namespace DeskPair.Platform.Windows.Tests;

/// <summary>
/// The host now says which encoder it is using rather than deciding privately. These check that what comes
/// back names something real, because a descriptor that says "Unknown software encoder" is worse than none.
/// </summary>
public class EncoderDescriptorTests
{
    [Theory]
    [InlineData("NVIDIA H.264 Encoder MFT", GpuVendor.Nvidia)]
    [InlineData("Intel® Quick Sync Video H.264 Encoder MFT", GpuVendor.Intel)]
    [InlineData("Intel Hardware H264 Encoder MFT", GpuVendor.Intel)]
    [InlineData("AMD H.264 Hardware MFT Encoder", GpuVendor.Amd)]
    [InlineData("AMD Radeon HEVC Encoder", GpuVendor.Amd)]
    [InlineData("H264 Encoder MFT", GpuVendor.Unknown)]
    [InlineData("Microsoft AVC DX12 Encoder", GpuVendor.Unknown)]
    public void The_vendor_is_read_from_the_transform_name(string mftName, GpuVendor expected) =>
        MediaFoundation.VendorOf(mftName).ShouldBe(expected);

    [Fact]
    public void This_machine_describes_at_least_one_h264_encoder()
    {
        var factory = new MfVideoEncoderFactory(NullLoggerFactory.Instance);

        IReadOnlyList<EncoderDescriptor> described = factory.Describe();

        described.ShouldNotBeEmpty();
        described.ShouldContain(d => d.Codec == VideoCodec.H264, "every Windows edition except N/KN has one");
        described.ShouldAllBe(d => d.Backend == CodecBackend.MediaFoundation);
        described.ShouldAllBe(d => d.Name.Length > 0);

        // Hardware first within each codec, so taking the head of a codec's group gets the best one. Across
        // codecs the order does not mean anything: H.265 hardware follows H.264 software on this machine.
        foreach (IGrouping<VideoCodec, EncoderDescriptor> group in described.GroupBy(d => d.Codec))
        {
            EncoderDescriptor[] ordered = [.. group];
            int lastHardware = ordered.Select((d, i) => (d, i)).Where(x => x.d.IsHardware).Select(x => x.i).DefaultIfEmpty(-1).Max();
            int firstSoftware = ordered.Select((d, i) => (d, i)).Where(x => !x.d.IsHardware).Select(x => x.i).DefaultIfEmpty(int.MaxValue).Min();
            lastHardware.ShouldBeLessThan(firstSoftware, $"{group.Key} encoders are not hardware-first");
        }
    }

    /// <summary>
    /// Whatever Describe offers, Create must accept: a descriptor the factory will not build from is a promise
    /// the host makes to the viewer and then breaks after the session is already up.
    /// </summary>
    [Fact]
    public async Task Every_described_codec_can_actually_be_created()
    {
        var factory = new MfVideoEncoderFactory(NullLoggerFactory.Instance);

        foreach (VideoCodec codec in factory.Describe().Select(d => d.Codec).Distinct())
        {
            await using IVideoEncoder encoder = factory.Create(
                new VideoEncoderConfig(codec, 320, 240, 30, 1000, PreferHardware: true, PixelFormat.Nv12, GpuApi.None, 0));
            encoder.Codec.ShouldBe(codec);
            encoder.Descriptor.Name.Length.ShouldBeGreaterThan(0);
        }
    }

    [Fact]
    public void The_old_flag_view_agrees_with_the_descriptors()
    {
        var factory = new MfVideoEncoderFactory(NullLoggerFactory.Instance);

        factory.Probe().ShouldBe(factory.Describe().ToFlags());
        factory.Probe().Supports(VideoCodec.H264).ShouldBeTrue();
    }

    [Theory]
    [InlineData(VideoCodec.H264, false, SupportedCodecs.H264Software)]
    [InlineData(VideoCodec.H264, true, SupportedCodecs.H264Hardware)]
    [InlineData(VideoCodec.Av1, true, SupportedCodecs.Av1Hardware)]
    [InlineData(VideoCodec.Vp9, false, SupportedCodecs.Vp9Software)]
    public void A_descriptor_contributes_one_bit(VideoCodec codec, bool hardware, SupportedCodecs expected)
    {
        SupportedCodecsExtensions.Bit(codec, hardware).ShouldBe(expected);
        SupportedCodecsExtensions.Pair(codec).HasFlag(expected).ShouldBeTrue();
    }

    [Fact]
    public void Every_codec_has_its_own_pair_of_bits()
    {
        VideoCodec[] all = Enum.GetValues<VideoCodec>();
        SupportedCodecs seen = SupportedCodecs.None;
        foreach (VideoCodec codec in all)
        {
            SupportedCodecs pair = SupportedCodecsExtensions.Pair(codec);
            pair.ShouldNotBe(SupportedCodecs.None, $"{codec} has no bits");
            (pair & seen).ShouldBe(SupportedCodecs.None, $"{codec} shares bits with another codec");
            seen |= pair;
        }
    }
}
