using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace DeskPair.Desktop.Services;

/// <summary>
/// Writes unhandled exceptions to the desktop log and shows a plain message box, so a crash never
/// disappears silently. The log lives in %LocalAppData%\Sunllo\DeskPair\logs\desktop.log.
/// </summary>
internal static partial class CrashReporter
{
    public static string LogDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sunllo", "DeskPair", "logs");

    /// <summary>One process, one log. The headless engine keeps its own, beside its data directory.</summary>
    public static string LogPath => Path.Combine(LogDirectory, App.Role == AppRole.Server ? "server.log" : "desktop.log");

    public static void Install(ILogger log)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Report(log, e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()), fatal: e.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            log.LogError(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };
    }

    public static void Report(ILogger log, Exception exception, bool fatal)
    {
        try
        {
            log.LogCritical(exception, fatal ? "Fatal error" : "Unhandled error");
        }
        catch (Exception)
        {
        }

        if (fatal)
        {
            ShowMessage(Localization.Strings.Format("crash.body", $"{exception.GetType().Name}: {exception.Message}", LogPath));
        }
    }

    public static void ShowMessage(string text)
    {
        if (OperatingSystem.IsWindows())
        {
            MessageBoxW(0, text, Localization.Strings.Get("crash.title"), 0x10 /* MB_ICONERROR */);
        }
        else
        {
            Console.Error.WriteLine(text);
        }
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(nint hWnd, string text, string caption, uint type);
}
