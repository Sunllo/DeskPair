using System.Text;
using DeskPair.Platform.Abstractions.Hosting;
using DeskPair.Platform.MacOS.Native;

namespace DeskPair.Platform.MacOS.Hosting;

/// <summary>
/// Everything worth knowing when the macOS host will not start, gathered in one line.
///
/// On a Mac the answer is almost always one of three: the native shim did not load, Screen Recording has
/// not been granted, or this process is not the one sitting at the screen. The first is a packaging fault,
/// the second is a thing the user must click, and the third is a thing only the program can fix -- and
/// they are indistinguishable from "capture failed" alone.
///
/// <c>console</c> is here for the arrangement this phase adds: a job loaded into both the login window and
/// the desktop session runs as root in one and as the user in the other, and knowing which of those this
/// process is will be the first question asked of any report from it.
/// </summary>
public static class MacSessionDiagnostics
{
    public static string Describe()
    {
        var text = new StringBuilder();
        text.Append("shim=").Append(MacShim.IsAvailable ? "loaded" : "NOT loaded");

        if (MacShim.IsAvailable)
        {
            // Only meaningful once the shim is there; asking before that throws, which is not a diagnostic.
            PlatformPermissions granted = new MacPlatformInfo().GrantedPermissions();
            text.Append(" screen-recording=").Append(granted.HasFlag(PlatformPermissions.ScreenRecording) ? "granted" : "NOT granted");
            text.Append(" accessibility=").Append(granted.HasFlag(PlatformPermissions.Accessibility) ? "granted" : "NOT granted");
            text.Append(" console=").Append(ConsoleUser());
        }

        text.Append(" euid=").Append(Environment.GetEnvironmentVariable("UID") ?? (MacShim.IsAvailable && MacShim.fd_is_root() != 0 ? "0" : "non-root"));
        return text.ToString();
    }

    /// <summary>Who is sitting at the screen: a name, or "(login window)" when nobody has signed in yet.</summary>
    private static string ConsoleUser()
    {
        try
        {
            byte[] name = new byte[256];
            if (MacShim.fd_console_user(name, name.Length, out uint uid) == 0)
            {
                return "(unknown)";
            }

            // uid 0 at the console is how macOS says the login window is up, which is not an error here:
            // it is precisely the state the unattended arrangement exists to serve.
            string who = System.Text.Encoding.UTF8.GetString(name).TrimEnd('\0');
            return uid == 0 ? "(login window)" : $"{who}/{uid}";
        }
        catch (Exception)
        {
            return "(unreadable)";
        }
    }
}
