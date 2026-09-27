using DeskPair.Platform.Linux.Hosting;
using DeskPair.Platform.Linux.Native;

namespace DeskPair.Platform.Linux.Tests;

/// <summary>
/// What this machine can actually be tested against, computed once. The Linux analogue of
/// <c>InteractiveDesktop</c> in the Windows suite, but layered: "there is a display", "capture will work" and
/// "input will work" are four different questions here, not one.
///
/// Two rules keep it honest. It queries <em>extensions</em> and never constructs a production type — a guard
/// that built an X11ScreenCapturer and caught Exception would turn every genuine capture bug into a silent
/// skip. And Wayland counts as "cannot capture": under Xwayland frames arrive and geometry assertions pass
/// while the picture is of an empty root, which is the worst outcome available, green and wrong.
/// </summary>
internal static class X11Session
{
    static X11Session()
    {
        if (!OperatingSystem.IsLinux())
        {
            Why = "not Linux";
            return;
        }

        try
        {
            // XInitThreads has to run before the first XOpenDisplay in the process, and this opens one before
            // any production type does.
            Xlib.EnsureThreadSafe();
            IsWayland = new LinuxPlatformInfo().IsWayland;

            nint dpy = Xlib.XOpenDisplay(null);
            if (dpy == 0)
            {
                Why = $"XOpenDisplay failed (DISPLAY={Environment.GetEnvironmentVariable("DISPLAY") ?? "unset"}, XAUTHORITY={Environment.GetEnvironmentVariable("XAUTHORITY") ?? "unset"})";
                return;
            }

            try
            {
                IsAvailable = true;
                HasShm = XShm.XShmQueryExtension(dpy);
                HasXTest = XTest.XTestQueryExtension(dpy, out _, out _, out _, out _);
                HasCursor = XFixes.XFixesQueryExtension(dpy, out _, out _);
                Why = $"X11 ok (wayland={IsWayland}, shm={HasShm}, xtest={HasXTest}, xfixes={HasCursor})";
            }
            finally
            {
                Xlib.XCloseDisplay(dpy);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or TypeInitializationException or BadImageFormatException)
        {
            Why = $"X libraries unavailable: {e.Message}";
        }
    }

    public static bool IsAvailable { get; }

    public static bool IsWayland { get; }

    private static bool HasShm { get; }

    private static bool HasXTest { get; }

    /// <summary>Capture needs MIT-SHM, and needs the session not to be Wayland, or it captures an empty root.</summary>
    public static bool CanCapture => IsAvailable && HasShm && !IsWayland;

    public static bool CanInject => IsAvailable && HasXTest;

    public static bool HasCursor { get; }

    /// <summary>One line naming what was found, so a skip or a failure says why.</summary>
    public static string Why { get; } = "not evaluated";

    /// <summary>Moves the real pointer of whoever is using this machine.</summary>
    public static bool AllowInput => Flag("SUNLLO_TEST_INPUT");

    /// <summary>Replaces the clipboard of whoever is using this machine.</summary>
    public static bool AllowClipboard => Flag("SUNLLO_TEST_CLIPBOARD");

    /// <summary>
    /// Turns the skips into assertions. A skipped test reports as passed, so a run where everything skipped
    /// is indistinguishable from a healthy one — set this on a machine that is supposed to have a display and
    /// a broken XAUTHORITY becomes a red test naming the reason instead of a green lie.
    /// </summary>
    public static bool Required => Flag("SUNLLO_REQUIRE_X11");

    private static bool Flag(string name) => Environment.GetEnvironmentVariable(name) is "1" or "true";
}
