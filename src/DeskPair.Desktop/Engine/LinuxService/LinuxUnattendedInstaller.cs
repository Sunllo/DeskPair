using System.Diagnostics;
using Microsoft.Extensions.Logging;
using DeskPair.Core.Config;
using DeskPair.Desktop.Services;
using DeskPair.Platform.Abstractions.Security;

namespace DeskPair.Desktop.Engine.LinuxService;

/// <summary>
/// Installs and removes the systemd unit that makes this desk reachable while it is locked or nobody is
/// signed in.
///
/// Two stages, like macOS, but for a different reason. There the elevated half must not read the login
/// keychain; here root can read everything, and the split is only that a desktop app has no way to hold
/// root for a few calls. So the user half checks what it can check, then re-runs this program through
/// <c>pkexec</c> with <c>--system-stage</c>, and that half does the whole install: the account, the copy of
/// the program, the machine-wide store, the unit.
///
/// The copy of the program is not optional, and was learned on a real machine. The engine runs as the
/// <c>deskpair</c> account, and a home directory on Ubuntu 24.04 is 0750: an engine started from
/// <c>/home/somebody/DeskPair</c> could not read its own executable once it had given up root, and the
/// single-file runtime's first lazily loaded assembly failed with FileNotFoundException -- every five
/// seconds, from a daemon that was itself fine.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
internal static class LinuxUnattendedInstaller
{
    public const string InstallDirectory = "/opt/deskpair";

    /// <summary>Where the unit points, whatever the app was run from.</summary>
    public const string Executable = InstallDirectory + "/DeskPair";

    /// <summary>The machine-wide data directory: the engine account's home, and the identity of this machine.</summary>
    public const string DataDirectory = "/var/lib/deskpair";

    /// <summary>Exit code: the authorisation prompt was declined or dismissed, and nothing was changed.</summary>
    public const int Declined = 3;

    /// <summary>Exit code: there was no way to ask for root here; the log has the command to run instead.</summary>
    public const int NoElevation = 4;

    /// <summary>The secrets that are this machine rather than this installation.</summary>
    internal static readonly string[] Identity =
    [
        "identity-key",
        "peer-id",
        "machine-id",
        "password-salt",
        "password-permanent-h1",
    ];

    private static readonly string[] NologinShells = ["/usr/sbin/nologin", "/sbin/nologin", "/bin/false"];

    public static bool IsInstalled() => UnattendedInstall.IsInstalled();

    /// <summary>
    /// The whole install, from the app or from <c>--install-service</c> run by hand. Returns a process exit
    /// code, because the caller that matters is the settings page, and it sees only the code.
    /// </summary>
    public static int Install(string[] args, string userDataDir, ILogger log)
    {
        if (Array.IndexOf(args, "--system-stage") >= 0)
        {
            if (NativeUser.Geteuid() != 0)
            {
                log.LogError("--system-stage is the root half of the install; pkexec or sudo starts it, not a user");
                return 2;
            }

            return SystemStage(Environment.ProcessPath ?? "", ServerRole.Arg(args, "--from"), log);
        }

        if (NativeUser.Geteuid() == 0)
        {
            log.LogError(
                "Run this as yourself, not with sudo: the install needs to know whose settings and password "
                + "to hand to the machine. To do the root half by hand, run: {Command}",
                SudoCommand(install: true, userDataDir));
            return 2;
        }

        // The password this is about to look for may still be filed under the product's previous name.
        LegacyIdentity.Migrate(userDataDir, log);

        if (Environment.ProcessPath is not { Length: > 0 })
        {
            log.LogError("Cannot work out where this program is, so there is nothing to install");
            return 2;
        }

        if (!CanBeReachedUnattended(userDataDir, log))
        {
            return 2;
        }

        int code = Elevate(["--install-service", "--system-stage", "--from", userDataDir], userDataDir, install: true, log);
        if (code == 0)
        {
            log.LogInformation("DeskPair will now be reachable while this computer is locked or signed out, and after a restart.");
        }

        return code;
    }

    public static int Uninstall(string[] args, ILogger log)
    {
        if (NativeUser.Geteuid() == 0)
        {
            return RemoveStage(log);
        }

        _ = args;
        return Elevate(["--uninstall-service", "--system-stage"], ServerRole.DefaultDataDir(), install: false, log);
    }

