using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Microsoft.Extensions.Logging;
using System.Reflection;
using DeskPair.Core.Ipc;
using DeskPair.Desktop.Localization;
using DeskPair.Desktop.Services;
using DeskPair.Desktop.ViewModels;
using DeskPair.Desktop.Views;

namespace DeskPair.Desktop;

public partial class App : Application
{
    private static readonly List<Window> SessionWindows = [];
    private static HostLink? _host;
    private static EngineHost? _engine;
    private static int _exited;

    /// <summary>
    /// The connection manager's own link to the engine. It is a second IPC client in this one process,
    /// because the engine honours an approval only from the connection-manager role: the main window can
    /// watch a connection arrive but cannot decide for the user. That was never a security boundary -- the
    /// IPC token sits in a file any process of this user can read -- but it does keep the decision in the one
    /// place the user is looking at.
    /// </summary>
    private static HostLink? _cmLink;
    private static ConnectionManagerViewModel? _cmVm;
    private static ConnectionManagerWindow? _cmWindow;

    /// <summary>Keeps the saved-computer list in step with the account's, when this install is linked to one.</summary>
    public static AddressBookSyncService? BookSync { get; private set; }

    /// <summary>
    /// A sync brought in what another computer of the account changed. Raised on a timer's thread. Static, because the
    /// device page is built before the sync exists -- and nothing listened to the sync's own event, so a list on screen
    /// kept showing the old entries while the file under it already had the new ones.
    /// </summary>
    public static event Action? AddressBookPulled;

    private static readonly SemaphoreSlim BookSyncGate = new(1, 1);
    private static bool _saidNoIdentity;

    /// <summary>Sends the connection record to the linked account; null until this machine has an identity.</summary>
    public static ConnectionHistoryUploadService? HistoryUpload { get; private set; }

    /// <summary>
    /// Asks the portal what the newest version is. Null until the window exists, and null forever in
    /// <c>--server</c>, which has nobody to tell.
    /// </summary>
    public static UpdateCheckService? Updates { get; private set; }

