using System.Runtime.InteropServices;

namespace DeskPair.Platform.Linux.Native;

/// <summary>
/// XRandR, for enumerating monitors. The screen size Xlib reports is the whole virtual desktop across every
/// output; XRandR is how you find out that it is really two monitors side by side, where each one is, and
/// which is primary — the same distinction the Windows enumerator draws with EnumDisplayMonitors.
/// </summary>
internal static partial class Xrandr
{
    private const string Lib = "libXrandr.so.2";

    [LibraryImport(Lib)]
    public static partial nint XRRGetScreenResourcesCurrent(nint display, nint window);

    [LibraryImport(Lib)]
    public static partial void XRRFreeScreenResources(nint resources);

    [LibraryImport(Lib)]
    public static partial nint XRRGetOutputInfo(nint display, nint resources, nint output);

    [LibraryImport(Lib)]
    public static partial void XRRFreeOutputInfo(nint outputInfo);

    [LibraryImport(Lib)]
    public static partial nint XRRGetCrtcInfo(nint display, nint resources, nint crtc);

    [LibraryImport(Lib)]
    public static partial void XRRFreeCrtcInfo(nint crtcInfo);

    [LibraryImport(Lib)]
    public static partial nint XRRGetOutputPrimary(nint display, nint window);

    /// <summary>
    /// The head of <c>XRRScreenResources</c>. Only the counts and the two arrays are needed; the caller reads
    /// the crtc and output arrays by walking the pointers.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct XRRScreenResources
    {
        public nint Timestamp;
        public nint ConfigTimestamp;
        public int NCrtc;
        public nint Crtcs;
        public int NOutput;
        public nint Outputs;
        public int NMode;
        public nint Modes;
    }

    /// <summary>
    /// The head of <c>XRRCrtcInfo</c>: where an output actually is on the virtual desktop and how big. A crtc
    /// with <c>Mode == 0</c> or <c>NOutput == 0</c> is disconnected and is skipped.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct XRRCrtcInfo
    {
        public nint Timestamp;
        public int X;
        public int Y;
        public uint Width;
        public uint Height;
        public nint Mode;
        public ushort Rotation;
        public int NOutput;
        public nint Outputs;
        public ushort Rotations;
        public int NPossible;
        public nint Possible;
    }

    /// <summary>
    /// The head of <c>XRROutputInfo</c>. <c>Connection == 0</c> is RR_Connected. The name is a byte string of
    /// <c>NameLen</c> bytes at <c>Name</c> (e.g. "Virtual-1", "HDMI-1"), used as the display's stable name.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct XRROutputInfo
    {
        public nint Timestamp;
        public nint Crtc;
        public nint Name;
        public int NameLen;
        public ulong MmWidth;
        public ulong MmHeight;
        public ushort Connection;
        public ushort SubpixelOrder;
        public int NCrtc;
        public nint Crtcs;
        public int NClone;
        public nint Clones;
        public int NMode;
        public int NPreferred;
        public nint Modes;
    }

    /// <summary>
    /// <c>XRRModeInfo</c>, the element type of <see cref="XRRScreenResources.Modes"/> (an array of structs,
    /// not of pointers). An output's mode list is an array of these ids.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct XRRModeInfo
    {
        public nint Id;
        public uint Width;
        public uint Height;
        public nuint DotClock;
        public uint HSyncStart;
        public uint HSyncEnd;
        public uint HTotal;
        public uint HSkew;
        public uint VSyncStart;
        public uint VSyncEnd;
        public uint VTotal;
        public nint Name;
        public uint NameLength;
        public nuint ModeFlags;
    }

    public const ushort RrConnected = 0;
}