    /// <summary>The command to run by hand when there is no way to ask for root from here.</summary>
    public static string SudoCommand(bool install, string userDataDir)
    {
        string self = Environment.ProcessPath ?? "DeskPair";
        return install
            ? $"sudo {Quote(self)} --install-service --system-stage --from {Quote(userDataDir)}"
            : $"sudo {Quote(self)} --uninstall-service";
    }

    /// <summary>
    /// The engine's account: a system account with no shell and no password, whose home is the data
    /// directory. Created once and never changed; the arguments are a function so a test can pin them.
    /// </summary>
    public static string[] UseraddArguments(string shell) =>
    [
        "--system",
        "--user-group",
        "--home-dir", DataDirectory,
        "--no-create-home",
        "--shell", shell,
        "--comment", "Sunllo DeskPair engine",
        LinuxAccounts.EngineUser,
    ];

    /// <summary>
    /// What pkexec's exit code means for the install. 126 is the person saying no or closing the prompt;
    /// 127 is pkexec unable to run the program at all -- on a machine with no authentication agent, such
    /// as over SSH, it reports "not authorized" that way, and the answer then is the sudo command.
    /// </summary>
    public static int OutcomeOf(int pkexecExit) => pkexecExit switch
    {
        126 => Declined,
        127 => NoElevation,
        _ => pkexecExit,
    };