    /// <summary>
    /// Starts the address book's sync unless it runs already. Asked again whenever a sync is wanted: at start-up this
    /// computer may have no identity yet -- the engine makes one the first time it starts -- and the sync then stayed
    /// off until the app was reopened, however often the person signed in or opened the list meanwhile.
    /// </summary>
    private static async Task<AddressBookSyncService?> EnsureAddressBookSyncAsync()
    {
        await BookSyncGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (BookSync is not null)
            {
                return BookSync;
            }

            // Null when this machine has no identity yet, which is not a fault: the engine makes one the
            // first time it starts, and an address book belongs to a machine that exists.
            if (await AddressBookSyncService.CreateAsync(Logs).ConfigureAwait(false) is not { } sync)
            {
                if (!_saidNoIdentity)
                {
                    _saidNoIdentity = true;
                    Logs.CreateLogger("account").LogInformation("No address book sync yet: this computer has no identity until the host engine has started once");
                }

                return null;
            }

            sync.Pulled += () => AddressBookPulled?.Invoke();
            BookSync = sync;
            sync.Start();

            // The same identity and the same "only when linked" rule, so it goes up beside the book.
            HistoryUpload = ConnectionHistoryUploadService.Create(Host, Logs, () => Config.UploadConnectionHistory);
            HistoryUpload?.Start();
            return sync;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // A keystore this app cannot read is a reason not to sync, not a reason not to start.
            Logs.CreateLogger("account").LogWarning(e, "Address book sync is not available");
            return null;
        }
        finally
        {
            BookSyncGate.Release();
        }
    }

    /// <summary>
    /// Syncs the address book in a moment, starting the sync first if it could not start before: when somebody has just
    /// signed in, and while the device list is on screen. The fifteen-minute round is for a list nobody is looking at;
    /// one that is looked at shows what another computer of the account added, not what was there a quarter-hour ago.
    /// </summary>
    public static void SyncAddressBookSoon() => _ = SyncAddressBookSoonAsync();

    private static async Task SyncAddressBookSoonAsync() => (await EnsureAddressBookSyncAsync().ConfigureAwait(false))?.Nudge();

    public static AppRole Role { get; set; } = AppRole.Main;

    public static string[] Arguments { get; set; } = [];

    private static readonly Lazy<ILoggerFactory> LogsLazy = new(() => LoggerFactory.Create(b => b
        .AddSimpleConsole(o => o.SingleLine = true)
        .AddProvider(new Core.Logging.FileLoggerProvider(CrashReporter.LogPath))
        .SetMinimumLevel(LogLevel.Information)));

    /// <summary>Created on first use, after <see cref="Role"/> is set, so the log file matches the role.</summary>
    public static ILoggerFactory Logs => LogsLazy.Value;

    public static DesktopConfig Config { get; set; } = new();

    /// <summary>Set when the user really means to quit, so the main window stops hiding to the tray.</summary>
    public static bool IsShuttingDown { get; private set; }

    /// <summary>Ends the process on purpose, the way the tray's Quit does: an update's finishing script waits for exactly this.</summary>
    public static void Quit()
    {
        IsShuttingDown = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
            }
        });
    }

    /// <summary>
    /// This process's claim on being the one DeskPair for this user, when it holds one. Set before the
    /// application starts so a second launch can be answered as soon as there is a window to raise.
    /// </summary>
    internal static SingleInstance? Instance { get; set; }

    /// <summary>
    /// What this build calls itself, everywhere: the About page, <c>--version</c>, the handshake, and the
    /// <c>version</c> column the rendezvous server keeps per peer.
    ///
    /// It reads InformationalVersion rather than AssemblyVersion, which is the difference between "0.2.0"
    /// and "0.2.0+8ec4feb". AssemblyVersion has only four numbers and cannot carry a commit at all, so while
    /// this read it, two builds of different commits were indistinguishable at runtime -- and nothing had
    /// bumped the number since 0.1.0 either, so in practice *every* build answered the same. An update
    /// channel cannot be tested against that: "you are up to date" and "the check is broken" look alike.
    ///
    /// Nothing parses this. It is a label, and the trailing "+sha" is absent in a build made outside a git
    /// checkout (see Directory.Build.props).
    /// </summary>
    public static string Version { get; } =
        typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(App).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";

    public static string PlatformName => Core.Session.Host.PlatformName.Current;

    public static HostLink Host => _host ?? throw new InvalidOperationException("Not initialised.");

    /// <summary>Pinned keys for hosts addressed by ip/host name (trust on first use).</summary>
    public static Core.Transport.IKnownHostsStore KnownHosts { get; } = new Core.Transport.FileKnownHostsStore(Path.Combine(Path.GetDirectoryName(DesktopConfig.DefaultPath)!, "known_hosts.json"));

    /// <summary>
    /// Where the signalling servers are, for connecting out. See <see cref="NetworkDirectoryCache"/> for
    /// why the controller needs its own answer to a question the host has always asked.
    /// </summary>
    public static NetworkDirectoryCache Directory { get; } = new();

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        Config = DesktopConfig.Load();
        Strings.Language = Config.Language;
        string? token = Arguments.SkipWhile(a => a != "--ipc-token").Skip(1).FirstOrDefault();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Opening the app is enough to be controllable: the engine runs in this process, unless one is
            // already listening or the caller asked us not to start one. The launcher does not have to start
            // anything any more -- the connection manager is already attached below, and the engine waits for
            // it either way -- but it must not be null, which is how the headless role says there is no UI.
            _engine = new EngineHost(Logs, _ => Task.CompletedTask);
            Func<string?> tokens = () => token;
            if (token is null && !Arguments.Contains("--no-engine"))
            {
                // Asked on every connection attempt: the engine can move between this app and the service while
                // the app is open, and the links follow it by reconnecting with whichever token is current.
                _engine.EnsureEngineAsync().GetAwaiter().GetResult();
                EngineHost engine = _engine;
                tokens = () => engine.Token;
                engine.StartWatching();
            }

            _host = new HostLink(IpcRoles.Ui, Logs.CreateLogger("ipc"), tokens);
            _host.Start();

            // Attached from the start, so a connection that arrives in the first second still has somebody to
            // approve it. Only the card itself waits: it is built when there is something to show.
            _cmLink = new HostLink(IpcRoles.ConnectionManager, Logs.CreateLogger("ipc.cm"), tokens);
            _cmVm = new ConnectionManagerViewModel(_cmLink);
            _cmVm.ApprovalRequested += _ => ShowConnectionManager(topmost: true);
            _cmVm.ConnectionAuthorized += _ => ShowConnectionManager(topmost: false);
            _cmLink.Start();

            // The saved-computer list follows the account, when there is one. Started without being waited
            // for: an unlinked install does nothing here, and a linked one must not hold up the window while
            // it finds out whether a portal is reachable.
            _ = EnsureAddressBookSyncAsync();

            // Nothing here can fail -- no keystore, no identity, no token -- so unlike the book sync it
            // needs no guard. Started only on this branch, so --server never asks: it has no screen to
            // put the answer on.
            Updates = UpdateCheckService.Create(Logs);
            Updates.Start();

            // Started by the login entry: stay in the notification area with the engine running, and build
            // the window only when the user asks for it.
            bool minimised = Arguments.Contains("--minimised") || Config.StartMinimised;
            if (Config.CloseToTray || minimised)
            {
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                SetupTray(desktop);
            }

            if (!minimised)
            {
                desktop.MainWindow = new MainWindow(new MainWindowViewModel(_host));
            }

            if (Arguments.SkipWhile(a => a != "--connect").Skip(1).FirstOrDefault() is { } connect)
            {
                OpenRemoteSession(connect, Arguments.SkipWhile(a => a != "--password").Skip(1).FirstOrDefault());
            }

            // Somebody started DeskPair again: show what they were looking for rather than nothing at all.
            if (Instance is not null)
            {
                Instance.Launched += started => Avalonia.Threading.Dispatcher.UIThread.Post(() => OnSecondLaunch(desktop, started));
            }

            ILogger lifecycle = Logs.CreateLogger("lifecycle");

            // The system asking the app to end -- Windows signing out or shutting down, or an installer's Restart
            // Manager closing it to replace or remove its files -- is a real quit, as the tray's is. Left to the
            // windows, the main one hid itself in the tray instead of closing, Avalonia took that for a refusal, and
            // an MSI upgrade left the old version running from files Windows Installer had had to move aside.
            desktop.ShutdownRequested += (_, e) =>
            {
                lifecycle.LogInformation("Shutdown requested (cancel={Cancel})", e.Cancel);
                if (!e.Cancel)
                {
                    Quit();
                }
            };
            desktop.Exit += async (_, e) =>
            {
                // Once. When the system ends the app, Exit comes twice -- for the end of the session and for the Quit
                // above -- and a second pass disposed the engine again, which threw on the way out and left the
                // process sitting on a crash dialog.
                if (Interlocked.Exchange(ref _exited, 1) == 1)
                {
                    return;
                }

                lifecycle.LogInformation("Application exit (code {Code}); open windows: {Windows}", e.ApplicationExitCode, string.Join(", ", desktop.Windows.Select(w => w.GetType().Name)));
                if (_cmLink is not null)
                {
                    await _cmLink.DisposeAsync();
                }

                if (_host is not null)
                {
                    await _host.DisposeAsync();
                }

                _engine?.Dispose();
                BookSync?.Dispose();
                Updates?.Dispose();
            };
            Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                lifecycle.LogError(e.Exception, "Unhandled exception on the UI thread");
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// A second launch, handed over by <see cref="SingleInstance"/>. It carries the arguments that process
    /// was started with, so opening a link while DeskPair is already running connects instead of merely
    /// raising the window.
    /// </summary>
    private static void OnSecondLaunch(IClassicDesktopStyleApplicationLifetime desktop, string[] started)
    {
        ShowMainWindow(desktop);
        if (started.SkipWhile(a => a != "--connect").Skip(1).FirstOrDefault() is { Length: > 0 } target)
        {
            OpenRemoteSession(target, started.SkipWhile(a => a != "--password").Skip(1).FirstOrDefault());
        }
    }

    /// <summary>
    /// Builds the approval card on demand and brings it forward. A closed window cannot be shown again, so
    /// each time the card has gone a fresh one takes its place; the view model behind it is the same one that
    /// has been listening since start-up.
    /// </summary>
    private static void ShowConnectionManager(bool topmost)
    {
        if (_cmVm is null)
        {
            return;
        }

        if (_cmWindow is null)
        {
            _cmWindow = new ConnectionManagerWindow(_cmVm);
            _cmWindow.Closed += (_, _) => _cmWindow = null;
        }

        _cmWindow.Surface(topmost);
    }

    private static void SetupTray(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var menu = new NativeMenu();
        menu.Items.Add(Item("tray.show", MenuIcons.Window, () => ShowMainWindow(desktop)));
        menu.Items.Add(Item("tray.settings", MenuIcons.Settings, () => ShowMainWindow(desktop, MainWindowViewModel.SettingsSection)));
        menu.Items.Add(new NativeMenuItemSeparator());

        // Reserved for the account feature that does not exist yet; shown so the menu is the shape it will
        // keep, disabled so it cannot promise anything.
        menu.Items.Add(new NativeMenuItem(Strings.Get("tray.signOut"))
        {
            Icon = MenuIcons.Render(MenuIcons.SignOut, 0xFFE0524E),
            IsEnabled = false,
            ToolTip = Strings.Get("tray.signOutSoon"),
        });

        // Quitting stays: the tray is the only way back out of an application that has no window open, and
        // signing out is not going to be it.
        menu.Items.Add(Item("tray.quit", MenuIcons.Quit, () =>
        {
            IsShuttingDown = true;
            desktop.Shutdown();
        }));

        var tray = new TrayIcon { ToolTipText = Strings.Get("app.title"), Menu = menu, IsVisible = true, Icon = AppIcon() };
        tray.Clicked += (_, _) => ShowMainWindow(desktop);
        TrayIcon.SetIcons(Current!, [tray]);
    }

    private static NativeMenuItem Item(string key, string geometry, Action click, uint colour = 0xFFE6E9EF)
    {
        var item = new NativeMenuItem(Strings.Get(key)) { Icon = MenuIcons.Render(geometry, colour) };
        item.Click += (_, _) => click();
        return item;
    }

    /// <summary>The app mark for the tray; the same .ico the windows and the executable use.</summary>
    private static WindowIcon AppIcon()
    {
        using Stream stream = AssetLoader.Open(new Uri("avares://DeskPair/Assets/deskpair.ico"));
        return new WindowIcon(stream);
    }

    private static void ShowMainWindow(IClassicDesktopStyleApplicationLifetime desktop, int? section = null)
    {
        MainWindow? existing = desktop.Windows.OfType<MainWindow>().FirstOrDefault();
        if (existing is null)
        {
            existing = new MainWindow(new MainWindowViewModel(Host));
            existing.Show();
        }
        else
        {
            existing.Show();
            existing.Activate();
        }

        if (section is { } index && existing.DataContext is MainWindowViewModel vm)
        {
            vm.SelectedSection = index;
        }
    }

    public static void OpenRemoteSession(string target, string? password = null)
    {
        var vm = new RemoteSessionViewModel(target, Host.Id.Length > 0 ? Host.Id : "desktop", Config, Logs);
        if (password is not null)
        {
            vm.AutoPassword = password;
        }

        // Sessions share one window: reaching a second machine adds a tab rather than another window to
        // arrange. A window that is already open is brought forward, because the new tab is in it.
        RemoteSessionWindow? window = SessionWindows.OfType<RemoteSessionWindow>().FirstOrDefault();
        if (window is null)
        {
            window = new RemoteSessionWindow(new RemoteSessionsViewModel());
            Track(window);
        }
        else
        {
            window.Activate();
        }

        window.Open(vm);
    }

    /// <summary>One of a session's displays in a window of its own.</summary>
    public static void OpenScreenWindow(RemoteScreenViewModel screen) => Track(new RemoteScreenWindow(screen));

    /// <summary>Brings a display window forward.</summary>
    public static void ShowScreen(RemoteScreenViewModel screen) =>
        SessionWindows.OfType<RemoteScreenWindow>().FirstOrDefault(w => ReferenceEquals(w.DataContext, screen))?.Activate();

    /// <summary>Brings the window holding <paramref name="session"/> forward, with its tab selected.</summary>
    public static void ShowSession(RemoteSessionViewModel session)
    {
        RemoteSessionWindow? window = SessionWindows.OfType<RemoteSessionWindow>().FirstOrDefault(w => w.Sessions.Sessions.Contains(session));
        if (window is null)
        {
            return;
        }

        window.Sessions.Selected = session;
        window.Activate();
    }

    public static void OpenFileTransfer(string target)
    {
        var vm = new FileTransferViewModel(target, Host.Id.Length > 0 ? Host.Id : "desktop", Config, Logs);
        Track(new FileTransferWindow(vm));
    }

    /// <summary>A terminal on <paramref name="target"/>, in a window of its own: a shell is not a view of the desktop, and it outlives none.</summary>
    public static void OpenTerminal(string target)
    {
        var vm = new TerminalViewModel(target, Host.Id.Length > 0 ? Host.Id : "desktop", Config, Logs);
        Track(new TerminalWindow(vm));
    }

    public static void RememberPeer(string id, string? name, string? platform = null)
    {
        Config = Config.WithRecent(id, name, platform);
        try
        {
            Config.Save();

            // A saved device also records when it was last reached, so the device list can show it.
            DeviceBook book = DeviceBook.Load();
            DeviceBook touched = book.Touched(id, DateTimeOffset.UtcNow, platform);
            if (!ReferenceEquals(touched, book))
            {
                touched.Save();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static async Task CopyToClipboardAsync(string text)
    {
        if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow?.Clipboard is { } clipboard && text.Length > 0)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    private static void Track(Window window)
    {
        SessionWindows.Add(window);
        ILogger lifecycle = Logs.CreateLogger("lifecycle");
        window.Closing += (_, e) => lifecycle.LogInformation("{Window} closing (reason {Reason})", window.GetType().Name, e.CloseReason);
        window.Closed += (_, _) =>
        {
            lifecycle.LogInformation("{Window} closed", window.GetType().Name);
            SessionWindows.Remove(window);
        };
        window.Show();
    }
}
