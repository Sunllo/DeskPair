using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using DeskPair.Core.Config;
using DeskPair.Desktop.Services;
using DeskPair.Platform.Abstractions.Security;
using DeskPair.Platform.MacOS.Security;

namespace DeskPair.Desktop.Engine.MacService;

/// <summary>
/// Installs and removes the launchd job that makes this Mac reachable while nobody is signed in.
///
/// It is two stages, and the order is not a preference. On Windows the elevated process is still the same
/// user, so one pass can read a secret under their scope and write it under the machine's. macOS elevation
/// is <em>root, a different account</em>: it cannot open the login keychain, and anything it creates
/// belongs to root. So the system-level work happens elevated, and the secrets move afterwards, back in
/// the process that is still the person who asked.
///
/// What is being moved matters more than it looks. A machine-wide install means a different data
/// directory, and a host that starts against an empty one mints a new identity and saves it -- new key,
/// new fingerprint, and every device that had trusted this machine no longer does. Nothing fails; the
/// machine simply becomes a different machine. See <see cref="HandOver"/>.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
internal static class MacUnattendedInstaller
{
    /// <summary>The secrets that are this machine rather than this installation.</summary>
    private static readonly string[] Identity =
    [
        "identity-key",
        "peer-id",
        "machine-id",
        "password-salt",
        "password-permanent-h1",
    ];

    public static bool IsInstalled() => UnattendedInstall.IsInstalled();

    /// <summary>
    /// The whole install, from the app or from <c>--install-service</c> run by hand.
    ///
    /// Returns a process exit code, because the caller that matters is usually not a person reading a
    /// console: the settings page relaunches this and sees only the code.
    /// </summary>
    public static int Install(string[] args, string userDataDir, ILogger log)
    {
        // The elevated half re-enters here, as root, and must not run the user half: root cannot open the
        // login keychain, and a precondition check against root's own store would fail for a reason that
        // has nothing to do with the machine.
        if (Array.IndexOf(args, "--system-stage") >= 0)
        {
            string? asked = ServerRole.Arg(args, "--owner");
            if (!uint.TryParse(asked, out uint uid) || uid == 0)
            {
                log.LogError("--system-stage needs --owner <uid>, and that uid is who the machine-wide store will belong to");
                return 2;
            }

            return SystemStage(Environment.ProcessPath ?? "", uid, log);
        }

        // The password this is about to look for may still be filed under the product's previous name.
        LegacyIdentity.Migrate(userDataDir, log);

        if (!CanBeReachedUnattended(userDataDir, log))
        {
            return 2;
        }

        string executable = Environment.ProcessPath ?? "";
        if (executable.Length == 0)
        {
            log.LogError("Cannot work out where this program is, so there is nothing to put in the launchd jobs");
            return 2;
        }

        uint owner = NativeUser.Geteuid();
        if (owner == 0)
        {
            log.LogError(
                "Run this as yourself, not with sudo. The install elevates the one step that needs it, and "
                + "the steps that must not be root -- reading your keychain, and owning the machine-wide "
                + "store -- would be done by the wrong account.");
            return 2;
        }

        int code = SystemStage(executable, owner, log);
        if (code != 0)
        {
            return code;
        }

        // Now, and not before: the directory above was created by root and handed to this user, so this is
        // the first moment it can be written to, and this process is still the only one that can read the
        // login keychain it is being written from.
        HandOver(userDataDir, log);

        // The login window's agent is already registered for the next time that session exists. This is
        // the other half: start one in the session that exists now, so installing takes effect without
        // signing out.
        Run("/bin/launchctl", ["bootstrap", $"gui/{owner}", UnattendedInstall.MacAgentPlist], log);

        log.LogInformation(
            "DeskPair will now be reachable while this Mac is locked or signed out. Screen recording has to "
            + "be granted to DeskPair in System Settings for that to show a picture; it does not prompt on "
            + "its own from a launchd job.");
        return 0;
    }

