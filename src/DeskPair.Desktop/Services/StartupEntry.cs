using DeskPair.Desktop.Localization;

namespace DeskPair.Desktop.Services;

/// <summary>
/// The per-user "start when I sign in" entry. It is what replaces an installed service: the desk becomes
/// reachable once the user signs in, never before, which is what the settings page says.
///
/// Windows uses the HKCU Run key, macOS a LaunchAgent in the user's own <c>~/Library/LaunchAgents</c>, and
/// Linux an XDG autostart desktop entry in <c>~/.config/autostart</c>. None of the three needs administrator
/// rights, which is the point: this replaced an installed service, not the other way round.
/// </summary>
public static class StartupEntry
{
#if WINDOWS
    private const string EntryName = "DeskPair";
#endif

    /// <summary>
    /// Deliberately not <c>com.sunllo.deskpair</c>: that is the label an older version's launch agent used,
    /// and <see cref="LegacyInstall"/> removes it at startup. Sharing it would make the app delete its own
    /// login entry every time it ran.
    /// </summary>
    private const string LaunchAgentLabel = "com.sunllo.deskpair.login";

    public static bool IsSupported =>
#if WINDOWS
        OperatingSystem.IsWindows();
#else
        OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();
#endif

    /// <summary>Reads what the system actually holds, so an entry removed elsewhere shows as off.</summary>
    public static bool IsEnabled()
    {
#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(EntryName) is string value && value.Length > 0;
        }
#else
        if (OperatingSystem.IsMacOS())
        {
            return File.Exists(LaunchAgentPath);
        }

        if (OperatingSystem.IsLinux())
        {
            return File.Exists(AutostartPath);
        }
#endif
        return false;
    }

    /// <summary>Writes or removes the entry; returns false with a reason when the system refused.</summary>
    public static bool TryApply(bool enabled, bool minimised, out string error)
    {
        error = string.Empty;
#if WINDOWS
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            using Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (!enabled)
            {
                key.DeleteValue(EntryName, throwOnMissingValue: false);
                return true;
            }

            string exe = Environment.ProcessPath ?? string.Empty;
            if (exe.Length == 0)
            {
                error = "The application path is unknown.";
                return false;
            }

            key.SetValue(EntryName, minimised ? $"\"{exe}\" --minimised" : $"\"{exe}\"", Microsoft.Win32.RegistryValueKind.String);
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            error = e.Message;
            return false;
        }
#else
        if (OperatingSystem.IsMacOS())
        {
            return enabled ? EnableAgent(minimised, out error) : DisableAgent(out error);
        }

        if (OperatingSystem.IsLinux())
        {
            return enabled ? EnableAutostart(minimised, out error) : DisableAutostart(out error);
        }

        _ = enabled;
        _ = minimised;
        return true;
#endif
    }

#if WINDOWS
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
#else
    private static string LaunchAgentPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "LaunchAgents", $"{LaunchAgentLabel}.plist");

    private static bool EnableAgent(bool minimised, out string error)
    {
        error = string.Empty;
        string exe = Environment.ProcessPath ?? string.Empty;
        if (exe.Length == 0)
        {
            error = "The application path is unknown.";
            return false;
        }

        // Screen recording and accessibility are granted to the .app, not to a loose binary, so an entry
        // pointing outside a bundle would start something the consents do not cover — a black screen with no
        // explanation. Refuse rather than write it.
        if (!IsInsideAppBundle(exe))
        {
            error = Strings.Get("settings.startupNeedsBundle");
            return false;
        }

        string path = LaunchAgentPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Plist(exe, minimised));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error = e.Message;
            return false;
        }

        // Replace whatever was loaded before, so editing the entry takes effect without signing out.
        string domain = $"gui/{Engine.NativeUser.Geteuid()}";
        Launchctl($"bootout {domain}/{LaunchAgentLabel}");
        if (!Launchctl($"bootstrap {domain} \"{path}\""))
        {
            error = Strings.Get("settings.startupLaunchctl");
            return false;
        }

        return true;
    }

    private static bool DisableAgent(out string error)
    {
        error = string.Empty;
        Launchctl($"bootout gui/{Engine.NativeUser.Geteuid()}/{LaunchAgentLabel}");
        try
        {
            File.Delete(LaunchAgentPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error = e.Message;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Aqua only: the agent loads once the user has signed in and never at the login window. Reaching the
    /// login window would need the job to also declare LoginWindow and the executable to carry a
    /// __CGPreLoginApp section, which is the unattended access this product does not offer.
    ///
    /// No KeepAlive either — quitting DeskPair has to mean quit, not "launchd starts it straight back".
    /// </summary>
    private static string Plist(string exe, bool minimised)
    {
        string arguments = minimised
            ? $"    <string>{Escape(exe)}</string>\n    <string>--minimised</string>"
            : $"    <string>{Escape(exe)}</string>";
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key><string>{LaunchAgentLabel}</string>
              <key>ProgramArguments</key>
              <array>
            {arguments}
              </array>
              <key>RunAtLoad</key><true/>
              <key>LimitLoadToSessionType</key><array><string>Aqua</string></array>
              <key>ProcessType</key><string>Interactive</string>
            </dict>
            </plist>

            """;
    }

    private static string Escape(string text) => System.Security.SecurityElement.Escape(text) ?? text;

    /// <summary>…/DeskPair.app/Contents/MacOS/&lt;exe&gt; — the shape that carries the app's TCC identity.</summary>
    private static bool IsInsideAppBundle(string exe)
    {
        DirectoryInfo? macos = new FileInfo(exe).Directory;
        DirectoryInfo? contents = macos?.Parent;
        DirectoryInfo? bundle = contents?.Parent;
        return macos?.Name == "MacOS"
            && contents?.Name == "Contents"
            && bundle is not null
            && bundle.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase);
    }

    private static string AutostartPath => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config
            ? config
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "autostart", "deskpair.desktop");

    /// <summary>
    /// The XDG autostart entry every desktop environment reads. It needs no root and no systemd unit, which
    /// is what makes it the right shape for a per-user login item.
    /// </summary>
    private static bool EnableAutostart(bool minimised, out string error)
    {
        error = string.Empty;
        string exe = Environment.ProcessPath ?? string.Empty;
        if (exe.Length == 0)
        {
            error = "The application path is unknown.";
            return false;
        }

        string path = AutostartPath;
        string entry = $"""
            [Desktop Entry]
            Type=Application
            Name=Sunllo DeskPair
            Comment=Makes this computer reachable once you are signed in
            Exec="{exe}"{(minimised ? " --minimised" : string.Empty)}
            Terminal=false
            X-GNOME-Autostart-enabled=true

            """;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, entry);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error = e.Message;
            return false;
        }
    }

    private static bool DisableAutostart(out string error)
    {
        error = string.Empty;
        try
        {
            File.Delete(AutostartPath);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error = e.Message;
            return false;
        }
    }

    private static bool Launchctl(string arguments)
    {
        try
        {
            using System.Diagnostics.Process? process = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("launchctl", arguments) { UseShellExecute = false, CreateNoWindow = true });
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(5000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
#endif
}
