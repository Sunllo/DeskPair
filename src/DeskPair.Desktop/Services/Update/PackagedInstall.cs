namespace DeskPair.Desktop.Services.Update;

/// <summary>
/// Whether a package manager installed this program. The .deb, .rpm and Arch packages and the Windows installer put a
/// file named <c>packaged</c> beside it, holding the package format.
///
/// Such an install is the package manager's to update. Replacing files it owns would leave its database describing a
/// program that is no longer there, and its next upgrade would put its own version back over ours. So a Linux package
/// is told about a new version and sent to the download page, and is never replaced in place; a copy Windows Installer
/// put here fetches the next installer, checks it like any other download and hands it to Windows Installer, which
/// does the replacing.
/// </summary>
internal static class PackagedInstall
{
    /// <summary>What the Windows installer writes: a copy that updates by running the next installer.</summary>
    public const string WindowsInstaller = "msi";

    /// <summary>The package format ("deb", "rpm", "archlinux", "msi"), or null when no package manager installed this program.</summary>
    public static string? Format { get; } = FormatIn(AppContext.BaseDirectory);

    public static string? FormatIn(string directory)
    {
        string marker = Path.Combine(directory, "packaged");
        try
        {
            if (!File.Exists(marker))
            {
                return null;
            }

            string format = File.ReadAllText(marker).Trim();
            return format.Length > 0 ? format : "package";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
