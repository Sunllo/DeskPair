using System.Runtime.InteropServices;

namespace DeskPair.Platform.Linux.Native;

/// <summary>
/// libdrm's mode-setting queries, and the three raw ioctls beside them.
///
/// This is how the login screen and the lock screen are seen at all. Neither is in X: GNOME draws both
/// as a compositor and never hands them to an X drawable, so the only place the picture exists is the
/// framebuffer the CRTC is scanning out. Reading that takes a root process — GETFB2 hands GEM handles
/// only to the DRM master or CAP_SYS_ADMIN, and the compositor is the master — which is why everything
/// here is called from the daemon and never from the engine.
///
/// Only <c>libdrm.so.2</c> is used, deliberately. It is a thin wrapper over ioctls and loads no driver;
/// GBM, EGL and every vendor userspace stay out of the root process, because <c>gbm_create_device</c>
/// alone pulls in Mesa's DRI driver.
///
/// Struct layouts are the C declarations from <c>xf86drmMode.h</c>, member for member; DrmLayoutTests
/// derives the sizes from those declarations and checks them here, since a wrong pad reads the wrong
/// monitor.
/// </summary>
internal static partial class Drm
{
    private const string Lib = "libdrm.so.2";

    public const ulong ClientCapUniversalPlanes = 2;
    public const ulong ClientCapAtomic = 3;

    public const uint ObjectPlane = 0xEEEEEEEE;
    public const uint ObjectCrtc = 0xCCCCCCCC;
    public const uint ObjectConnector = 0xC0C0C0C0;

    public const ulong PlaneTypeOverlay = 0;
    public const ulong PlaneTypePrimary = 1;
    public const ulong PlaneTypeCursor = 2;

    /// <summary>drm_mode_get_connector.connection: 1 connected, 2 disconnected, 3 unknown.</summary>
    public const int Connected = 1;

    [LibraryImport(Lib)]
    public static partial nint drmGetVersion(int fd);

    [LibraryImport(Lib)]
    public static partial void drmFreeVersion(nint version);

    [LibraryImport(Lib)]
    public static partial int drmSetClientCap(int fd, ulong capability, ulong value);

    /// <summary>Gives up DRM master on this descriptor: 0 when it was held and is now released, negative otherwise.</summary>
    [LibraryImport(Lib)]
    public static partial int drmDropMaster(int fd);

    [LibraryImport(Lib)]
    public static partial nint drmModeGetResources(int fd);

    [LibraryImport(Lib)]
    public static partial void drmModeFreeResources(nint resources);

    [LibraryImport(Lib)]
    public static partial nint drmModeGetCrtc(int fd, uint crtcId);

    [LibraryImport(Lib)]
    public static partial void drmModeFreeCrtc(nint crtc);

    [LibraryImport(Lib)]
    public static partial nint drmModeGetConnector(int fd, uint connectorId);

    [LibraryImport(Lib)]
    public static partial void drmModeFreeConnector(nint connector);

    /// <summary>
    /// The connector as the kernel last knew it, without probing it: <see cref="drmModeGetConnector"/> reads a
    /// monitor's EDID again, which takes a while and can flicker the screen, and this is asked every few seconds.
    /// </summary>
    [LibraryImport(Lib)]
    public static partial nint drmModeGetConnectorCurrent(int fd, uint connectorId);

    [LibraryImport(Lib)]
    public static partial nint drmModeGetEncoder(int fd, uint encoderId);

    [LibraryImport(Lib)]
    public static partial void drmModeFreeEncoder(nint encoder);

    [LibraryImport(Lib)]
    public static partial nint drmModeGetPlaneResources(int fd);

    [LibraryImport(Lib)]
    public static partial void drmModeFreePlaneResources(nint resources);

    [LibraryImport(Lib)]
    public static partial nint drmModeGetPlane(int fd, uint planeId);

    [LibraryImport(Lib)]
    public static partial void drmModeFreePlane(nint plane);

    [LibraryImport(Lib)]
    public static partial nint drmModeGetFB2(int fd, uint fbId);

