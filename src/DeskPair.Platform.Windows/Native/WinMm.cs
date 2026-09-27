using System.Runtime.InteropServices;

namespace DeskPair.Platform.Windows.Native;

#pragma warning disable CA1401, SYSLIB1054

/// <summary>
/// Multimedia timer resolution. The default 15.6 ms scheduler tick quantises every sleep and wait in the
/// process; while a capture loop runs we ask for 1 ms so frame pacing and the DXGI timeout wait are precise.
/// Reference counted so several capturers share one request.
/// </summary>
public static partial class WinMm
{
    private static readonly object Lock = new();
    private static int _refs;

    public static void BeginPeriod()
    {
        lock (Lock)
        {
            if (_refs++ == 0)
            {
                _ = timeBeginPeriod(1);
            }
        }
    }

    public static void EndPeriod()
    {
        lock (Lock)
        {
            if (_refs > 0 && --_refs == 0)
            {
                _ = timeEndPeriod(1);
            }
        }
    }

    [LibraryImport("winmm.dll")]
    private static partial uint timeBeginPeriod(uint uPeriod);

    [LibraryImport("winmm.dll")]
    private static partial uint timeEndPeriod(uint uPeriod);
}
