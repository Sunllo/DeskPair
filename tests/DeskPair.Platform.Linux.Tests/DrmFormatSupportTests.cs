using DeskPair.Platform.Linux.Capture.Drm;
using DeskPair.Platform.Linux.Native;

namespace DeskPair.Platform.Linux.Tests;

/// <summary>
/// Every scanout format the verification machine cannot show us, decided on paper.
///
/// The one Linux machine this is verified on is a VMware guest: linear XRGB, no modifier, no tiling, no
/// compression. Everything in this file that is not that one row is a format from a machine nobody on
/// the project has, and this table is the whole of the coverage those formats get. The single rule it
/// exists to hold: nothing tiled or compressed is ever read as if it were linear, because a linear read of
/// a tiled buffer is a scrambled picture that looks like a bug in something else.
/// </summary>
public class DrmFormatSupportTests
{
    private static readonly uint Xr24 = DrmFormat.Xrgb8888;

    /// <summary>The row that was measured: 2026-09-23, the GDM greeter on a Linux test machine.</summary>
    [Fact]
    public void The_verification_machine_is_linear_packed_32()
    {
        DrmFormatSupport.Classify("vmwgfx", Xr24, DrmFormat.ModifierLinear, 1).ShouldBe(DrmReadPath.LinearPacked32);
    }

    [Theory]
    [InlineData("i915")]
    [InlineData("amdgpu")]
    [InlineData("nvidia-drm")]
    [InlineData("nouveau")]
    [InlineData("radeon")]
    public void Linear_is_linear_on_every_driver(string driver)
    {
        DrmFormatSupport.Classify(driver, Xr24, DrmFormat.ModifierLinear, 1).ShouldBe(DrmReadPath.LinearPacked32);
    }

    /// <summary>
    /// INVALID means "no modifier was recorded", not "linear". On i915 a legacy-ADDFB buffer can be X-tiled
    /// with the tiling kept in the GEM object, invisible to GETFB2. Reading it as linear is the scrambled
    /// picture this whole table exists to prevent.
    /// </summary>
    [Theory]
    [InlineData("i915")]
    [InlineData("xe")]
    [InlineData("amdgpu")]
    [InlineData("nvidia-drm")]
    [InlineData("nouveau")]
    [InlineData("panfrost")]
    [InlineData("msm")]
    public void Invalid_is_not_linear_on_a_gpu_driver(string driver)
    {
        DrmFormatSupport.Classify(driver, Xr24, DrmFormat.ModifierInvalid, 1).ShouldNotBe(DrmReadPath.LinearPacked32);
    }

    /// <summary>Only display hardware that cannot tile at all gets the benefit of the doubt.</summary>
    [Theory]
    [InlineData("vmwgfx")]
    [InlineData("virtio_gpu")]
    [InlineData("qxl")]
    [InlineData("bochs-drm")]
    [InlineData("simpledrm")]
    [InlineData("ast")]
    [InlineData("mgag200")]
    public void Invalid_is_linear_only_on_a_linear_only_driver(string driver)
    {
        DrmFormatSupport.Classify(driver, Xr24, DrmFormat.ModifierInvalid, 1).ShouldBe(DrmReadPath.LinearPacked32);
    }

    /// <summary>Every vendor's tiled and compressed layout goes to the GPU, never to memcpy.</summary>
    [Theory]
    [InlineData(0x0100000000000001UL, "I915 X_TILED")]
    [InlineData(0x0100000000000002UL, "I915 Y_TILED")]
    [InlineData(0x0100000000000003UL, "I915 Yf_TILED")]
    [InlineData(0x0100000000000004UL, "I915 Y_TILED_CCS")]
    [InlineData(0x0100000000000006UL, "I915 Y_TILED_GEN12_RC_CCS")]
    [InlineData(0x0100000000000009UL, "I915 4_TILED")]
    [InlineData(0x0200000000000E1BUL, "AMD GFX9 64K_S_X with DCC")]
    [InlineData(0x0200000000040C1BUL, "AMD GFX10 64K_R_X")]
    [InlineData(0x0300000000000010UL, "NVIDIA 16Bx2 block linear")]
    [InlineData(0x0800000000000001UL, "ARM AFBC 16x16")]
    [InlineData(0x0800000000000081UL, "ARM AFBC 16x16 YTR")]
    public void A_vendor_modifier_needs_the_gpu(ulong modifier, string what)
    {
        DrmFormatSupport.Classify("any", Xr24, modifier, 1).ShouldBe(DrmReadPath.GpuImportRequired, what);
    }

    /// <summary>Ten-bit, YUV and multi-planar buffers are refused rather than misread as 8-bit BGRX.</summary>
    [Theory]
    [InlineData('X', 'R', '3', '0', 1)]
    [InlineData('A', 'R', '3', '0', 1)]
    [InlineData('N', 'V', '1', '2', 2)]
    [InlineData('P', '0', '1', '0', 2)]
    [InlineData('X', 'R', '2', '4', 2)]
    public void Anything_that_is_not_one_packed_32_bit_plane_is_refused(char a, char b, char c, char d, int planes)
    {
        DrmFormatSupport.Classify("vmwgfx", DrmFormat.Fourcc(a, b, c, d), DrmFormat.ModifierLinear, planes)
            .ShouldBe(DrmReadPath.Unsupported);
    }

    [Theory]
    [InlineData('X', 'R', '2', '4')]
    [InlineData('A', 'R', '2', '4')]
    [InlineData('X', 'B', '2', '4')]
    [InlineData('A', 'B', '2', '4')]
    public void The_four_packed_32_bit_layouts_are_readable(char a, char b, char c, char d)
    {
        DrmFormatSupport.Classify("vmwgfx", DrmFormat.Fourcc(a, b, c, d), DrmFormat.ModifierLinear, 1)
            .ShouldBe(DrmReadPath.LinearPacked32);
    }

    /// <summary>The message carries the numbers: a support log needs fourcc, modifier and driver, not "unsupported".</summary>
    [Fact]
    public void The_description_names_the_format_the_modifier_and_the_driver()
    {
        string text = DrmFormatSupport.Describe("eDP-1", "i915", DrmFormat.Argb8888, 0x0100000000000002, 1);

        text.ShouldStartWith("DRM-FORMAT: eDP-1");
        text.ShouldContain("AR24");
        text.ShouldContain("I915_FORMAT_MOD_Y_TILED");
        text.ShouldContain("0x0100000000000002");
        text.ShouldContain("i915");
    }

    [Fact]
    public void Fourcc_round_trips_through_its_name()
    {
        DrmFormat.Name(DrmFormat.Xrgb8888).ShouldBe("XR24");
        DrmFormat.Name(DrmFormat.Fourcc('N', 'V', '1', '2')).ShouldBe("NV12");
    }
}