    [LibraryImport(Lib)]
    public static partial void drmModeFreeFB2(nint fb);

    [LibraryImport(Lib)]
    public static partial nint drmModeObjectGetProperties(int fd, uint objectId, uint objectType);

    [LibraryImport(Lib)]
    public static partial void drmModeFreeObjectProperties(nint properties);

    [LibraryImport(Lib)]
    public static partial nint drmModeGetProperty(int fd, uint propertyId);

    [LibraryImport(Lib)]
    public static partial void drmModeFreeProperty(nint property);

    /// <summary>Exports a GEM handle as a dma-buf. Needs the handle GETFB2 only gives root or the master.</summary>
    [LibraryImport(Lib)]
    public static partial int drmPrimeHandleToFD(int fd, uint handle, uint flags, out int primeFd);

    /// <summary>
    /// GETFB2 mints a fresh GEM handle in this file's namespace on every call, and a handle that is not
    /// closed is a kernel object leaked at frame rate. Raw ioctl because libdrm has no wrapper for it.
    /// </summary>
    public static int GemClose(int fd, uint handle)
    {
        var request = new GemCloseRequest { Handle = handle };
        return ioctl(fd, IoctlGemClose, ref request);
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int ioctl(int fd, nuint request, ref GemCloseRequest argument);

    /// <summary><c>_IOW('d', 0x09, struct drm_gem_close)</c>.</summary>
    public const uint IoctlGemClose = 0x40086409;

    [StructLayout(LayoutKind.Sequential)]
    public struct GemCloseRequest
    {
        public uint Handle;
        public uint Pad;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Version
    {
        public int Major;
        public int Minor;
        public int PatchLevel;
        public int NameLength;
        public nint Name;
        public int DateLength;
        public nint Date;
        public int DescriptionLength;
        public nint Description;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ModeRes
    {
        public int CountFbs;
        public nint Fbs;
        public int CountCrtcs;
        public nint Crtcs;
        public int CountConnectors;
        public nint Connectors;
        public int CountEncoders;
        public nint Encoders;
        public uint MinWidth;
        public uint MaxWidth;
        public uint MinHeight;
        public uint MaxHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ModeInfo
    {
        public uint Clock;
        public ushort HDisplay;
        public ushort HSyncStart;
        public ushort HSyncEnd;
        public ushort HTotal;
        public ushort HSkew;
        public ushort VDisplay;
        public ushort VSyncStart;
        public ushort VSyncEnd;
        public ushort VTotal;
        public ushort VScan;
        public uint VRefresh;
        public uint Flags;
        public uint Type;
        public fixed byte Name[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ModeCrtc
    {
        public uint CrtcId;
        public uint BufferId;
        public uint X;
        public uint Y;
        public uint Width;
        public uint Height;
        public int ModeValid;
        public ModeInfo Mode;
        public int GammaSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ModeConnector
    {
        public uint ConnectorId;
        public uint EncoderId;
        public uint ConnectorType;
        public uint ConnectorTypeId;
        public int Connection;
        public uint MmWidth;
        public uint MmHeight;
        public int Subpixel;
        public int CountModes;
        public nint Modes;
        public int CountProps;
        public nint Props;
        public nint PropValues;
        public int CountEncoders;
        public nint Encoders;
    }

    /// <summary>What joins a connector to a CRTC: a connector's encoder says which CRTC is driving it.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ModeEncoder
    {
        public uint EncoderId;
        public uint EncoderType;
        public uint CrtcId;
        public uint PossibleCrtcs;
        public uint PossibleClones;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ModePlaneRes
    {
        public uint CountPlanes;
        public nint Planes;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ModePlane
    {
        public uint CountFormats;
        public nint Formats;
        public uint PlaneId;
        public uint CrtcId;
        public uint FbId;
        public uint CrtcX;
        public uint CrtcY;
        public uint X;
        public uint Y;
        public uint PossibleCrtcs;
        public uint GammaSize;
    }

    /// <summary>What GETFB2 answers. The handles are zero for a caller that is neither master nor root.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ModeFB2
    {
        public uint FbId;
        public uint Width;
        public uint Height;
        public uint PixelFormat;
        public ulong Modifier;
        public uint Flags;
        public fixed uint Handles[4];
        public fixed uint Pitches[4];
        public fixed uint Offsets[4];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ModeObjectProperties
    {
        public uint CountProps;
        public nint Props;
        public nint PropValues;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ModeProperty
    {
        public uint PropId;
        public uint Flags;
        public fixed byte Name[32];
        public int CountValues;
        public nint Values;
        public int CountEnums;
        public nint Enums;
        public int CountBlobs;
        public nint BlobIds;
    }

    /// <summary>The connector type names the kernel uses, so "Virtual-1" here is "Virtual-1" in xrandr too.</summary>
    public static string ConnectorTypeName(uint type) => type switch
    {
        1 => "VGA",
        2 => "DVI-I",
        3 => "DVI-D",
        4 => "DVI-A",
        5 => "Composite",
        6 => "SVIDEO",
        7 => "LVDS",
        8 => "Component",
        9 => "DIN",
        10 => "DP",
        11 => "HDMI-A",
        12 => "HDMI-B",
        13 => "TV",
        14 => "eDP",
        15 => "Virtual",
        16 => "DSI",
        17 => "DPI",
        18 => "Writeback",
        19 => "SPI",
        20 => "USB",
        _ => "Unknown",
    };
}

/// <summary>DRM fourcc codes and framebuffer modifiers, as numbers the classifier can name.</summary>
internal static class DrmFormat
{
    public static uint Fourcc(char a, char b, char c, char d) =>
        (uint)a | ((uint)b << 8) | ((uint)c << 16) | ((uint)d << 24);

    public static string Name(uint fourcc) => new(
    [
        (char)(fourcc & 0xFF), (char)((fourcc >> 8) & 0xFF), (char)((fourcc >> 16) & 0xFF), (char)((fourcc >> 24) & 0xFF),
    ]);

    public static readonly uint Xrgb8888 = Fourcc('X', 'R', '2', '4');
    public static readonly uint Argb8888 = Fourcc('A', 'R', '2', '4');
    public static readonly uint Xbgr8888 = Fourcc('X', 'B', '2', '4');
    public static readonly uint Abgr8888 = Fourcc('A', 'B', '2', '4');

    public const ulong ModifierLinear = 0;

    /// <summary>"No modifier recorded": the FB came through the legacy path, and its tiling is not visible.</summary>
    public const ulong ModifierInvalid = 0x00FFFFFFFFFFFFFF;

    public static byte Vendor(ulong modifier) => (byte)(modifier >> 56);

    /// <summary>A name for the log, for the modifiers a support engineer will meet. Everything else is hex.</summary>
    public static string ModifierName(ulong modifier) => modifier switch
    {
        ModifierLinear => "LINEAR",
        ModifierInvalid => "INVALID",
        0x0100000000000001 => "I915_FORMAT_MOD_X_TILED",
        0x0100000000000002 => "I915_FORMAT_MOD_Y_TILED",
        0x0100000000000003 => "I915_FORMAT_MOD_Yf_TILED",
        0x0100000000000004 => "I915_FORMAT_MOD_Y_TILED_CCS",
        0x0100000000000005 => "I915_FORMAT_MOD_Yf_TILED_CCS",
        0x0100000000000006 => "I915_FORMAT_MOD_Y_TILED_GEN12_RC_CCS",
        0x0100000000000007 => "I915_FORMAT_MOD_Y_TILED_GEN12_MC_CCS",
        0x0100000000000008 => "I915_FORMAT_MOD_Y_TILED_GEN12_RC_CCS_CC",
        0x0100000000000009 => "I915_FORMAT_MOD_4_TILED",
        _ => Vendor(modifier) switch
        {
            1 => $"intel 0x{modifier:x16}",
            2 => $"amd 0x{modifier:x16}",
            3 => $"nvidia 0x{modifier:x16}",
            8 => $"arm 0x{modifier:x16}",
            _ => $"0x{modifier:x16}",
        },
    };
}
