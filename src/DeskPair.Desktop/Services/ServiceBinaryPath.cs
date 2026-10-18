namespace DeskPair.Desktop.Services;

/// <summary>
/// The command line the host service is registered with, and whether one already registered runs this program.
///
/// The service is registered against whichever DeskPair.exe the person was running when they turned unattended
/// access on: a copy unpacked from the zip, a test build, anything. Installing the MSI afterwards puts a new
/// DeskPair.exe in Program Files and replaces it on every update, but the service keeps starting the old one
/// wherever it was -- a newer app in front of an older engine, and no engine at all once that folder is deleted.
/// So an install compares the registered command line with its own and, when they differ, points the service
/// at itself (<c>HostServiceInstaller.Adopt</c>, Windows only). Pure and compiled everywhere, so the comparison is
/// tested without a service.
/// </summary>
public static class ServiceBinaryPath
{
    /// <summary>The command line that runs <paramref name="exe"/> as the service: quoted, because paths have spaces.</summary>
    public static string For(string exe) => "\"" + exe + "\" --service";

    /// <summary>
    /// The executable a registered command line starts: the first token, quoted or not. Null when there is none.
    /// </summary>
    public static string? ExecutableOf(string? binaryPath)
    {
        if (string.IsNullOrWhiteSpace(binaryPath))
        {
            return null;
        }

        string s = binaryPath.Trim();
        if (s[0] == '"')
        {
            int end = s.IndexOf('"', 1);
            return end < 0 ? s[1..] : s[1..end];
        }

        // Unquoted: Windows would try every prefix ending at a space; the whole first token is the only sane reading.
        int space = s.IndexOf(' ');
        return space < 0 ? s : s[..space];
    }

    /// <summary>
    /// Whether <paramref name="binaryPath"/> runs <paramref name="exe"/>: the same file, however the path is
    /// spelled (case, trailing separators; 8.3 names are not resolved -- an exact file is what the installer wrote).
    /// </summary>
    public static bool Runs(string? binaryPath, string exe)
    {
        string? registered = ExecutableOf(binaryPath);
        if (registered is null)
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(registered).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(exe).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