    public static int Uninstall(ILogger log)
    {
        uint owner = NativeUser.Geteuid();
        if (owner != 0)
        {
            // Booting the agent out of this user's own session needs no privilege, and doing it first
            // means the engine stops before the files it was started from disappear.
            Run("/bin/launchctl", ["bootout", $"gui/{owner}/{UnattendedInstall.MacAgentLabel}"], log, quiet: true);
            return Elevate(["--uninstall-service"], log);
        }

        // Without -w. That flag writes a persistent "this label is disabled" override, and the command
        // that would clear it again exits non-zero on this system whatever it does -- so a removal that
        // used it could leave the next install placing a plist that launchd had been told to ignore.
        // Taking the file away is what removing it means; nothing needs to be remembered about it.
        Run("/bin/launchctl", ["unload", "-S", "LoginWindow", UnattendedInstall.MacAgentPlist], log, quiet: true);
        Delete(UnattendedInstall.MacAgentPlist, log);

        // The machine-wide store is left alone on purpose. It holds this machine's identity, and removing
        // the unattended install is not a reason to make the machine a different one -- somebody who put
        // it back would otherwise find every device had stopped trusting it.
        log.LogInformation("The unattended jobs are gone. {Dir} was left where it is; it holds this machine's identity.", UnattendedInstall.MacDataDirectory);
        Console.Out.WriteLine(Done);
        Console.Out.Flush();
        return 0;
    }

