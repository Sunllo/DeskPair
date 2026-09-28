using Avalonia;
using Microsoft.Extensions.Logging;
using DeskPair.Core.Logging;
using DeskPair.Desktop.Engine;
using DeskPair.Desktop.Services;

namespace DeskPair.Desktop;

public static class Program
{
    /// <summary>
    /// One executable for the whole product. Opening it is the normal case: it shows the window and hosts the
    /// host engine in this same process. The other roles are command-line tools that do one thing and exit.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        AppRole role = ParseRole(args);
        bool console = role == AppRole.Main || ConsoleAttach.AttachToParent();
        return role switch
        {
            AppRole.Server => RunServer(args),
            AppRole.Service => RunService(args),
            AppRole.SessionAgent => RunSessionAgent(args),
            AppRole.InstallService => RunServiceSetup(args, console, install: true),
            AppRole.UninstallService => RunServiceSetup(args, console, install: false),
            AppRole.AllowFirewall => RunFirewall(args, console, add: true),
            AppRole.RemoveFirewall => RunFirewall(args, console, add: false),
            AppRole.InstallVirtualDisplay => RunVirtualDisplaySetup(args, console, install: true),
            AppRole.RemoveVirtualDisplay => RunVirtualDisplaySetup(args, console, install: false),
            AppRole.RemoveSystemChanges => RunRemoveSystemChanges(args),
            AppRole.Update => RunUpdate(args),
            AppRole.Version => Print(App.Version),
            AppRole.Help => Usage(),
            _ => RunDesktop(args),
        };
    }

    /// <summary>The role is the first argument; everything else (<c>--connect</c>, <c>--minimised</c>, …) modifies the main UI.</summary>
    internal static AppRole ParseRole(string[] args) => args.Length == 0 ? AppRole.Main : args[0] switch
    {
        "--server" => AppRole.Server,
        "--service" => AppRole.Service,
        "--session-agent" => AppRole.SessionAgent,
        "--install-service" => AppRole.InstallService,
        "--uninstall-service" => AppRole.UninstallService,
        "--allow-firewall" => AppRole.AllowFirewall,
        "--remove-firewall" => AppRole.RemoveFirewall,
        "--install-virtual-display" => AppRole.InstallVirtualDisplay,
        "--remove-virtual-display" => AppRole.RemoveVirtualDisplay,
        "--remove-system-changes" => AppRole.RemoveSystemChanges,
        "--update" => AppRole.Update,
        "--version" => AppRole.Version,
        "--help" or "-h" or "-?" or "/?" => AppRole.Help,
        _ => AppRole.Main,
    };

    private static int RunDesktop(string[] args)
    {
        App.Role = AppRole.Main;
        App.Arguments = args;
        ILogger log = App.Logs.CreateLogger("app");
        CrashReporter.Install(log);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => log.LogInformation("Process exit (code {Code})", Environment.ExitCode);

        // One DeskPair per user: a second launch hands its arguments to the running one and exits.
        SingleInstance? single = SingleInstance.Claim(log);
        if (single is null && SingleInstance.Signal(args, log))
        {
            log.LogInformation("DeskPair is already running; handed over and exiting");
            return 0;
        }

        try
        {
            App.Instance = single;
            log.LogInformation("Starting {Version} on {Os}", App.Version, Environment.OSVersion);
#if WINDOWS
            RestartAfterUpdate.Register(log);
#endif
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception e)
        {
            CrashReporter.Report(log, e, fatal: true);
            return 1;
        }
        finally
        {
            single?.Dispose();
        }
    }

    /// <summary>
    /// The engine with no UI, for a server with no desktop session. It logs to the console and to
    /// <c>&lt;data&gt;/logs/server.log</c> rather than through <see cref="App.Logs"/>, which is fixed to the
    /// per-user desktop log and would ignore <c>--data</c>.
    /// </summary>
    private static int RunServer(string[] args)
    {
        App.Role = AppRole.Server;

#if !WINDOWS
        // Before anything else, including the log file: a supervisor that started this as root and asked
        // for it to become the session's user wants that to be true of every file this process creates,
        // not just the ones it happens to open after some later point.
        if (Engine.LinuxService.PrivilegeDrop.Apply(args) is { } refusal)
        {
            Console.Error.WriteLine($"DeskPair: cannot give up root: {refusal}");
            return 2;
        }

        // Started by the daemon: the screen and the input devices arrive as inherited descriptors, and the
        // platform is built around them instead of around an X display this account does not have.
        if (ServerRole.Arg(args, "--supervisor-fd") is { } supervisorFd &&
            ServerRole.Arg(args, "--keyboard-fd") is { } keyboardFd &&
            ServerRole.Arg(args, "--pointer-fd") is { } pointerFd &&
            ServerRole.Arg(args, "--relative-fd") is { } relativeFd)
        {
            Engine.PlatformServices.Daemon = new Engine.PlatformServices.DaemonHandles(
                int.Parse(supervisorFd), int.Parse(keyboardFd), int.Parse(pointerFd), int.Parse(relativeFd));
            if (!Platform.Linux.Hosting.ParentDeath.FollowParent())
            {
                Console.Error.WriteLine("The daemon that started this engine is already gone; not starting.");
                return 1;
            }
        }
#endif

        string dataDir = ServerRole.Arg(args, "--data") ?? ServerRole.DefaultDataDir();
        using ILoggerFactory logs = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(args.Contains("--verbose") ? LogLevel.Debug : LogLevel.Information);
            b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss.fff "; });
            b.AddProvider(new FileLoggerProvider(Path.Combine(dataDir, "logs", "server.log")));
        });

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            // No connection manager: there is no UI here, so click-to-accept has nobody to ask and fails closed.
            return ServerRole.RunAsync(args, logs, cts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (DeskPair.Platform.Abstractions.Security.SecretUnreadableException e)
        {
            // This machine's identity belongs to somebody else, and there is nothing to do about it here.
            //
            // It is what a second account's session looks like once the machine-wide store has been handed
            // to the person who installed: the store is theirs, this engine is not them, and the one thing
            // that must never happen is writing a fresh identity over the top -- which is what "there is
            // no key here" would have led to before this was told apart from absence.
            //
            // Exit 0, deliberately. A supervisor restarts what fails, and failing every five seconds for a
            // reason that will not change is how a real machine ended up looping on Windows.
            logs.CreateLogger("server").LogWarning(
                "Not starting: {Reason} This session belongs to a different account from the one DeskPair "
                + "was set up under, so it cannot read this machine's identity -- and writing a new one "
                + "would make this a different machine to everything that has ever connected to it.",
                e.Message);
            return 0;
        }
        catch (Exception e)
        {
            // Everything, not a chosen few. The GUI role installs CrashReporter; this one had nothing, so an
            // exception it did not expect ended as a core dump with the reason buried in a stack trace on
            // stderr. A logged line and an exit code are what a headless role owes whoever is reading the log.
            logs.CreateLogger("server").LogCritical(e, "The host engine stopped");
            return 2;
        }
    }

    /// <summary>
    /// The Linux daemon's session agent: started by the daemon as root, it becomes the signed-in user and holds the
    /// portal on that user's session bus for the daemon's engine, which runs as another account and has no such bus
    /// (see <c>SessionAgent</c>). It logs to stderr, which is the daemon's journal, and ends with the engine's socket or
    /// the daemon.
    /// </summary>
    private static int RunSessionAgent(string[] args)
    {
#if WINDOWS
        _ = args;
        Console.Error.WriteLine("The session agent is part of the Linux daemon.");
        return 2;
#else
        App.Role = AppRole.SessionAgent;
        if (!OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine("The session agent is part of the Linux daemon.");
            return 2;
        }

        // Before anything else, as for the engine. The unpacked libraries stay the engine's: the agent loads none.
        if (Engine.LinuxService.PrivilegeDrop.Apply(args, handOverLibraries: false) is { } refusal)
        {
            Console.Error.WriteLine($"DeskPair: cannot give up root: {refusal}");
            return 2;
        }

        // The portal reads /proc/<pid>/root of whoever calls it, as the user; the drop left those files root's.
        Engine.LinuxService.PrivilegeDrop.BecomeOrdinaryProcess();

        if (ServerRole.Arg(args, "--agent-fd") is not { } fdText || !int.TryParse(fdText, out int socket) || socket < 0)
        {
            Console.Error.WriteLine("--session-agent needs --agent-fd N, the socket the daemon started it with.");
            return 2;
        }

        // Inherited across exec; not to be inherited again by anything this starts.
        _ = Platform.Linux.Native.UnixSocketMsg.fcntl(socket, Platform.Linux.Native.UnixSocketMsg.FSetFd, 1);
        if (!Platform.Linux.Hosting.ParentDeath.FollowParent())
        {
            Console.Error.WriteLine("The daemon that started this agent is already gone; not starting.");
            return 1;
        }

        using ILoggerFactory logs = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(args.Contains("--verbose") ? LogLevel.Debug : LogLevel.Information);
            b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss.fff "; });
        });

        using var cts = new CancellationTokenSource();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();
        Platform.Linux.Wayland.Agent.SessionAgent.RunAsync(socket, logs, App.Version, cts.Token).GetAwaiter().GetResult();
        return 0;
