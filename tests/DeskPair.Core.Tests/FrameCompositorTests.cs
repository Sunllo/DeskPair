using DeskPair.Core.Video;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Abstractions.Codec;
using DeskPair.Protocol.Messages;

namespace DeskPair.Core.Tests;

public class FrameCompositorTests
{
    private const int W = 128, H = 64;

    [Fact]
    public void A_decoder_reusing_its_buffer_does_not_change_a_detached_picture()
    {
        byte[] decoderBuffer = new byte[W * H * 4];
        Array.Fill(decoderBuffer, (byte)0x11);
        var compositor = new FrameCompositor();
        compositor.ApplyVideo(new DecodedFrame { Width = W, Height = H, Format = PixelFormat.Bgra32, Cpu = decoderBuffer, Stride = W * 4 });

        // The next frame will not be shown: detach first, then the decoder overwrites its buffer.
        compositor.DetachFromDecoder();
        Array.Fill(decoderBuffer, (byte)0x99);

        compositor.Current.Cpu.ToArray().ShouldAllBe(b => b == 0x11);
        compositor.ApplyTiles(new TileUpdate { Width = W, Height = H, TileSize = 64 }).ShouldBeTrue();
        compositor.Current.Cpu.ToArray().ShouldAllBe(b => b == 0x11);
    }

    [Fact]
    public void Video_frames_are_passed_through_without_a_copy()
    {
        byte[] decoderBuffer = new byte[W * H * 4];
        var compositor = new FrameCompositor();
        for (int i = 0; i < 5; i++)
        {
            compositor.ApplyVideo(new DecodedFrame { Width = W, Height = H, Format = PixelFormat.Bgra32, Cpu = decoderBuffer, Stride = W * 4 });
        }

        compositor.CanvasCopies.ShouldBe(0);
    }
}
