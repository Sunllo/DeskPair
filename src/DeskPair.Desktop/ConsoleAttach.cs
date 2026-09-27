using System.Runtime.InteropServices;

namespace DeskPair.Desktop;

/// <summary>
/// Lets the command-line roles write to the console that started them. DeskPair is a <c>WinExe</c> so that
/// opening it never flashes a console window, which also means Windows gives it no console of its own; the
/// roles that are meant to be run from a shell borrow the caller's.
///
/// The shell does not wait for a <c>WinExe</c>, so its prompt comes back before the output does. That is the
/// price of not flashing a window on every normal launch; the log file is the reliable record either way.
/// </summary>
internal static partial class ConsoleAttach
{
    private const uint AttachParentProcess = 0xFFFFFFFF;
    private const int StdOutputHandle = -11;

    /// <summary>Gives this process somewhere to write. False when there is nowhere — a double-click, or an elevated relaunch.</summary>
    public static bool AttachToParent()
    {
        // Runtime check rather than #if WINDOWS: WinExe applies to the net10.0 target too, where that symbol
        // is not defined. Everywhere else the apphost is an ordinary terminal binary and stdout already works.
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        // Run from a shell, a WinExe still inherits the standard handles it was given -- a console, but also
        // the pipe or file behind "--version > version.txt". Those must be left alone; replacing them with
        // the console device would throw the caller's redirection away.
        if (HasStandardOutput())
        {
            return true;
        }

        try
        {
            if (!AttachConsole(AttachParentProcess))
            {
                return false;
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }

        try
        {
            // Attached to a console but still holding no handles of our own: write to the console device.
            var console = new StreamWriter(new FileStream(@"\\.\CONOUT$", FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
            Console.SetOut(console);
            Console.SetError(console);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }

        return true;
    }

    private static bool HasStandardOutput()
    {
        nint handle = GetStdHandle(StdOutputHandle);
        return handle != 0 && handle != -1;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GetStdHandle(int stdHandle);
}
