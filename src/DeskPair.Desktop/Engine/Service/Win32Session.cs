using System.Runtime.InteropServices;

namespace DeskPair.Desktop.Engine.Service;

/// <summary>
/// The calls needed to put a process on somebody else's desktop: which session has the screen, which
/// desktop that session is showing, and how to borrow a token that is allowed to draw on it.
/// </summary>
internal static partial class Win32Session
{
    // ---- sessions ----

    /// <summary>The session attached to the physical console, or 0xFFFFFFFF when there is none.</summary>
    [LibraryImport("kernel32.dll")]
    internal static partial uint WTSGetActiveConsoleSessionId();

    internal const uint NoSession = 0xFFFFFFFF;

    // ---- processes and tokens ----

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenProcessToken(nint process, uint access, out nint token);

    [LibraryImport("advapi32.dll", EntryPoint = "DuplicateTokenEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DuplicateTokenEx(
        nint existing,
        uint access,
        nint attributes,
        int impersonationLevel,
        int tokenType,
        out nint duplicate);

    [LibraryImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CreateEnvironmentBlock(out nint environment, nint token, [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [LibraryImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyEnvironmentBlock(nint environment);

    /// <summary>
    /// Every string here is a pointer the caller owns rather than a marshalled <c>string</c>.
    ///
    /// Two reasons, and both of them are the API's. CreateProcess is documented to write into the command
    /// line buffer it is given, which a marshalled literal must never be; and StartupInfo carries string
    /// fields, which makes it non-blittable and so not something a source-generated P/Invoke will marshal
    /// at all.
    /// </summary>
    [LibraryImport("advapi32.dll", EntryPoint = "CreateProcessAsUserW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CreateProcessAsUser(
        nint token,
        nint applicationName,
        nint commandLine,
        nint processAttributes,
        nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        nint environment,
        nint currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TerminateProcess(nint process, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetExitCodeProcess(nint process, out uint exitCode);

    internal const uint StillActive = 259;

    internal const uint ProcessQueryInformation = 0x0400;
    internal const uint ProcessQueryLimitedInformation = 0x1000;
    internal const uint ProcessTerminate = 0x0001;
    internal const uint Synchronize = 0x00100000;

    internal const uint TokenDuplicate = 0x0002;
    internal const uint TokenQuery = 0x0008;
    internal const uint TokenAssignPrimary = 0x0001;
    internal const uint TokenAdjustDefault = 0x0080;
    internal const uint TokenAdjustSessionId = 0x0100;

    internal const int SecurityImpersonation = 2;
    internal const int TokenPrimary = 1;

    internal const uint CreateUnicodeEnvironment = 0x00000400;
    internal const uint CreateNoWindow = 0x08000000;
    internal const uint CreateNewConsole = 0x00000010;

    [StructLayout(LayoutKind.Sequential)]
    internal struct StartupInfo
    {
        public int Size;
        public nint Reserved;

        /// <summary>
        /// Which window station and desktop the new process starts on. This is the whole point of the
        /// exercise: "winsta0\Default" is the desk somebody is signed in to, "winsta0\Winlogon" is the lock
        /// and sign-in screen, and a process is fixed to whichever it was born on.
        /// </summary>
        public nint Desktop;

        public nint Title;
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public int ConsoleColumns;
        public int ConsoleRows;
        public int FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort ReservedLength;
        public nint Reserved2;
        public nint StdInput;
        public nint StdOutput;
        public nint StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public uint ProcessId;
        public uint ThreadId;
    }
}