    /// <summary>
    /// The root half: the account, the program under /opt, the machine-wide store, the unit -- and then
    /// the daemon, started or restarted so the change takes effect without a reboot.
    /// </summary>
    private static int SystemStage(string executable, string? from, ILogger log)
    {
        if (executable.Length == 0)
        {
            log.LogError("Cannot work out where this program is, so there is nothing to install");
            return 2;
        }

        (uint Uid, uint Gid)? account = EnsureAccount(log);
        if (account is null)
        {
            return 2;
        }

        try
        {
            InstallExecutable(executable, log);

            // The engine account's home, and nobody else's business: a second account on this machine
            // must not read the key that is this machine's name or the hash of the password that opens
            // it. The engine reads its own secrets directly, which is what makes the fixed account worth
            // having -- there is no "hand the directory to whoever is signed in", and so no way for a
            // fast user switch to hand the machine's identity to a second person.
            Directory.CreateDirectory(DataDirectory);
            File.SetUnixFileMode(DataDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
            string secrets = Path.Combine(DataDirectory, "secrets");
            Directory.CreateDirectory(secrets);
            File.SetUnixFileMode(secrets, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            if (from is { Length: > 0 } && Directory.Exists(from))
            {
                HandOver(from, DataDirectory, log);
            }

            // Everything written above belongs to root at this moment, and the engine is not root. A
            // store the engine cannot read makes it exit 0 with an explanation, which looks exactly like
            // a successful install from the outside; so this is not optional and not best-effort.
            ChownTree(DataDirectory, account.Value.Uid, account.Value.Gid);

            if (!MachineCanBeReached(log))
            {
                return 2;
            }

            Write(UnattendedInstall.SystemdUnitPath, SystemdUnit.Daemon(Executable), log);
            WriteTerminalGate(log);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.LogError(e, "Could not set the machine up");
            return 2;
        }

        if (!Run("systemctl", ["daemon-reload"], log)
            || !Run("systemctl", ["enable", UnattendedInstall.SystemdUnit], log)
            || !Run("systemctl", ["restart", UnattendedInstall.SystemdUnit], log))
        {
            return 2;
        }

        log.LogInformation("{Unit} is enabled and running; `journalctl -u {Unit} -f` shows the daemon and the engine together", UnattendedInstall.SystemdUnit, UnattendedInstall.SystemdUnit);
        return 0;
    }

    private static int RemoveStage(ILogger log)
    {
        Run("systemctl", ["disable", "--now", UnattendedInstall.SystemdUnit], log, quiet: true);
        Delete(UnattendedInstall.SystemdUnitPath, log);
        Run("systemctl", ["daemon-reload"], log, quiet: true);
        Delete(Executable, log);
        try
        {
            if (Directory.Exists(InstallDirectory) && !Directory.EnumerateFileSystemEntries(InstallDirectory).Any())
            {
                Directory.Delete(InstallDirectory);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(e, "Could not remove {Dir}", InstallDirectory);
        }

        // The machine-wide store and the account are left alone on purpose. The store holds this
        // machine's identity, and removing the unattended install is not a reason to make the machine a
        // different one -- somebody who put it back would otherwise find every device had stopped
        // trusting it. The account exists so that store has an owner.
        log.LogInformation("The unattended service is gone. {Dir} and the '{User}' account were left where they are; they hold this machine's identity.", DataDirectory, LinuxAccounts.EngineUser);
        return 0;
    }

    private static (uint Uid, uint Gid)? EnsureAccount(ILogger log)
    {
        if (LinuxAccounts.Lookup(LinuxAccounts.EngineUser) is { } existing)
        {
            return existing;
        }

        string shell = NologinShells.FirstOrDefault(File.Exists) ?? NologinShells[^1];
        if (!Run("useradd", UseraddArguments(shell), log))
        {
            log.LogError("Could not create the '{User}' account the engine runs as", LinuxAccounts.EngineUser);
            return null;
        }

        (uint Uid, uint Gid)? created = LinuxAccounts.Lookup(LinuxAccounts.EngineUser);
        if (created is null)
        {
            log.LogError("useradd reported success but there is still no '{User}' account", LinuxAccounts.EngineUser);
            return null;
        }

        log.LogInformation("Created the '{User}' account (uid {Uid}) for the engine", LinuxAccounts.EngineUser, created.Value.Uid);
        return created;
    }

    /// <summary>
    /// Copies this program to where the unit points. Written beside and renamed over, so a daemon that is
    /// running from the old copy keeps its file and the new one appears whole; an upgrade is this same
    /// install run again from the new build.
    /// </summary>
    private static void InstallExecutable(string executable, ILogger log)
    {
        if (Path.GetFullPath(executable) == Executable)
        {
            return; // already running from the installed copy
        }

        Directory.CreateDirectory(InstallDirectory);
        File.SetUnixFileMode(InstallDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        string staging = Executable + ".new";
        File.Copy(executable, staging, overwrite: true);
        File.SetUnixFileMode(staging, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        File.Move(staging, Executable, overwrite: true);
        log.LogInformation("Installed {Executable}", Executable);
    }

    /// <summary>
    /// Copies this machine's identity from the installing user's store into the machine-wide one, and
    /// never over anything already there. A re-install must leave the machine the machine it has been to
    /// everything that ever connected to it; only keys with nothing under them are filled in.
    ///
    /// With one exception, learned from a machine whose daemon had run before it was installed: the salt
    /// and the permanent password are one thing, because the hash is computed against the salt beside it.
    /// A machine-wide store that has minted a salt of its own but has no permanent password gets the
    /// user's salt and hash together, or a hash that would never match anything. The salt is the one key
    /// this overwrites, and only in that case.
    /// </summary>
    internal static void HandOver(string userDataDir, string machineDataDir, ILogger log)
    {
        var from = new FileSecretStore(Path.Combine(userDataDir, "secrets"));
        var to = new FileSecretStore(Path.Combine(machineDataDir, "secrets"));

        foreach (string key in Identity)
        {
            if (key is "password-salt" or "password-permanent-h1")
            {
                continue; // moved as a pair, below
            }

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

        try
        {
            if (to.GetAsync("password-permanent-h1").AsTask().GetAwaiter().GetResult() is not null)
            {
                log.LogInformation("The machine-wide store already has a permanent password; leaving it and its salt alone");
            }
            else if (from.GetAsync("password-permanent-h1").AsTask().GetAwaiter().GetResult() is { } hash
                && from.GetAsync("password-salt").AsTask().GetAwaiter().GetResult() is { } salt)
            {
                to.SetAsync("password-salt", salt).AsTask().GetAwaiter().GetResult();
                to.SetAsync("password-permanent-h1", hash).AsTask().GetAwaiter().GetResult();
                log.LogInformation("Moved the permanent password and its salt into the machine-wide store");
            }
        }
        catch (SecretUnreadableException e)
        {
            log.LogWarning("The permanent password could not be read ({Reason}); it has been left where it is", e.Message);
        }

        // The settings the engine starts with. Same rule: a config already there belongs to the machine.
        string source = Path.Combine(userDataDir, "config.json");
        string target = Path.Combine(machineDataDir, "config.json");
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
    /// The user half's check, before anybody is asked for a password: is there a permanent password to
    /// hand over? Only asked of a machine that has no machine-wide store yet; a re-install trusts the
    /// store the machine already has, which root checks after the hand-over.
    /// </summary>
    private static bool CanBeReachedUnattended(string userDataDir, ILogger log)
    {
        if (Directory.Exists(Path.Combine(DataDirectory, "secrets")))
        {
            return true;
        }

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
            "This computer has no permanent password, so the service would have no way to let anybody in "
            + "while it is signed out: the temporary password is read off the screen, and click-to-accept "
            + "needs somebody signed in. Set one in Settings, then install.");
        return false;
    }

    /// <summary>
    /// Root's check, after the hand-over: readable, not merely present. A hash that outlived its salt would
    /// never match anything, and a machine reachable in principle that declines every password is worse
    /// than one that refused to install.
    /// </summary>
    private static bool MachineCanBeReached(ILogger log)
    {
        var store = new FileSecretStore(Path.Combine(DataDirectory, "secrets"));
        try
        {
            if (store.GetAsync("password-permanent-h1").AsTask().GetAwaiter().GetResult() is not null
                && store.GetAsync("password-salt").AsTask().GetAwaiter().GetResult() is not null)
            {
                return true;
            }
        }
        catch (SecretUnreadableException e)
        {
            log.LogError("The machine-wide store's {Key} could not be read: {Reason}", e.Key, e.Message);
            return false;
        }

        log.LogError("The machine-wide store has no permanent password; the service is not enabled. Set one in Settings and install again.");
        return false;
    }

    /// <summary>
    /// Re-runs this program as root through polkit, the prompt every desktop has. Not sudo: there is no
    /// terminal to type into, and a GUI app leaning on sudoers leans on a policy that belongs to the
    /// administrator rather than to us.
    /// </summary>
    private static int Elevate(string[] arguments, string userDataDir, bool install, ILogger log)
    {
        string? pkexec = new[] { "/usr/bin/pkexec", "/bin/pkexec", "/usr/local/bin/pkexec" }.FirstOrDefault(File.Exists);
        if (pkexec is null)
        {
            log.LogError("pkexec is not installed, so there is no way to ask for administrator rights from here. In a terminal, run: {Command}", SudoCommand(install, userDataDir));
            return NoElevation;
        }

        try
        {
            var start = new ProcessStartInfo(pkexec) { UseShellExecute = false, RedirectStandardError = true };
            start.ArgumentList.Add(Environment.ProcessPath ?? "");
            foreach (string argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            using Process? process = Process.Start(start);
            if (process is null)
            {
                log.LogError("Could not start pkexec");
                return NoElevation;
            }

            Task<string> error = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            int outcome = OutcomeOf(process.ExitCode);
            switch (outcome)
            {
                case Declined:
                    log.LogError("Cancelled at the administrator prompt; nothing was changed.");
                    break;
                case NoElevation:
                    log.LogError("pkexec could not ask for authorisation ({Error}). In a terminal, run: {Command}", error.Result.Trim(), SudoCommand(install, userDataDir));
                    break;
                case 0:
                    break;
                default:
                    log.LogError("The root half of the install exited {Code}; its log is under {Dir}/logs", process.ExitCode, DataDirectory);
                    break;
            }

            return outcome;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log.LogError(e, "Could not ask for administrator rights");
            return NoElevation;
        }
    }

    private static void ChownTree(string root, uint uid, uint gid)
    {
        _ = LinuxAccounts.Chown(root, uid, gid);
        foreach (string entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            _ = LinuxAccounts.Chown(entry, uid, gid);
        }
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    /// <summary>
    /// The daemon's terminal gate, written once and then left to the administrator: an upgrade must not
    /// switch back on what somebody deliberately switched off. Said out loud, because what "yes" means is
    /// not something to find out later.
    /// </summary>
    private static void WriteTerminalGate(ILogger log)
    {
        string path = Platform.Linux.Terminal.DaemonTerminalConfig.Path;
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            Write(path, Platform.Linux.Terminal.DaemonTerminalConfig.Default, log);
        }

        log.LogWarning(
            Platform.Linux.Terminal.DaemonTerminalConfig.Read()
                ? "Terminals on this machine may run as root (terminal-root = yes in {Path}): the deskpair account is equivalent to root here. Set it to no to limit terminals to the deskpair account."
                : "Terminals on this machine run as the deskpair account only (terminal-root is not yes in {Path}).",
            path);
    }

    private static void Write(string path, string contents, ILogger log)
    {
        File.WriteAllText(path, contents);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
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

    /// <param name="quiet">For the calls that are expected to fail, such as disabling a unit that was never enabled.</param>
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

            // Read while it runs: a pipe that fills up stops the child, and a stopped child never exits.
            Task<string> error = process.StandardError.ReadToEndAsync();
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
                    // Already gone, or not ours to end.
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
}
