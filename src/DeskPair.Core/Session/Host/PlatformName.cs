namespace DeskPair.Core.Session.Host;

/// <summary>
/// What this machine calls itself to the other end. It is shown beside the peer's name and picks the logo
/// next to it, so on Linux it names the distribution rather than the kernel: "Linux" identifies almost
/// nothing a user recognises, while "Ubuntu 24.04" or "Debian GNU/Linux 12" identifies the machine they
/// think they are connecting to.
/// </summary>
public static class PlatformName
{
    private static string? _cached;

    public static string Current => _cached ??= Detect();

    private static string Detect()
    {
        if (OperatingSystem.IsWindows())
        {
            return "Windows";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "macOS";
        }

        if (OperatingSystem.IsAndroid())
        {
            return "Android";
        }

        if (OperatingSystem.IsIOS())
        {
            return "iOS";
        }

        return OperatingSystem.IsLinux() ? Distribution() ?? "Linux" : "Unknown";
    }

    /// <summary>
    /// PRETTY_NAME out of os-release, which every systemd distribution has and most others copied. Missing,
    /// unreadable or empty is normal enough not to be worth a log line: the caller falls back to "Linux".
    /// </summary>
    private static string? Distribution()
    {
        foreach (string path in (string[])["/etc/os-release", "/usr/lib/os-release"])
        {
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                foreach (string line in File.ReadLines(path))
                {
                    if (!line.StartsWith("PRETTY_NAME=", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    string value = line["PRETTY_NAME=".Length..].Trim().Trim('"');
                    if (value.Length > 0)
                    {
                        return value;
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        return null;
    }
}
