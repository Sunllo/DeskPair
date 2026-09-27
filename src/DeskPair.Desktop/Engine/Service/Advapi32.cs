using System.Runtime.InteropServices;

namespace DeskPair.Desktop.Engine.Service;

/// <summary>
/// The service control manager, by hand.
///
/// <c>sc.exe</c> would have done this and would have matched <see cref="FirewallRules"/>'s use of
/// <c>netsh</c>, but its <c>binPath=</c> argument has to carry a quoted executable path and a command-line
/// argument inside one token, with a mandatory space after the equals sign -- a shape that survives being
/// written down and then breaks the first time a path contains a space. The API takes the two apart.
/// </summary>
internal static partial class Advapi32
{
    internal const uint ScManagerConnect = 0x0001;
    internal const uint ScManagerCreateService = 0x0002;

    internal const uint ServiceQueryConfig = 0x0001;
    internal const uint ServiceChangeConfig = 0x0002;
    internal const uint ServiceQueryStatus = 0x0004;
    internal const uint ServiceStart = 0x0010;
    internal const uint ServiceStop = 0x0020;
    internal const uint Delete = 0x00010000;

    internal const uint ServiceWin32OwnProcess = 0x00000010;
    internal const uint ServiceAutoStart = 0x00000002;
    internal const uint ServiceErrorNormal = 0x00000001;

    internal const int ConfigDescription = 1;
    internal const int ConfigFailureActions = 2;

    /// <summary>Restart the service after a wait. The only action this needs.</summary>
    internal const int ActionRestart = 1;

    internal const uint StateStopped = 0x00000001;
    internal const uint StateStartPending = 0x00000002;
    internal const uint StateRunning = 0x00000004;

    internal const int ErrorServiceExists = 1073;
    internal const int ErrorServiceDoesNotExist = 1060;
    internal const int ErrorServiceMarkedForDelete = 1072;
    internal const int ErrorServiceNotActive = 1062;
    internal const int ErrorAccessDenied = 5;

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial nint OpenSCManager(string? machineName, string? databaseName, uint access);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial nint OpenService(nint manager, string serviceName, uint access);

    [LibraryImport("advapi32.dll", EntryPoint = "CreateServiceW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial nint CreateService(
        nint manager,
        string serviceName,
        string displayName,
        uint access,
        uint serviceType,
        uint startType,
        uint errorControl,
        string binaryPath,
        string? loadOrderGroup,
        nint tagId,
        string? dependencies,
        string? serviceStartName,
        string? password);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteService(nint service);

    [LibraryImport("advapi32.dll", EntryPoint = "StartServiceW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool StartService(nint service, uint argc, nint argv);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ControlService(nint service, uint control, ref ServiceStatus status);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryServiceStatus(nint service, ref ServiceStatus status);

    [LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ChangeServiceConfig2(nint service, int infoLevel, nint info);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseServiceHandle(nint handle);

    internal const uint ControlStop = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    internal struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ServiceDescription
    {
        public nint Description;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ScAction
    {
        public int Type;
        public uint Delay;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ServiceFailureActions
    {
        public uint ResetPeriod;
        public nint RebootMessage;
        public nint Command;
        public uint ActionCount;
        public nint Actions;
    }
}
