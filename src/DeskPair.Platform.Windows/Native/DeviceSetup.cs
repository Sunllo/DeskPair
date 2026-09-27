using System.Runtime.InteropServices;

namespace DeskPair.Platform.Windows.Native;

#pragma warning disable CA1401, SYSLIB1054, IDE1006, CA1069

/// <summary>
/// Device nodes and driver packages: a root-enumerated device node made the way devcon makes one (setupapi),
/// its driver installed from the driver store (newdev), enabled and disabled (cfgmgr32), and a driver package
/// staged in and taken out of the driver store. Every one of these needs administrator rights.
///
/// Not the software device API (SwDeviceCreate), which would have tied the device to a handle: it answers
/// ERROR_MOD_NOT_FOUND to LocalSystem, in session 0 and in a user's session alike, for any device at all --
/// measured on Windows 11 25H2, where an elevated administrator gets through. The service's engine is
/// LocalSystem.
/// </summary>
internal static unsafe partial class DeviceSetup
{
    /// <summary>SP_DEVINFO_DATA: 32 bytes on x64.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public nint Reserved;
    }

    /// <summary>The Display device class.</summary>
    public static readonly Guid DisplayClass = new("4d36e968-e325-11ce-bfc1-08002be10318");

    public const uint DICD_GENERATE_ID = 0x1;
    public const uint SPDRP_HARDWAREID = 0x1;
    public const uint DIF_REMOVE = 0x05;
    public const uint DIF_REGISTERDEVICE = 0x19;
    public const uint INSTALLFLAG_FORCE = 0x1;
    public const uint INSTALLFLAG_NONINTERACTIVE = 0x4;
    public static readonly nint InvalidHandle = -1;

    [LibraryImport("setupapi.dll", SetLastError = true)]
    public static partial nint SetupDiCreateDeviceInfoList(in Guid classGuid, nint hwndParent);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiCreateDeviceInfoW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupDiCreateDeviceInfo(nint set, string deviceName, in Guid classGuid, string? description, nint hwndParent, uint flags, ref SP_DEVINFO_DATA data);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiOpenDeviceInfoW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupDiOpenDeviceInfo(nint set, string deviceInstanceId, nint hwndParent, uint flags, ref SP_DEVINFO_DATA data);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiSetDeviceRegistryPropertyW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupDiSetDeviceRegistryProperty(nint set, ref SP_DEVINFO_DATA data, uint property, byte* buffer, uint size);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupDiCallClassInstaller(uint function, nint set, ref SP_DEVINFO_DATA data);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupDiDestroyDeviceInfoList(nint set);

    /// <summary>Installs the driver an INF names on every present device with this hardware id.</summary>
    [LibraryImport("newdev.dll", EntryPoint = "UpdateDriverForPlugAndPlayDevicesW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UpdateDriverForPlugAndPlayDevices(nint hwndParent, string hardwareId, string infPath, uint flags, [MarshalAs(UnmanagedType.Bool)] out bool rebootRequired);

    public const uint CM_LOCATE_DEVNODE_NORMAL = 0;
    public const uint CM_DISABLE_UI_NOT_OK = 0x4;
    public const uint CM_DISABLE_PERSIST = 0x8;
    public const int CR_SUCCESS = 0;

    /// <summary>DN_STARTED: the device is running.</summary>
    public const uint DN_STARTED = 0x8;

    /// <summary>Finds a device node; a disabled one is still present.</summary>
    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int CM_Locate_DevNode(out uint devInst, string deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    public static partial int CM_Enable_DevNode(uint devInst, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    public static partial int CM_Disable_DevNode(uint devInst, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    public static partial int CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW")]
    public static partial int CM_Get_Device_ID(uint devInst, char* buffer, uint length, uint flags);

    public const uint SPOST_PATH = 1;
    public const uint SUOI_FORCEDELETE = 1;

    /// <summary>
    /// Imports a driver package into the driver store, and names the <c>oemNN.inf</c> it became there (the same
    /// name again when the package is already there).
    /// </summary>
    [LibraryImport("setupapi.dll", EntryPoint = "SetupCopyOEMInfW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupCopyOEMInf(
        string sourceInfFileName,
        string? oemSourceMediaLocation,
        uint oemSourceMediaType,
        uint copyStyle,
        char* destinationInfFileName,
        uint destinationInfFileNameSize,
        out uint requiredSize,
        nint destinationInfFileNameComponent);

    /// <summary>Takes a driver package out of the driver store, by its <c>oemNN.inf</c> name.</summary>
    [LibraryImport("setupapi.dll", EntryPoint = "SetupUninstallOEMInfW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupUninstallOEMInf(string infFileName, uint flags, nint reserved);
}