#endif
    }

    /// <summary>
    /// The service control manager started us. Nothing here writes to a console -- there is none in session
    /// 0 -- so the log file is the only account of what happened, and it goes beside the engine's own under
    /// the machine-wide data directory rather than any user's.
    /// </summary>
    private static int RunService(string[] args)
    {
#if !WINDOWS
        if (!OperatingSystem.IsLinux())
        {
            _ = args;
            Console.Error.WriteLine("The host service is a Windows and Linux feature; macOS uses a launchd agent.");
            return 2;
        }

        App.Role = AppRole.Service;
        string dataDir = ServerRole.Arg(args, "--data") ?? ServerRole.DefaultDataDir();

        // stderr only: under systemd that is the journal, and one `journalctl -u sunllo-deskpair` then
        // shows the daemon and the engine it started interleaved on one clock.
        using ILoggerFactory logs = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(args.Contains("--verbose") ? LogLevel.Debug : LogLevel.Information);
            b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss.fff "; });
        });

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();
        return Engine.LinuxService.LinuxHostDaemon.RunAsync(dataDir, logs, cts.Token).GetAwaiter().GetResult();
#else
        App.Role = AppRole.Service;
        string dataDir = ServerRole.Arg(args, "--data") ?? ServerRole.DefaultDataDir();
        using ILoggerFactory logs = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(args.Contains("--verbose") ? LogLevel.Debug : LogLevel.Information);
            b.AddProvider(new FileLoggerProvider(Path.Combine(dataDir, "logs", "service.log")));
        });

        return Engine.Service.HostService.Run(logs.CreateLogger("service"));
