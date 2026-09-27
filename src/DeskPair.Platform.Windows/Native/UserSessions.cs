using System.Runtime.InteropServices;

namespace DeskPair.Platform.Windows.Native;

#pragma warning disable CA1401, SYSLIB1054, IDE1006

/// <summary>The signed-in user's token and what goes with it, for a shell started as them by a SYSTEM engine.</summary>
internal static unsafe partial class UserSessions
{
    public const uint NoSession = 0xFFFFFFFF;
    private const int WTSActive = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_SESSION_INFOW
    {
        public uint SessionId;
        public nint pWinStationName;
        public int State;
    }

    [LibraryImport("kernel32.dll")]
    public static partial uint WTSGetActiveConsoleSessionId();

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    public static partial int WTSQueryUserToken(uint sessionId, out nint token);

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    private static partial int WTSEnumerateSessionsW(nint server, uint reserved, uint version, out WTS_SESSION_INFOW* sessions, out uint count);

    [LibraryImport("wtsapi32.dll")]
    private static partial void WTSFreeMemory(void* memory);

    [LibraryImport("userenv.dll", SetLastError = true)]
    public static partial int CreateEnvironmentBlock(out nint environment, nint token, int inherit);

    [LibraryImport("userenv.dll", SetLastError = true)]
    public static partial int DestroyEnvironmentBlock(nint environment);

    [LibraryImport("userenv.dll", SetLastError = true)]
    public static partial int GetUserProfileDirectoryW(nint token, char* directory, ref uint size);

    [LibraryImport("advapi32.dll", EntryPoint = "CreateProcessAsUserW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int CreateProcessAsUserW(
        nint token, string? applicationName, char* commandLine, nint processAttributes, nint threadAttributes, int inheritHandles,
        uint creationFlags, nint environment, string? currentDirectory, ref ConPty.STARTUPINFOEXW startupInfo, out ConPty.PROCESS_INFORMATION processInformation);

    /// <summary>
    /// The signed-in user's primary token: the console's user first, then any other active session (a
    /// machine used only over Remote Desktop has nobody at the console). Zero when nobody is signed in.
    /// Needs SeTcbPrivilege, which is to say SYSTEM.
    /// </summary>
    public static nint SignedInUserToken()
    {
        uint console = WTSGetActiveConsoleSessionId();
        if (console != NoSession && WTSQueryUserToken(console, out nint token) != 0)
        {
            return token;
        }

        if (WTSEnumerateSessionsW(0, 0, 1, out WTS_SESSION_INFOW* sessions, out uint count) == 0)
        {
            return 0;
        }

        try
        {
            for (int i = 0; i < count; i++)
            {
                if (sessions[i].State == WTSActive && sessions[i].SessionId != console && WTSQueryUserToken(sessions[i].SessionId, out nint other) != 0)
                {
                    return other;
                }
            }
        }
        finally
        {
            WTSFreeMemory(sessions);
        }

        return 0;
    }

    public static string? ProfileDirectory(nint token)
    {
        uint size = 512;
        char* buffer = stackalloc char[512];
        return GetUserProfileDirectoryW(token, buffer, ref size) != 0 ? new string(buffer) : null;
    }
}
