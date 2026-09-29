using System.Runtime.InteropServices;

namespace DeskPair.Platform.Windows.Native;

#pragma warning disable IDE1006 // the names are the SDK's

/// <summary>
/// The display configuration (CCD) API: which display paths are active, where, at what size, and changing that. The
/// structures are handed over by pointer; their sizes are asserted in DisplayConfigLayoutTests.
/// </summary>
internal static partial class User32
{
    public const uint QDC_ALL_PATHS = 0x1;
    public const uint QDC_ONLY_ACTIVE_PATHS = 0x2;

    public const uint SDC_TOPOLOGY_INTERNAL = 0x1;
    public const uint SDC_TOPOLOGY_EXTEND = 0x4;
    public const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x20;
    public const uint SDC_VALIDATE = 0x40;
    public const uint SDC_APPLY = 0x80;
    public const uint SDC_SAVE_TO_DATABASE = 0x200;
    public const uint SDC_ALLOW_CHANGES = 0x400;

    /// <summary>INTERNAL | CLONE | EXTEND | EXTERNAL: what the database remembers for the displays connected now.</summary>
    public const uint SDC_USE_DATABASE_CURRENT = 0xF;

    public const uint DISPLAYCONFIG_PATH_ACTIVE = 0x1;
    public const uint DISPLAYCONFIG_PATH_MODE_IDX_INVALID = 0xFFFFFFFF;
    public const uint DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE = 1;
    public const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;

    public const int ERROR_SUCCESS = 0;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_RATIONAL
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    /// <summary>72 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_SOURCE_MODE
    {
        public uint width;
        public uint height;
        public uint pixelFormat;
        public int x;
        public int y;
    }

    /// <summary>64 bytes: the header, then a union of which only the source mode is read or written here.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    public struct DISPLAYCONFIG_MODE_INFO
    {
        [FieldOffset(0)] public uint infoType;
        [FieldOffset(4)] public uint id;
        [FieldOffset(8)] public LUID adapterId;
        [FieldOffset(16)] public DISPLAYCONFIG_SOURCE_MODE sourceMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public uint type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    /// <summary>84 bytes: which GDI display (<c>\\.\DISPLAYn</c>) a source is.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public unsafe struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public fixed char viewGdiDeviceName[32];
    }

    [LibraryImport("user32.dll")]
    public static partial int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [LibraryImport("user32.dll")]
    public static unsafe partial int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, DISPLAYCONFIG_PATH_INFO* pathArray, ref uint numModeInfoArrayElements, DISPLAYCONFIG_MODE_INFO* modeInfoArray, nint currentTopologyId);

    [LibraryImport("user32.dll")]
    public static unsafe partial int SetDisplayConfig(uint numPathArrayElements, DISPLAYCONFIG_PATH_INFO* pathArray, uint numModeInfoArrayElements, DISPLAYCONFIG_MODE_INFO* modeInfoArray, uint flags);

    [LibraryImport("user32.dll")]
    public static unsafe partial int DisplayConfigGetDeviceInfo(DISPLAYCONFIG_DEVICE_INFO_HEADER* requestPacket);
}