#endif
    }

    /// <summary>
    /// Installing or removing the service, relaunched elevated from the settings page or run by hand. Both
    /// answer with an exit code, because the caller that matters is usually not a person reading a console.
    /// </summary>
    private static int RunServiceSetup(string[] args, bool console, bool install)
    {
#if !WINDOWS
        if (OperatingSystem.IsLinux())
        {
            // Under pkexec this is root and the default is /var/lib/deskpair, so the install log lands
            // beside the engine's; as the user it is their own directory, and --from carries that path
            // into the root half so the hand-over reads the right store.
            string linuxDataDir = ServerRole.Arg(args, "--data") ?? ServerRole.DefaultDataDir();
            using ILoggerFactory linuxLogs = LoggerFactory.Create(b => b
                .AddSimpleConsole(o => o.SingleLine = true)
                .AddProvider(new FileLoggerProvider(Path.Combine(linuxDataDir, "logs", "install.log")))
                .SetMinimumLevel(args.Contains("--verbose") ? LogLevel.Debug : LogLevel.Information));
            ILogger linuxLog = linuxLogs.CreateLogger("install");
            _ = console; // the settings page reads the exit code; a root half has no display to put a box on
            return install
                ? Engine.LinuxService.LinuxUnattendedInstaller.Install(args, linuxDataDir, linuxLog)
                : Engine.LinuxService.LinuxUnattendedInstaller.Uninstall(args, linuxLog);
        }

        if (!OperatingSystem.IsMacOS())
        {
            _ = (console, install);
            Console.Error.WriteLine("Unattended access is not available on this system.");
            return 2;
        }

        // macOS keeps the machine-wide store where root can create it and the installing user can read
        // it; the *user's* data directory is what the secrets are moved out of, so that is what is passed.
        string macDataDir = ServerRole.Arg(args, "--data") ?? ServerRole.DefaultDataDir();
        using ILoggerFactory macLogs = LoggerFactory.Create(b => b
            .AddSimpleConsole(o => o.SingleLine = true)
            .AddProvider(new FileLoggerProvider(Path.Combine(macDataDir, "logs", "install.log")))
            .SetMinimumLevel(args.Contains("--verbose") ? LogLevel.Debug : LogLevel.Information));
        ILogger macLog = macLogs.CreateLogger("install");
        int macCode = install
            ? Engine.MacService.MacUnattendedInstaller.Install(args, macDataDir, macLog)
            : Engine.MacService.MacUnattendedInstaller.Uninstall(macLog);
        if (macCode != 0 && !console)
        {
            CrashReporter.ShowMessage(install
                ? "Could not set DeskPair up to run while this Mac is signed out."
                : "Could not remove DeskPair's unattended access.");
        }

        return macCode;
#else
        // The file matters more than the console here. These are almost always run by relaunching this
        // program elevated from the settings page, where there is no console at all and the window closes
        // the instant it finishes -- so a failure explained only on stdout is a failure explained to
        // nobody. It goes in the same log the service writes, because that is where somebody will look.
        string dataDir = ServerRole.Arg(args, "--data") ?? ServerRole.DefaultDataDir();
        using ILoggerFactory logs = LoggerFactory.Create(b => b
            .AddSimpleConsole(o => o.SingleLine = true)
            .AddProvider(new FileLoggerProvider(Path.Combine(dataDir, "logs", "service.log")))
            .SetMinimumLevel(args.Contains("--verbose") ? LogLevel.Debug : LogLevel.Information));
        ILogger log = logs.CreateLogger("service");
        int code = install
            ? Engine.Service.HostServiceInstaller.Install(dataDir, log)
            : Engine.Service.HostServiceInstaller.Uninstall(log);
        if (code != 0 && !console)
        {
            CrashReporter.ShowMessage(install
                ? "Could not install the Sunllo DeskPair service."
                : "Could not remove the Sunllo DeskPair service.");
        }

        return code;
#endif
    }

    private static int RunFirewall(string[] args, bool console, bool add)
    {
        using ILoggerFactory logs = LoggerFactory.Create(b => b
            .AddSimpleConsole(o => o.SingleLine = true)
            .SetMinimumLevel(args.Contains("--verbose") ? LogLevel.Debug : LogLevel.Information));
        int code = add ? FirewallRules.Add(logs) : FirewallRules.Remove(logs);
        if (code != 0 && !console)
        {
            // Relaunched elevated from the settings page: there is no console for the error to land in.
            CrashReporter.ShowMessage($"Could not change the Windows Firewall rules for \"{FirewallRules.RuleName}\".");
        }

        return code;
    }

    /// <summary>
    /// Installs or removes the driver that lets a viewer add a display this computer does not have. Windows only,
    /// administrator only, and logged where the service logs: like the service setup, this is usually run by
    /// relaunching the program elevated, with no console for a failure to be explained on.
    /// </summary>
    private static int RunVirtualDisplaySetup(string[] args, bool console, bool install)
    {
#if WINDOWS
        string dataDir = ServerRole.Arg(args, "--data") ?? ServerRole.DefaultDataDir();
        using ILoggerFactory logs = LoggerFactory.Create(b => b
            .AddSimpleConsole(o => o.SingleLine = true)
            .AddProvider(new FileLoggerProvider(Path.Combine(dataDir, "logs", "service.log")))
            .SetMinimumLevel(args.Contains("--verbose") ? LogLevel.Debug : LogLevel.Information));
        ILogger log = logs.CreateLogger("vdd");
        int code = install
            ? Platform.Windows.Capture.VirtualDisplayDriverInstaller.Install(dataDir, Platform.Windows.Capture.VirtualDisplayDriver.PackageDirectory, log)
            : Platform.Windows.Capture.VirtualDisplayDriverInstaller.Uninstall(dataDir, log);
        if (code != 0 && !console)
        {
            CrashReporter.ShowMessage(install
                ? "Could not install the virtual display driver."
                : "Could not remove the virtual display driver.");
        }

        return code;
#else
        Console.Error.WriteLine("Adding displays that do not exist needs a Windows driver; there is nothing to install here.");
        return 2;
#endif
    }

    /// <summary>
    /// What the Windows installer runs, as LocalSystem, just before it deletes the program: removes the service, the
    /// firewall rules, the login entries and the virtual display driver -- everything outside the program's own folder
    /// that nothing else would take away. The firewall rules include the ones Windows made itself when somebody allowed
    /// the program through its prompt, and the login entries are those that start this copy, for everybody signed in.
    /// What people set up -- this computer's id and passwords, settings, the device list, the logs -- stays, so that
    /// installing DeskPair again gives back the same computer. Never a window: under the installer nobody can answer
    /// one, and a message box in session 0 would hold the uninstall up for good. And always exit 0, so a computer being
    /// cleaned up is not left half-uninstalled because one step was already done or would not go; the service log says
    /// which.
    /// </summary>
    private static int RunRemoveSystemChanges(string[] args)
    {
#if WINDOWS
        string dataDir = ServerRole.Arg(args, "--data") ?? ServerRole.DefaultDataDir();
        using ILoggerFactory logs = LoggerFactory.Create(b => b
            .AddSimpleConsole(o => o.SingleLine = true)
            .AddProvider(new FileLoggerProvider(Path.Combine(dataDir, "logs", "service.log")))
            .SetMinimumLevel(args.Contains("--verbose") ? LogLevel.Debug : LogLevel.Information));
        ILogger log = logs.CreateLogger("uninstall");
        log.LogInformation("DeskPair is being removed; taking away what it changed outside its own folder");
        string exe = Environment.ProcessPath ?? string.Empty;
        Remove("the service", () => Engine.Service.HostServiceInstaller.Uninstall(log));
        Remove("the firewall rules", () => FirewallRules.Remove(logs, quiet: true));
        Remove("the firewall rules Windows made for this program", () => FirewallRules.RemoveAllFor(exe, log));
        Remove("the login entries", () =>
        {
            log.LogInformation("Login entries that started {Program} removed: {Count}", exe, StartupEntry.RemoveForSignedInUsers(exe, log));
            return 0;
        });
        Remove("the virtual display driver", () => Platform.Windows.Capture.VirtualDisplayDriverInstaller.Uninstall(dataDir, log));
        return 0;

        void Remove(string what, Func<int> remove)
        {
            try
            {
                int code = remove();
                if (code != 0)
                {
                    log.LogWarning("Could not remove {What} (exit {Code}); going on with the rest", what, code);
                }
            }
            catch (Exception e)
            {
                log.LogWarning(e, "Could not remove {What}; going on with the rest", what);
            }
        }
#else
        _ = args;
        Console.Error.WriteLine("This is what the Windows installer runs before it removes DeskPair; there is nothing for it to do here.");
        return 2;
#endif
    }

    private static int Print(string text)
    {
        Console.WriteLine(text);
        return 0;
    }

    /// <summary>
    /// The update, from a terminal or a cron job rather than the notice. The same road the button takes --
    /// the same check, the same signature, the same hash -- so a machine nobody sits at stays current
    /// without a screen to click on.
    /// </summary>
    private static int RunUpdate(string[] args)
    {
        App.Config = DesktopConfig.Load();
        using ILoggerFactory logs = LoggerFactory.Create(b => b
            .AddSimpleConsole(o => o.SingleLine = true)
            .SetMinimumLevel(args.Contains("--verbose") ? LogLevel.Debug : LogLevel.Information));
        using var client = new Core.Update.UpdateClient();
        using var check = new UpdateCheckService(client, App.Version, () => true, () => App.Config.PortalServer,
            logs.CreateLogger<UpdateCheckService>(), publicKey: () => App.Config.UpdatePublicKeyBase64);
        check.CheckNowAsync().GetAwaiter().GetResult();
        UpdateState state = check.Current;
        if (state.Problem.Length > 0)
        {
            Console.Error.WriteLine($"Could not check: {state.Problem}");
            return 2;
        }

        if (!state.IsUpdateAvailable)
        {
            Console.WriteLine($"Up to date: {App.Version} (the portal offers {state.LatestVersion}).");
            return 0;
        }

        if (state.Install is not { } file)
        {
            Console.Error.WriteLine(Services.Update.PackagedInstall.Format is { } format
                ? $"DeskPair {state.LatestVersion} is available. The system's package manager installed this copy ({format}), so it updates it too: get the new package from {state.DownloadUrl}"
                : $"DeskPair {state.LatestVersion} is available but cannot be installed from here (no signed manifest, or no file for this machine). Download it from {state.DownloadUrl}");
            return 3;
        }

        Console.WriteLine($"Installing DeskPair {state.LatestVersion} from {file.Name} ({file.Size} bytes)...");
        var installer = new Services.Update.UpdateInstaller(new HttpClient { Timeout = TimeSpan.FromMinutes(30) }, logs.CreateLogger("update"));
        long shown = -1;
        var progress = new Progress<Services.Update.UpdateProgress>(p =>
        {
            long pct = p.Total > 0 ? 100 * p.Done / p.Total : 0;
            if (p.Stage != Services.Update.UpdateStage.Downloading || pct / 10 != shown / 10)
            {
                shown = pct;
                Console.WriteLine(p.Stage == Services.Update.UpdateStage.Downloading ? $"  downloading {pct}%" : $"  {p.Stage.ToString().ToLowerInvariant()}");
            }
        });
        try
        {
            installer.InstallAsync(App.Config.PortalServer, file, progress, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is Services.Update.UpdateException or HttpRequestException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Not installed: {e.Message}");
            return 2;
        }

        Console.WriteLine("Verified and staged. This process is exiting; the finishing script replaces the program and starts it.");
        return 0;
    }

    private static int Usage()
    {
        Console.Error.WriteLine("""
            Sunllo DeskPair

              DeskPair                      the app: the window, and the host engine in the same process
              DeskPair --server             the host engine only, no window -- it still needs a desktop to
                                           capture, and refuses to start without one
                                                   [--data DIR] [--config FILE] [--ipc-token HEX] [--verbose]
                                                   [--token-path FILE] [--synthetic] [--x11]
                                                   [--drop-uid N --drop-gid N --drop-user NAME] (Linux)
              DeskPair --install-service   set this desk up to be reached while it is locked or signed out
                                           (Windows: a service, run as administrator. macOS: a launchd
                                           agent -- run it as yourself, it asks for the one step that
                                           needs an administrator. Linux: a root daemon, sunllo-deskpair,
                                           whose engine runs as the 'deskpair' account -- run it as
                                           yourself and polkit asks; with no desktop to ask on, it prints
                                           the sudo command to run instead)
              DeskPair --uninstall-service remove that again
              DeskPair --service            the Linux daemon itself, started by systemd; root only
              DeskPair --session-agent      the daemon's helper in a signed-in Wayland desktop; started by
                                           the daemon, never by hand
              DeskPair --allow-firewall     allow this program through the Windows Firewall (administrator)
              DeskPair --remove-firewall    remove those rules (administrator)
              DeskPair --install-virtual-display   let viewers add displays this computer does not have
                                           (Windows, administrator; used by the service's engine)
              DeskPair --remove-virtual-display    remove that driver again
              DeskPair --remove-system-changes     remove the service, the firewall rules and that driver in one go
                                           (Windows, administrator; the installer runs it before removing DeskPair)
              DeskPair --update             check the portal and, if a newer release is signed by the key this
                                           install trusts, download it, verify it and replace this program
                                           (exit 0: installed or already current; 3: newer but not installable)
              DeskPair --version

            Opening the app is what makes this desk controllable; closing it stops that. To be reachable after a
            restart, turn on "start at sign-in" in Settings, or install the unattended access above.

            On macOS, --server takes its screen-recording permission from whatever started it, so running
            it from a terminal is for testing only: launched from a shell it is the shell that was granted
            or refused, not DeskPair. The launchd agent --install-service writes is started by launchd,
            which is what makes the grant apply.

            --synthetic streams a made-up desktop instead of this computer's screen, for the harness and for
            tests. Without it a host that cannot see a screen stops and says why, rather than serving a
            picture of nothing.

            On Linux a Wayland session is shared through the desktop's screen-sharing portal: the first viewer
            to connect makes it ask the person at the computer (once, if they let it remember), and the
            "being shared" indicator stays on while somebody is connected. --x11 (or SUNLLO_X11=1) captures
            through X11 instead, which under Wayland sees only the programs running on Xwayland.
            """);
        return 1;
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .With(new Avalonia.Media.FontManagerOptions
        {
            // Inter carries Latin and Cyrillic. Chinese, Japanese and Korean come from the system: the
            // families Windows, macOS and a Linux desktop with fonts-noto-cjk ship, tried in this order.
            // Without any of them a CJK language renders as boxes, which docs/unattended-linux.md warns about.
            FontFallbacks =
            [
                new Avalonia.Media.FontFallback { FontFamily = new Avalonia.Media.FontFamily("Microsoft JhengHei UI") },
                new Avalonia.Media.FontFallback { FontFamily = new Avalonia.Media.FontFamily("Microsoft YaHei UI") },
                new Avalonia.Media.FontFallback { FontFamily = new Avalonia.Media.FontFamily("Yu Gothic UI") },
                new Avalonia.Media.FontFallback { FontFamily = new Avalonia.Media.FontFamily("Malgun Gothic") },
                new Avalonia.Media.FontFallback { FontFamily = new Avalonia.Media.FontFamily("PingFang TC") },
                new Avalonia.Media.FontFallback { FontFamily = new Avalonia.Media.FontFamily("Hiragino Sans") },
                new Avalonia.Media.FontFallback { FontFamily = new Avalonia.Media.FontFamily("Apple SD Gothic Neo") },
                new Avalonia.Media.FontFallback { FontFamily = new Avalonia.Media.FontFamily("Noto Sans CJK TC") },
                new Avalonia.Media.FontFallback { FontFamily = new Avalonia.Media.FontFamily("Noto Sans CJK SC") },
                new Avalonia.Media.FontFallback { FontFamily = new Avalonia.Media.FontFamily("Noto Sans CJK JP") },
                new Avalonia.Media.FontFallback { FontFamily = new Avalonia.Media.FontFamily("Noto Sans CJK KR") },
            ],
        })
        .LogToTrace();
}

public enum AppRole
{
    /// <summary>The app: main window, tray, connection manager and the host engine, all in this process.</summary>
    Main,

    /// <summary>The host engine with no UI.</summary>
    Server,

    /// <summary>The Windows service: keeps an engine on the desktop the user is looking at. Windows only.</summary>
    Service,

    /// <summary>The Linux daemon's helper in a signed-in Wayland desktop: the portal, for the daemon's engine. Linux only.</summary>
    SessionAgent,
    InstallService,
    UninstallService,
    AllowFirewall,
    RemoveFirewall,

    /// <summary>Install or remove the driver virtual displays are made with. Windows only.</summary>
    InstallVirtualDisplay,
    RemoveVirtualDisplay,

    /// <summary>Undo everything outside the program's folder, for the Windows installer to run before it removes the program.</summary>
    RemoveSystemChanges,

    /// <summary>Check the portal and install a newer signed release, with no window: for a machine nobody sits at.</summary>
    Update,
    Version,
    Help,
}
