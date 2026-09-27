using System.Text;
using DeskPair.Desktop.Services;

namespace DeskPair.Desktop.Engine.MacService;

/// <summary>
/// The one launchd job that makes this desk reachable when nobody is signed in.
///
/// The arrangement is not Windows's, and it is smaller. There, a service running as LocalSystem creates an
/// engine in whichever session has the screen and moves it when the screen locks; that machinery exists
/// because Windows has nothing that will do it. launchd does: an agent limited to
/// <c>LimitLoadToSessionType = [LoginWindow, Aqua]</c> is loaded into the login window's session when
/// nobody is signed in and into the desktop session when somebody is, as the same executable with the same
/// argv, and nothing of ours has to notice which one it is in.
///
/// There is deliberately no root daemon beside it, and the reasons for having one did not survive being
/// tested. It could not re-load a mis-placed agent, because launchd refuses to bootstrap anything into the
/// login window's domain at all ("Domain does not support specified action"); and the registration does
/// not need repairing, because one <c>launchctl load -w -S LoginWindow</c> from a desktop session was
/// enough for the job to run at the next sign-out. That left "something stable to observe", which a plist
/// on disk already is. A root process that runs for ever and does nothing is not free: it is one more
/// thing with every privilege and no purpose.
///
/// Generating the XML rather than shipping a template keeps the path, the argv and the reasons together,
/// and makes the decisions testable without a Mac.
/// </summary>
internal static class MacLaunchdJobs
{
    /// <summary>
    /// The job with a screen.
    ///
    /// A LaunchAgent rather than a daemon because only an agent can be limited to a session type, and that
    /// limit is the entire mechanism by which anything of ours ends up in the session holding the login
    /// window. At the login window it runs as root; once somebody signs in, the same job runs as them --
    /// which is why the data directory is passed explicitly. Without it the two would take
    /// <c>DefaultDataDir</c>'s two different answers, and the same job would be two machines.
    /// </summary>
    public static string Agent(string executable, string dataDir) => Plist(
        UnattendedInstall.MacAgentLabel,
        [executable, "--server", "--data", dataDir],
        sessionTypes: ["LoginWindow", "Aqua"]);

    private static string Plist(string label, string[] arguments, string[]? sessionTypes)
    {
        var text = new StringBuilder();
        text.AppendLine("""<?xml version="1.0" encoding="UTF-8"?>""");
        text.AppendLine("""<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">""");
        text.AppendLine("""<plist version="1.0">""");
        text.AppendLine("<dict>");
        text.AppendLine($"    <key>Label</key><string>{Escape(label)}</string>");

        // The executable directly, never wrapped in /bin/sh -c the way the reference implementation does
        // it. A wrapper makes sh the program launchd started and the program TCC sees, which throws away
        // both the attribution in Login Items and the screen-recording consent the app was granted.
        text.AppendLine("    <key>ProgramArguments</key>");
        text.AppendLine("    <array>");
        foreach (string argument in arguments)
        {
            text.AppendLine($"        <string>{Escape(argument)}</string>");
        }

        text.AppendLine("    </array>");

        if (sessionTypes is not null)
        {
            text.AppendLine("    <key>LimitLoadToSessionType</key>");
            text.AppendLine("    <array>");
            foreach (string type in sessionTypes)
            {
                text.AppendLine($"        <string>{Escape(type)}</string>");
            }

            text.AppendLine("    </array>");
        }

        text.AppendLine("    <key>RunAtLoad</key><true/>");

        // Restart it when it fails, and leave it alone when it finishes cleanly. That second half is the
        // important one: the engine exits 0 on purpose when the machine-wide store is not its to read,
        // which is what a second account's session looks like after a fast user switch. Under a plain
        // KeepAlive that would be a restart every few seconds, for ever, with the reason in a log nobody
        // has been given a reason to open. Windows has already produced one loop of exactly that shape.
        text.AppendLine("    <key>KeepAlive</key>");
        text.AppendLine("    <dict><key>SuccessfulExit</key><false/></dict>");

        // Attribution, not authorisation: this is what puts the job under DeskPair in Login Items &
        // Extensions instead of leaving an unexplained background item. It also means somebody can turn it
        // off there, which is the right trade -- a remote-access daemon a user cannot find is worse.
        text.AppendLine("    <key>AssociatedBundleIdentifiers</key>");
        text.AppendLine($"    <array><string>{Escape(UnattendedInstall.MacBundleIdentifier)}</string></array>");

        // Deliberately no StandardOutPath or StandardErrorPath. The engine already writes its own log
        // under the data directory; a second copy, owned by root and readable by everyone, would be one
        // more place for a password reset link or a peer id to sit where it was not meant to.
        text.AppendLine("</dict>");
        text.AppendLine("</plist>");
        return text.ToString();
    }

    private static string Escape(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);
}
