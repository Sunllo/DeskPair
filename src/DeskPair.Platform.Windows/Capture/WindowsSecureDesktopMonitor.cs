using System.Diagnostics;
using Microsoft.Extensions.Logging;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Windows.Native;

namespace DeskPair.Platform.Windows.Capture;

/// <summary>
/// Detects the Windows secure desktop by the one thing that matters to the engine: whether it can open the
/// desktop currently taking input. A user-mode (app) engine may not open Winlogon's secure desktop, so
/// <see cref="User32.OpenInputDesktop"/> fails exactly while a UAC prompt or the lock screen is up -- the
/// same desktop the capturer's BitBlt and the injector's SendInput cannot reach. A SYSTEM engine can open it,
/// so this reports <see cref="SecureDesktopKind.None"/> for the unattended service, which is correct: that
/// engine shows and drives the secure desktop itself and has nothing to warn about.
/// </summary>
public sealed class WindowsSecureDesktopMonitor : ISecureDesktopMonitor
{
    private readonly ILogger _log;
    private readonly int _session;

    public WindowsSecureDesktopMonitor(ILogger log)
    {
        _log = log;
        _session = SafeSessionId();
    }

    public SecureDesktopState Poll()
    {
        // GENERIC_ALL matches what the capturer asks for when it attaches (GdiScreenCapturer.AttachToInputDesktop),
        // so the probe succeeds exactly when the engine could actually read the desktop taking input.
        nint desktop = User32.OpenInputDesktop(0, 0, User32.GENERIC_ALL);
        if (desktop == 0)
        {
            // The input desktop is one this process may not open: an app-mode engine on the secure desktop a UAC
            // prompt or the lock screen draws on. Which of the two, so the banner can say -- LogonUI runs the lock
            // / sign-in screen, a bare elevation prompt runs consent.exe. When neither is found (a brief
            // Ctrl+Alt+Del screen, or a race with the switch) call it a UAC prompt: the viewer only needs to know
            // the screen is one this engine cannot show.
            SecureDesktopKind blind = HasProcess("LogonUI") ? SecureDesktopKind.Locked : SecureDesktopKind.Uac;
            return new SecureDesktopState(blind, OnSecureDesktop: true);
        }

        // Readable: an ordinary desktop, or a SYSTEM engine on the secure one, which it may read. Tell them apart
        // by the desktop's name -- the secure desktop is winlogon's "Winlogon" -- so a SYSTEM engine still knows it
        // is on the secure desktop and can gate who may see it, even though it has no banner to raise (Kind=None).
        string? name = DesktopName(desktop);
        User32.CloseDesktop(desktop);
        bool secure = name is not null && name.Equals("Winlogon", StringComparison.OrdinalIgnoreCase);
        return new SecureDesktopState(SecureDesktopKind.None, secure);
    }

    private static unsafe string? DesktopName(nint desktop)
    {
        const int UOI_NAME = 2;
        byte* buffer = stackalloc byte[256];
        return User32.GetUserObjectInformation(desktop, UOI_NAME, buffer, 256, out _) == 0
            ? null
            : new string((char*)buffer);
    }

    private bool HasProcess(string name)
    {
        Process[] found;
        try
        {
            found = Process.GetProcessesByName(name);
        }
        catch (Exception e)
        {
            _log.LogDebug(e, "Could not enumerate {Name} to name the secure desktop", name);
            return false;
        }

        try
        {
            foreach (Process p in found)
            {
                try
                {
                    if (p.SessionId == _session)
                    {
                        return true;
                    }
                }
                catch
                {
                    // The process ended between the enumeration and the read: not the one we are after.
                }
            }

            return false;
        }
        finally
        {
            foreach (Process p in found)
            {
                p.Dispose();
            }
        }
    }

    private int SafeSessionId()
    {
        try
        {
            using Process me = Process.GetCurrentProcess();
            return me.SessionId;
        }
        catch (Exception e)
        {
            _log.LogDebug(e, "Could not read this process's session; the secure-desktop kind may be less precise");
            return -1;
        }
    }
}