    /// <summary>
    /// The part that needs root: the machine-wide directory, the plist, and registering the job.
    ///
    /// Re-entered as root through osascript rather than done in-process, because there is no way to ask
    /// for privileges and keep them scoped to a few calls.
    /// </summary>
    private static int SystemStage(string executable, uint owner, ILogger log)
    {
        if (NativeUser.Geteuid() != 0)
        {
            return Elevate(["--install-service", "--system-stage", "--owner", owner.ToString()], log);
        }

        try
        {
            Directory.CreateDirectory(UnattendedInstall.MacDataDirectory);

            // Owned by the person installing, group wheel, and nobody else. root reads it because root
            // reads everything; that user reads it because it is theirs. A second account on this Mac
            // cannot, which is the whole point -- this directory holds the key that is this machine's
            // name and the hash of the password that opens it.
            //
            // The cost, stated rather than hidden: at rest this is weaker than the login keychain, which
            // is encrypted with the login password. It has to be, because the engine must read it before
            // anybody has typed that password.
            if (chown(UnattendedInstall.MacDataDirectory, owner, 0) != 0)
            {
                log.LogError("Could not give {Dir} to uid {Owner} (errno {Errno})", UnattendedInstall.MacDataDirectory, owner, Marshal.GetLastPInvokeError());
                return 2;
            }

            File.SetUnixFileMode(UnattendedInstall.MacDataDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            Write(UnattendedInstall.MacAgentPlist, MacLaunchdJobs.Agent(executable, UnattendedInstall.MacDataDirectory), log);

            // Best effort, and never the thing the install turns on.
            //
            // What actually reaches the login window is the plist being in /Library/LaunchAgents: launchd
            // scans that directory when a session is created and loads whatever matches the session type.
            // Measured on macOS 26 -- the agent ran at the next sign-out, and this command had reported a
            // failure both times.
            //
            // It reports one because there is no good way to ask for this any more. bootstrap refuses the
            // login window's domain outright ("Domain does not support specified action"), and the legacy
            // form warns that a root caller should be pointing at LaunchDaemons and then exits non-zero
            // whatever it did. What it is still worth calling for is the -w, which clears a "disabled"
            // override if some earlier version of this left one behind.
            //
            // Treating its exit code as the answer is what made an install that had done everything
            // report that it had failed.
            Run("/bin/launchctl", ["load", "-w", "-S", "LoginWindow", UnattendedInstall.MacAgentPlist], log, quiet: true);

            // The half that asked for these rights reads this line; see Done.
            Console.Out.WriteLine(Done);
            Console.Out.Flush();
            return 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.LogError(e, "Could not set up {Dir}", UnattendedInstall.MacDataDirectory);
            return 2;
        }
    }

    /// <summary>
    /// Copies this machine's identity into the machine-wide store, and never over anything already there.
    ///
    /// "Never over" is the whole rule. A re-install, or an install on a Mac that already has a
    /// machine-wide store from an earlier one, must leave that store as it is: what is in it is what this
    /// machine has been to everything that has ever connected to it. Only keys with nothing under them are
    /// filled in.
    /// </summary>
    private static void HandOver(string userDataDir, ILogger log)
    {
        ISecretStore from = PlatformServices.SecretStoreFor(userDataDir);
        var to = new FileSecretStore(Path.Combine(UnattendedInstall.MacDataDirectory, "secrets"));

        foreach (string key in Identity)
        {
            try
            {
                if (to.GetAsync(key).AsTask().GetAwaiter().GetResult() is not null)
                {
                    log.LogInformation("{Key} is already in the machine-wide store; leaving it alone", key);
                    continue;
                }

                if (from.GetAsync(key).AsTask().GetAwaiter().GetResult() is { } value)
                {
                    to.SetAsync(key, value).AsTask().GetAwaiter().GetResult();
                    log.LogInformation("Moved {Key} into the machine-wide store", key);
                }
            }
            catch (SecretUnreadableException e)
            {
                // Refused rather than absent. Reported and skipped: writing a replacement is the one thing
                // that must not happen, and it is what would happen if this were treated as absence.
                log.LogWarning("{Key} could not be read ({Reason}); it has been left where it is", key, e.Message);
            }
        }

        // The settings the engine starts with. Same rule: a config already there belongs to the machine.
        string source = Path.Combine(userDataDir, "config.json");
        string target = Path.Combine(UnattendedInstall.MacDataDirectory, "config.json");
        try
        {
            if (File.Exists(source) && !File.Exists(target))
            {
                File.Copy(source, target);
                log.LogInformation("Copied the host settings into the machine-wide directory");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(e, "Could not copy the host settings; the machine-wide engine will start with defaults");
        }
    }

    /// <summary>
    /// Whether installing would produce a machine anyone could actually reach.
    ///
    /// Readable, not merely present: a hash that cannot be opened is not a password this machine has, and
    /// a permanent hash is computed against the salt beside it, so one that outlived its salt would never
    /// match anything either. Refusing here is kinder than a Mac that is reachable in principle and
    /// declines every password in practice.
    /// </summary>
    private static bool CanBeReachedUnattended(string userDataDir, ILogger log)
    {
        try
        {
            ISecretStore store = PlatformServices.SecretStoreFor(userDataDir);
            if (store.GetAsync("password-permanent-h1").AsTask().GetAwaiter().GetResult() is not null
                && store.GetAsync("password-salt").AsTask().GetAwaiter().GetResult() is not null)
            {
                return true;
            }
        }
        catch (SecretUnreadableException e)
        {
            log.LogError("This computer's {Key} belongs to another account; sort that out first.", e.Key);
            return false;
        }

        log.LogError(
            "This Mac has no permanent password, so the unattended jobs would have no way to let anybody in "
            + "while it is signed out: the temporary password is read off the screen, and click-to-accept "
            + "needs somebody signed in. Set one in Settings, then install.");
        return false;
    }

    /// <summary>
    /// Re-runs this program as root through the authorisation dialog everyone on a Mac recognises.
    ///
    /// Not <c>sudo</c>: there is no terminal to type into, and a GUI app shelling out to sudo either finds
    /// no tty or leans on a sudoers policy that belongs to the administrator rather than to us.
    /// </summary>
    /// <summary>
    /// Printed by the elevated stage when it finishes, and looked for by the half that asked.
    ///
    /// The security server starts that process, so it is not this one's child and its exit status is not
    /// ours to collect. A line it prints is the only channel back.
    /// </summary>
    private const string Done = "deskpair-system-stage: ok";

    private static int Elevate(string[] arguments, ILogger log)
    {
        // Ask with this application's name on the dialog. The fallback below works and shows "osascript",
        // which is a password prompt from a tool nobody recognises on behalf of a program that does remote
        // access -- the exact shape of something a careful person should refuse.
        if (MacAuthorization.IsAvailable && Environment.ProcessPath is { Length: > 0 } self)
        {
            switch (MacAuthorization.Run(self, arguments, PromptFor(arguments), out string output))
            {
                case AuthorizationOutcome.Declined:
                    log.LogError("Cancelled at the administrator prompt; nothing was changed.");
                    return 2;

                case AuthorizationOutcome.Ran:
                    if (output.Contains(Done, StringComparison.Ordinal))
                    {
                        return 0;
                    }

                    log.LogError("The elevated step did not finish; see the install log for what it got to.");
                    return 2;

                default:
                    // The deprecated call has finally gone, or the shim is not there. Say so, because the
                    // dialog that follows will name something else and somebody will wonder why.
                    log.LogWarning("Could not ask for administrator rights directly; falling back to osascript, whose prompt names itself rather than DeskPair");
                    break;
            }
        }

        return ElevateThroughOsascript(arguments, log);
    }

    /// <summary>
    /// The sentence on the authorisation dialog.
    ///
    /// Translated, because the rest of that dialog is: macOS writes its own labels in the system language,
    /// and an English sentence in the middle of a Chinese password box reads as something that does not
    /// belong there -- which is the opposite of what putting our name on it was for.
    /// </summary>
    private static string PromptFor(string[] arguments) =>
        Localization.Strings.Get(Array.IndexOf(arguments, "--uninstall-service") >= 0
            ? "mac.authorizeRemove"
            : "mac.authorizeInstall");

    private static int ElevateThroughOsascript(string[] arguments, ILogger log)
    {
        // Output redirected away, because "do shell script" treats what the command writes as its result
        // and anything on stderr as a fault. The elevated stage logs to the install log like everything
        // else, and that is where its account belongs; a copy of it arriving back as an AppleScript error
        // string turns a successful install into an unreadable failure message.
        string command = string.Join(' ', new[] { Environment.ProcessPath ?? "" }.Concat(arguments).Select(Quote))
            + " > /dev/null 2>&1";
        string script = $"do shell script {AppleScriptString(command)} with administrator privileges";
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo("/usr/bin/osascript")
            {
                ArgumentList = { "-e", script },
                UseShellExecute = false,
                RedirectStandardError = true,
            });

            if (process is null)
            {
                log.LogError("Could not ask for administrator privileges");
                return 2;
            }

            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode == 0)
            {
                return 0;
            }

            // -128 is what osascript reports when the person closed the dialog. That is an answer, not a
            // fault, and it should not look like one in the log.
            log.LogError(
                error.Contains("-128", StringComparison.Ordinal)
                    ? "Cancelled at the administrator prompt; nothing was changed."
                    : "The elevated step failed: {Error}",
                error.Trim());
            return 2;
        }
        catch (Exception e)
        {
            log.LogError(e, "Could not ask for administrator privileges");
            return 2;
        }
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static string AppleScriptString(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static void Write(string path, string contents, ILogger log)
    {
        File.WriteAllText(path, contents);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        chown(path, 0, 0);
        log.LogInformation("Wrote {Path}", path);
    }

    private static void Delete(string path, ILogger log)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                log.LogInformation("Removed {Path}", path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(e, "Could not remove {Path}", path);
        }
    }

