using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DeskPair.Desktop.Services;

/// <summary>
/// Hands something to the desktop to open.
///
/// The only two things this application ever opens are a folder it computed and a web page it built from
/// its own configuration. Never a string that arrived over the network -- and the scheme check is what
/// keeps that true rather than merely customary. It is also why the update manifest needs no signature:
/// a forged one carries no URL, and if a URL field were ever added and passed here, everything that is
/// not http or https on the expected host is refused.
///
/// Avalonia's <c>TopLevel.Launcher</c> is deliberately not used. It needs a <c>TopLevel</c>, and these
/// view models are kept clear of the window on purpose so a settings tab can be tested headless; and it
/// has no allow-list, which is the entire point of this class.
/// </summary>
public static class Launcher
{
    /// <summary>Opens a web page. Returns false rather than throwing, so a caller can say it failed.</summary>
    public static bool TryOpenUrl(string? url, string? expectedHost = null)
    {
        if (!IsSafeUrl(url, expectedHost, out Uri? parsed))
        {
            return false;
        }

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                using Process? started = Process.Start(
                    new ProcessStartInfo(parsed!.AbsoluteUri) { UseShellExecute = true });
                return true;
            }

            // The URL is passed as an argument rather than through a shell, on both of these.
            string opener = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "open" : "xdg-open";
            using Process? process = Process.Start(new ProcessStartInfo(opener, parsed!.AbsoluteUri)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException
                                     or PlatformNotSupportedException or IOException)
        {
            // A box with no xdg-utils, or no registered browser. The caller shows the address instead.
            return false;
        }
    }

    /// <summary>Opens a folder, creating it first, because an absent folder opens nothing.</summary>
    public static bool TryOpenFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(path);
            using Process? started = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException
                                     or IOException or UnauthorizedAccessException
                                     or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether this is a web page, and the one expected.
    ///
    /// Refusing everything that is not http or https rules out <c>file:</c>, <c>javascript:</c>,
    /// <c>ms-settings:</c> and every other registered protocol handler on the machine in a single line,
    /// which is worth more than a list of the ones thought of today.
    /// </summary>
    internal static bool IsSafeUrl(string? url, string? expectedHost, out Uri? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        // A carriage return or newline can smuggle a second argument past some handlers.
        foreach (char c in url)
        {
            if (c < 0x20 || c == 0x7F)
            {
                return false;
            }
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // https://deskpair.app@evil.example/ reads as deskpair.app and goes to evil.example.
        if (uri.UserInfo.Length > 0 || uri.Host.Length == 0)
        {
            return false;
        }

        if (expectedHost is not null && !string.Equals(uri.Host, expectedHost, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        parsed = uri;
        return true;
    }
}
