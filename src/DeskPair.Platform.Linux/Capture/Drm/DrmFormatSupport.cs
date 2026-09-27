using DeskPair.Platform.Linux.Native;

namespace DeskPair.Platform.Linux.Capture.Drm;

/// <summary>How a scanout buffer can be read, decided before a byte of it is touched.</summary>
public enum DrmReadPath
{
    /// <summary>Packed 32-bit, one plane, rows in memory order: map it and copy.</summary>
    LinearPacked32,

    /// <summary>Tiled or compressed in a way the GPU has to undo: import through EGL and read back.</summary>
    GpuImportRequired,

    /// <summary>Nothing this build knows how to read. Refuse, with the numbers in the message.</summary>
    Unsupported,
}

/// <summary>
/// The one decision that separates "a scrambled picture on an Intel laptop" from "a clear error on an
/// Intel laptop".
///
/// A pure function of the numbers GETFB2 returns, so it can be tested against every vendor's format on a
/// machine that has none of them. That matters more here than usual: the only verification machine is a
/// VMware guest whose framebuffer is linear and unmodified, so nothing tiled, compressed or ten-bit is
/// ever exercised for real, and this table is the only place those cases are decided.
///
/// The rule that carries the weight: <c>DRM_FORMAT_MOD_INVALID</c> does not mean linear. GETFB2 reports it
/// for a buffer created through the legacy ADDFB path with no modifier, and on i915 such a buffer can still
/// be X-tiled with the tiling recorded in the GEM object where nobody outside the driver can see it. So
/// INVALID is read as linear only on drivers that cannot tile at all.
/// </summary>
public static class DrmFormatSupport
{
    /// <summary>
    /// Drivers whose framebuffers are always linear, so INVALID is safe to read as such. Dumb or virtual
    /// display hardware, by <c>drmGetVersion().name</c>.
    /// </summary>
    private static readonly HashSet<string> LinearOnlyDrivers = new(StringComparer.Ordinal)
    {
        "vmwgfx", "virtio_gpu", "qxl", "bochs-drm", "bochs", "simpledrm", "simple-framebuffer", "ast", "mgag200", "cirrus",
    };

    public static bool IsPacked32(uint fourcc) =>
        fourcc == DrmFormat.Xrgb8888 || fourcc == DrmFormat.Argb8888 ||
        fourcc == DrmFormat.Xbgr8888 || fourcc == DrmFormat.Abgr8888;

    public static DrmReadPath Classify(string driver, uint fourcc, ulong modifier, int planeCount)
    {
        if (!IsPacked32(fourcc) || planeCount != 1)
        {
            // Ten-bit, YUV, multi-planar: real formats, none of them read here, and none silently reinterpreted.
            return DrmReadPath.Unsupported;
        }

        if (modifier == DrmFormat.ModifierLinear)
        {
            return DrmReadPath.LinearPacked32;
        }

        if (modifier == DrmFormat.ModifierInvalid)
        {
            return LinearOnlyDrivers.Contains(driver) ? DrmReadPath.LinearPacked32 : DrmReadPath.GpuImportRequired;
        }

        // A vendor modifier: the layout is the GPU's business and only the GPU can undo it.
        return DrmReadPath.GpuImportRequired;
    }

    /// <summary>
    /// The sentence a support log needs: the exact numbers, named where a name exists, with a stable
    /// prefix to grep for.
    /// </summary>
    public static string Describe(string connector, string driver, uint fourcc, ulong modifier, int planeCount) =>
        $"DRM-FORMAT: {connector} scanout is {DrmFormat.Name(fourcc)} modifier {DrmFormat.ModifierName(modifier)} " +
        $"(0x{modifier:x16}) on driver {driver}, {planeCount} plane{(planeCount == 1 ? "" : "s")}";
}
