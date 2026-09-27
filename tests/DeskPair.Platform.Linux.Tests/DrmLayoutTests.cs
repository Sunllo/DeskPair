using System.Runtime.InteropServices;
using DeskPair.Platform.Linux.Native;

namespace DeskPair.Platform.Linux.Tests;

/// <summary>
/// The C structs behind libdrm and the ioctl numbers behind the raw calls, derived here from their
/// declarations rather than copied from a header — the same discipline VpxLayoutTests applies to
/// libvpx. A wrong pad in <c>drmModePlane</c> reads the wrong plane and a wrong size in the ioctl number
/// is ENOTTY, and neither shows up on the one machine that was measured unless it is checked.
/// </summary>
public class DrmLayoutTests
{
    // typedef struct _drmModePlane { uint32_t count_formats; uint32_t *formats; uint32_t plane_id, crtc_id,
    //   fb_id, crtc_x, crtc_y, x, y, possible_crtcs, gamma_size; }  -> 4 (+4 pad) + 8 + 9*4 = 52 -> 56 aligned
    [Fact]
    public void drmModePlane_is_56_bytes() => Marshal.SizeOf<Drm.ModePlane>().ShouldBe(56);

    // { uint32_t fb_id, width, height, pixel_format; uint64_t modifier; uint32_t flags; uint32_t handles[4],
    //   pitches[4], offsets[4]; } -> 16 + 8 + 4 + 48 = 76 -> 80 aligned to the uint64
    [Fact]
    public void drmModeFB2_is_80_bytes() => Marshal.SizeOf<Drm.ModeFB2>().ShouldBe(80);

    // { uint32_t count_planes; uint32_t *planes; } -> 4 (+4) + 8
    [Fact]
    public void drmModePlaneRes_is_16_bytes() => Marshal.SizeOf<Drm.ModePlaneRes>().ShouldBe(16);

    // { uint32_t count_props; uint32_t *props; uint64_t *prop_values; } -> 4 (+4) + 8 + 8
    [Fact]
    public void drmModeObjectProperties_is_24_bytes() => Marshal.SizeOf<Drm.ModeObjectProperties>().ShouldBe(24);

    // { uint32_t prop_id, flags; char name[32]; int count_values; uint64_t *values; int count_enums;
    //   ...*enums; int count_blobs; uint32_t *blob_ids; } -> 40 + 4 (+4) + 8 + 4 (+4) + 8 + 4 (+4) + 8 = 88
    [Fact]
    public void drmModePropertyRes_is_88_bytes() => Marshal.SizeOf<Drm.ModeProperty>().ShouldBe(88);

    // { int count_fbs; uint32_t *fbs; int count_crtcs; uint32_t *crtcs; int count_connectors; ...; int
    //   count_encoders; ...; uint32_t min_width, max_width, min_height, max_height; } -> 4*16 + 16 = 80
    [Fact]
    public void drmModeRes_is_80_bytes() => Marshal.SizeOf<Drm.ModeRes>().ShouldBe(80);

    // { uint32_t clock; uint16_t x10; uint32_t vrefresh, flags, type; char name[32]; } -> 4 + 20 + 12 + 32 = 68
    [Fact]
    public void drmModeModeInfo_is_68_bytes() => Marshal.SizeOf<Drm.ModeInfo>().ShouldBe(68);

    // { uint32_t crtc_id, buffer_id, x, y, width, height; int mode_valid; drmModeModeInfo mode; int gamma_size; }
    //   -> 24 + 4 + 68 + 4 = 100, and every member is 4-aligned so no tail pad
    [Fact]
    public void drmModeCrtc_is_100_bytes() => Marshal.SizeOf<Drm.ModeCrtc>().ShouldBe(100);

    // { uint32_t x4; int connection; uint32_t mmWidth, mmHeight; int subpixel; int count_modes; (+4) modes*;
    //   int count_props; (+4) props*; prop_values*; int count_encoders; (+4) encoders*; } -> 88
    [Fact]
    public void drmModeConnector_is_88_bytes() => Marshal.SizeOf<Drm.ModeConnector>().ShouldBe(88);

    // { uint32_t encoder_id, encoder_type, crtc_id, possible_crtcs, possible_clones; } -> 20
    [Fact]
    public void drmModeEncoder_is_20_bytes()
    {
        Marshal.SizeOf<Drm.ModeEncoder>().ShouldBe(20);
        Marshal.OffsetOf<Drm.ModeEncoder>(nameof(Drm.ModeEncoder.CrtcId)).ShouldBe(8);
    }

    // { int major, minor, patchlevel; int name_len; char *name; int date_len; char *date; int desc_len; char *desc; }
    //   -> 16 + 8 + 4 (+4) + 8 + 4 (+4) + 8 = 56
    [Fact]
    public void drmVersion_is_56_bytes() => Marshal.SizeOf<Drm.Version>().ShouldBe(56);

    /// <summary>_IOW('d', 0x09, struct drm_gem_close { u32 handle; u32 pad; }) — the number measured to work.</summary>
    [Fact]
    public void GEM_CLOSE_is_the_ioctl_the_kernel_expects()
    {
        Marshal.SizeOf<Drm.GemCloseRequest>().ShouldBe(8);
        Uinput.Ioc(Uinput.IocWrite, 'd', 0x09, 8).ShouldBe(Drm.IoctlGemClose);
        Drm.IoctlGemClose.ShouldBe(0x40086409u);
    }

    [Fact]
    public void The_fourcc_and_modifier_names_are_the_kernels()
    {
        DrmFormat.Xrgb8888.ShouldBe(0x34325258u); // 'X' 'R' '2' '4' little-endian
        DrmFormat.ModifierInvalid.ShouldBe(0x00FFFFFFFFFFFFFFUL);
        DrmFormat.Vendor(0x0100000000000002UL).ShouldBe((byte)1); // DRM_FORMAT_MOD_VENDOR_INTEL
        DrmFormat.ModifierName(0x0100000000000001UL).ShouldBe("I915_FORMAT_MOD_X_TILED");
        Drm.ConnectorTypeName(15).ShouldBe("Virtual"); // what the verification machine reports as Virtual-1
    }
}
