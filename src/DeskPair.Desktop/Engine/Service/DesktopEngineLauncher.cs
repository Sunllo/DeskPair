using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace DeskPair.Desktop.Engine.Service;

/// <summary>
/// Starting the host engine somewhere this process cannot go.
///
/// A service runs in session 0, which has no screen at all, so an engine started as a child of the service
/// would capture nothing however privileged it was. What it needs is to be born in the session attached to
/// the physical console -- which a process cannot move into afterwards, and which is why this is a launcher
/// rather than a one-time setup step.
///
/// The token comes from <c>winlogon.exe</c>. That is not a trick for getting more privilege than the
/// service has; the service is already LocalSystem. It is a trick for getting a token that is already in
/// the right session and already allowed on the secure desktop, without having to construct one.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class DesktopEngineLauncher
{
    /// <summary>A running engine, and where it was put.</summary>
    internal sealed class Engine : IDisposable
    {
        public Engine(nint process, uint processId)
        {
            Handle = process;
            ProcessId = processId;
        }

        public nint Handle { get; }

        public uint ProcessId { get; }

        public bool IsAlive =>
            Win32Session.GetExitCodeProcess(Handle, out uint code) && code == Win32Session.StillActive;

        /// <summary>What it left with. Only meaningful once <see cref="IsAlive"/> is false.</summary>
        public uint ExitCode =>
            Win32Session.GetExitCodeProcess(Handle, out uint code) ? code : Win32Session.StillActive;

        /// <summary>
        /// Ends it. There is no polite way: the engine has no window to close and no console to signal.
        /// </summary>
        public void Stop()
        {
            if (IsAlive)
            {
                Win32Session.TerminateProcess(Handle, 0);
            }
        }

        public void Dispose()
        {
            if (Handle != 0)
            {
                Win32Session.CloseHandle(Handle);
            }
        }
    }

    /// <summary>The session showing on the physical screen, or null when nobody is at it.</summary>
    internal static uint? ConsoleSession()
    {
        uint session = Win32Session.WTSGetActiveConsoleSessionId();
        return session == Win32Session.NoSession ? null : session;
    }

    /// <summary>
    /// The desk somebody signs in to, and the only one the engine is ever started on.
    ///
    /// It used to be whichever desktop was taking input, with the engine restarted onto the other one
    /// whenever that changed. That is gone: capture and injection each attach to the input desktop
    /// themselves, and the restart landed at exactly the wrong moment -- the desktop changes when the
    /// lock screen hands over to the credential prompt, so the session died every time somebody started
    /// to type a password.
    /// </summary>
    private const string Desktop = @"winsta0\Default";

    /// <summary>
    /// Starts <c>--server</c> in the console session.
    /// </summary>
    /// <returns>The running engine, or null with the reason logged.</returns>
    internal static Engine? Launch(uint session, ILogger log)
    {
        nint token = BorrowWinlogonToken(session, log);
        if (token == 0)
        {
            return null;
        }

        try
        {
            if (!Win32Session.CreateEnvironmentBlock(out nint environment, token, false))
            {
                // Not fatal. The engine reads its configuration from a path it works out itself, so an
                // inherited environment is a nicety; saying so is better than failing over it.
                log.LogDebug("Could not build an environment block (error {Error}); using none", Marshal.GetLastPInvokeError());
                environment = 0;
            }

            try
            {
                string exe = Environment.ProcessPath ?? string.Empty;
                if (exe.Length == 0)
                {
                    log.LogError("Could not determine the program path");
                    return null;
                }

                // Three buffers this side owns. The command line in particular must be writable memory of
                // our own: CreateProcess is documented to modify it in place.
                nint desktopName = Marshal.StringToHGlobalUni(Desktop);
                nint commandLine = Marshal.StringToHGlobalUni("\"" + exe + "\" --server");
                nint workingDirectory = Marshal.StringToHGlobalUni(Path.GetDirectoryName(exe) ?? string.Empty);
                try
                {
                    var startup = new Win32Session.StartupInfo
                    {
                        Size = Marshal.SizeOf<Win32Session.StartupInfo>(),
                        Desktop = desktopName,
                    };

                    bool started = Win32Session.CreateProcessAsUser(
                        token,
                        0,
                        commandLine,
                        0,
                        0,
                        false,
                        Win32Session.CreateUnicodeEnvironment | Win32Session.CreateNoWindow,
                        environment,
                        workingDirectory,
                        ref startup,
                        out Win32Session.ProcessInformation info);

                    if (!started)
                    {
                        log.LogError(
                            "Could not start the engine in session {Session} (error {Error})",
                            session,
                            Marshal.GetLastPInvokeError());
                        return null;
                    }

                    Win32Session.CloseHandle(info.Thread);
                    log.LogInformation("Engine {Process} started in session {Session}", info.ProcessId, session);
                    return new Engine(info.Process, info.ProcessId);
                }
                finally
                {
                    Marshal.FreeHGlobal(workingDirectory);
                    Marshal.FreeHGlobal(commandLine);
                    Marshal.FreeHGlobal(desktopName);
                }
            }
            finally
            {
                if (environment != 0)
                {
                    Win32Session.DestroyEnvironmentBlock(environment);
                }
            }
        }
        finally
        {
            Win32Session.CloseHandle(token);
        }
    }

    /// <summary>
    /// A primary token in the console session, copied from winlogon.
    ///
    /// There is one winlogon per session and it is always in the one this wants, so finding it is finding
    /// the session. The copy is a primary token because CreateProcessAsUser will not take an impersonation
    /// one, and it is taken rather than the service's own because the service's own belongs to session 0.
    /// </summary>
    private static nint BorrowWinlogonToken(uint session, ILogger log)
    {
        foreach (Process process in Process.GetProcessesByName("winlogon"))
        {
            using (process)
            {
                if ((uint)process.SessionId != session)
                {
                    continue;
                }

                nint handle = Win32Session.OpenProcess(Win32Session.ProcessQueryInformation, false, (uint)process.Id);
                if (handle == 0)
                {
                    log.LogDebug(
                        "Could not open winlogon {Process} (error {Error})",
                        process.Id,
                        Marshal.GetLastPInvokeError());
                    continue;
                }

                try
                {
                    const uint access = Win32Session.TokenDuplicate | Win32Session.TokenQuery
                        | Win32Session.TokenAssignPrimary | Win32Session.TokenAdjustDefault
                        | Win32Session.TokenAdjustSessionId;
                    if (!Win32Session.OpenProcessToken(handle, access, out nint token))
                    {
                        log.LogDebug("Could not open winlogon's token (error {Error})", Marshal.GetLastPInvokeError());
                        continue;
                    }

                    try
                    {
                        if (Win32Session.DuplicateTokenEx(
                                token,
                                0,
                                0,
                                Win32Session.SecurityImpersonation,
                                Win32Session.TokenPrimary,
                                out nint primary))
                        {
                            return primary;
                        }

                        log.LogDebug("Could not duplicate winlogon's token (error {Error})", Marshal.GetLastPInvokeError());
                    }
                    finally
                    {
                        Win32Session.CloseHandle(token);
                    }
                }
                finally
                {
                    Win32Session.CloseHandle(handle);
                }
            }
        }

        log.LogWarning("No winlogon process in session {Session}; nothing to launch the engine with", session);
        return 0;
    }
}