    /// <param name="quiet">
    /// For the calls that are expected to fail: booting out something that was never loaded is how this
    /// makes itself repeatable, and it is not worth a line in the log.
    /// </param>
    private static bool Run(string file, string[] arguments, ILogger log, bool quiet = false)
    {
        try
        {
            var start = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardError = true };
            foreach (string argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            using Process? process = Process.Start(start);
            if (process is null)
            {
                return false;
            }

            // Read while it runs, not after. A pipe that fills up stops the child, and a child that is
            // stopped never exits, which is the deadlock this shape is usually written to avoid.
            Task<string> error = process.StandardError.ReadToEndAsync();

            // WaitForExit(int) answers whether it finished. Asking for the exit code of something that has
            // not is an InvalidOperationException, not a value -- and it was being asked unconditionally,
            // so a slow launchctl came back as "could not run /bin/launchctl" from the middle of an
            // install that had already written its plist. Watched happening on a real Mac.
            if (!process.WaitForExit(30_000))
            {
                if (!quiet)
                {
                    log.LogError("{File} {Arguments} has not finished after 30 seconds; giving up on it", file, string.Join(' ', arguments));
                }

                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                    // Already gone, or not ours to end. Either way there is nothing further to do.
                }

                return false;
            }

            if (process.ExitCode == 0)
            {
                return true;
            }

            if (!quiet)
            {
                log.LogError("{File} {Arguments} failed ({Code}): {Error}", file, string.Join(' ', arguments), process.ExitCode, error.Result.Trim());
            }

            return false;
        }
        catch (Exception e)
        {
            if (!quiet)
            {
                log.LogError(e, "Could not run {File}", file);
            }

            return false;
        }
    }

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int chown(string path, uint owner, uint group);
}
