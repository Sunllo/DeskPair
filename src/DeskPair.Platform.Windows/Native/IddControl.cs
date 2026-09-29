using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DeskPair.Platform.Windows.Native;

#pragma warning disable IDE1006 // the names are the C header's

/// <summary>
/// The control protocol of DeskPair's virtual display driver: <c>native/idd/src/Control.h</c>, mirrored.
/// DisplayDriverProtocolTests reads that header and holds the two to the same codes, layouts and limits.
/// </summary>
internal static partial class IddControl
{
    /// <summary>The device interface the driver's adapter registers; only SYSTEM and administrators may open it.</summary>
    public static readonly Guid Interface = new(0x27f43708, 0x94d2, 0x4e46, 0x8f, 0x95, 0x22, 0xc3, 0x73, 0x86, 0x1d, 0xf4);

    public const uint Protocol = 1;
    public const int Slots = 4;
    public const int MaxModes = 199;
    public const int MinSide = 320;
    public const int MaxSide = 8192;

    // CTL_CODE(FILE_DEVICE_UNKNOWN, 0x900 + function, METHOD_BUFFERED, access)
    public const uint IOCTL_DESKPAIR_DISPLAY_INFO = 0x00226400;     // function 0, FILE_READ_ACCESS
    public const uint IOCTL_DESKPAIR_DISPLAY_PLUG = 0x0022a404;     // function 1, FILE_WRITE_ACCESS
    public const uint IOCTL_DESKPAIR_DISPLAY_UNPLUG = 0x0022a408;   // function 2
    public const uint IOCTL_DESKPAIR_DISPLAY_WATCHDOG = 0x0022a40c; // function 3
    public const uint IOCTL_DESKPAIR_DISPLAY_SELECT = 0x0022a410;   // function 4

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct DESKPAIR_DISPLAY_INFO
    {
        public uint Protocol;
        public uint Slots;
        public uint MaxModes;
        public uint PluggedMask;
        public uint WatchdogMs;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct DESKPAIR_DISPLAY_MODE
    {
        public uint Width;
        public uint Height;
        public uint RefreshHz;
    }

    /// <summary>Followed by <see cref="ModeCount"/> <see cref="DESKPAIR_DISPLAY_MODE"/>s.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct DESKPAIR_DISPLAY_PLUG
    {
        public uint Protocol;
        public uint Slot;
        public uint Current;
        public uint ModeCount;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct DESKPAIR_DISPLAY_PLUGGED
    {
        public uint Protocol;
        public uint Slot;
        public uint AdapterLuidLow;
        public int AdapterLuidHigh;
        public uint TargetId;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct DESKPAIR_DISPLAY_UNPLUG
    {
        public uint Protocol;
        public uint Slot;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct DESKPAIR_DISPLAY_WATCHDOG
    {
        public uint Protocol;
        public uint TimeoutMs;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct DESKPAIR_DISPLAY_SELECT
    {
        public uint Protocol;
        public uint Slot;
        public uint Current;
    }

    public const uint CM_GET_DEVICE_INTERFACE_LIST_PRESENT = 0;

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_List_SizeW")]
    public static unsafe partial int CM_Get_Device_Interface_List_Size(out uint length, in Guid interfaceClass, char* deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_ListW")]
    public static unsafe partial int CM_Get_Device_Interface_List(in Guid interfaceClass, char* deviceId, char* buffer, uint length, uint flags);

    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 0x1;
    public const uint FILE_SHARE_WRITE = 0x2;
    public const uint OPEN_EXISTING = 3;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool DeviceIoControl(SafeFileHandle device, uint code, void* input, uint inputLength, void* output, uint outputLength, out uint returned, nint overlapped);
}
