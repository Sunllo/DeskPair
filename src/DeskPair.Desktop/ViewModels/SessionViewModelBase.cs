using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using DeskPair.Core.Config;
using DeskPair.Core.Session.Controller;
using DeskPair.Core.Transport;
using DeskPair.Desktop.Localization;
using DeskPair.Desktop.Services;
using DeskPair.Platform.Abstractions.Audio;
using DeskPair.Platform.Abstractions.Codec;
using DeskPair.Protocol.Messages;
using DeskPair.Protocol.Rendezvous;

namespace DeskPair.Desktop.ViewModels;

/// <summary>Connect → (password) → login flow shared by the remote-control and file-transfer windows.</summary>
public abstract partial class SessionViewModelBase : ObservableObject, IControllerCallbacks, IAsyncDisposable
{
    protected readonly CancellationTokenSource Cts = new();
    private readonly DesktopConfig _config;

    /// <summary>The controller settings this session started with; later edits do not disturb a live session.</summary>
    protected DesktopConfig Config => _config;
    private readonly string _myId;

    protected SessionViewModelBase(string target, string myId, DesktopConfig config, ILoggerFactory logs)
    {
        Target = target;
        _myId = myId;
        _config = config;
        Logs = logs;
        Log = logs.CreateLogger("session");
        Title = $"{target} - {Strings.Get("app.title")}";
        ShortTitle = target;
        PeerPlatform = string.Empty;
        Status = Strings.Get("session.connecting");
        Password = string.Empty;
        ErrorText = string.Empty;
    }

    public event Action? CloseRequested;

    public string Target { get; }

    protected ILoggerFactory Logs { get; }

    protected ILogger Log { get; }

    protected ControllerSession? Session { get; private set; }

    protected abstract ConnType ConnType { get; }

    [ObservableProperty]
    public partial string Title { get; set; }

    /// <summary>
    /// The same thing said in as few characters as possible, for a tab: the desk's own name once it has
    /// introduced itself, the id or address that was typed until then. The window title keeps the long form.
    /// </summary>
    [ObservableProperty]
    public partial string ShortTitle { get; set; }

    /// <summary>What the far end is running, for the logo on its tab. Empty until it says.</summary>
    [ObservableProperty]
    public partial string PeerPlatform { get; set; }

    /// <summary>True while nothing has been heard from the far end for long enough to be worth saying.</summary>
    [ObservableProperty]
    public partial bool IsStalled { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOverlay))]
    public partial bool IsConnected { get; set; }

    [ObservableProperty]
    public partial bool NeedsPassword { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string Password { get; set; }

    // ASCII only, as the text changes: see PasswordText for why a password has to survive two keyboards.
    partial void OnPasswordChanged(string value)
    {
        string ascii = Services.PasswordText.Ascii(value);
        if (!ReferenceEquals(ascii, value) && ascii != value)
        {
            Password = ascii;
        }
    }

    [ObservableProperty]
    public partial string ErrorText { get; set; }

    /// <summary>How the session was established (LAN, PunchedTcp, Relay, DirectTcp).</summary>
    [ObservableProperty]
    public partial string TransportName { get; set; } = string.Empty;

    public bool ShowOverlay => !IsConnected;

    /// <summary>Password supplied on the command line (testing / automation); used once when the host asks for one.</summary>
    public string? AutoPassword { get; set; }

    protected virtual ControllerSessionOptions ConfigureOptions(ControllerSessionOptions options) => options;

    /// <summary>
    /// What the far end is told this computer is called.
    ///
    /// The same answer the host gives, from the same setting -- which the settings page calls "the name
    /// shown to visitors" and says falls back to the computer name. It only ever applied in one
    /// direction: when somebody connected *to* this machine they saw the name, and when this machine
    /// called out they saw the operating system's user account instead, which is a different thing about
    /// a different subject and is often just "alice" or "Administrator".
    /// </summary>
    private static string CallerName()
    {
        string configured = App.Host.Config?.DeviceName.Trim() ?? string.Empty;
        return configured.Length > 0 ? configured : Environment.MachineName;
    }

    protected virtual Task OnAuthorizedAsync() => Task.CompletedTask;

    /// <summary>
    /// False only in the screenshot tool, whose windows show a session that was never dialled: opening one must not
    /// reach for a server, a portal or a peer, nor save the sample settings over the user's own.
    /// </summary>
    internal static bool Dials { get; set; } = true;

    public async Task StartAsync()
    {
        if (!Dials)
        {
            return;
        }

        string serverKey = PeerSettings.CleanBase64(_config.ServerPublicKeyBase64);
        if (serverKey.Length == 0 && _config.RendezvousServer.Length > 0 && !PeerConnector.IsDirectTarget(Target))
        {
            // First run without a key: fetch it once from the server and persist it.
            try
            {
                Status = Strings.Format("session.keyMissing", _config.RendezvousServer);
                serverKey = await SettingsViewModel.FetchKeyFromServerAsync(_config.RendezvousServer, Cts.Token);
                App.Config = App.Config with { ServerPublicKeyBase64 = serverKey };
                App.Config.Save();

                // Trust on first use, said out loud. The key came over plain HTTP from whoever answered
                // at that address, so the fingerprint is shown and logged: it is what the person compares
                // against the server's own start-up line if they ever wonder.
                string fingerprint = SettingsViewModel.KeyFingerprint(serverKey);
                Log.LogInformation("Pinned the rendezvous server key for {Server}: fingerprint {Fingerprint}", _config.RendezvousServer, fingerprint);
                Status = Strings.Format("session.keyPinned", fingerprint, _config.RendezvousServer);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Without the key the connector refuses to connect, and says why; nothing to hide here.
                Log.LogWarning(e, "Could not fetch the server key");
            }
        }

        // Where to signal, when nothing is configured here. The host half of this program has always
        // asked the portal for this; the controller half never did, and an unanswered question came out
        // as an empty address that resolves to this machine -- so every call went to localhost.
        Core.Portal.NetworkDirectory directory = PeerConnector.IsDirectTarget(Target)
            ? Core.Portal.NetworkDirectory.None
            : await App.Directory.GetAsync(_config.PortalServer, Cts.Token);

        PeerSettings settings = _config.ToPeerSettings(App.Version, serverKey, directory);
        if (settings.RendezvousServer.Length == 0 && !PeerConnector.IsDirectTarget(Target))
        {
            // Said out loud rather than dialled anyway. The attempt would fail at a socket, several
            // layers down, with a message about a refused connection to a machine nobody named.
            Status = Strings.Get("session.noServer");
            Log.LogWarning("No signalling server: none is configured and the portal did not name one");
            IsBusy = false;
            return;
        }

        ControllerSessionOptions options = ConfigureOptions(new ControllerSessionOptions(_myId, CallerName(), App.PlatformName, App.Version, ConnType));
        Session = new ControllerSession(options, this, Log);
        try
        {
            IsBusy = true;
            var connector = new PeerConnector(settings, Logs.CreateLogger("connector"), App.KnownHosts);
            await Session.ConnectAsync(connector, Target, Cts.Token);
            IsBusy = false;
            TransportName = Session.TransportKind?.ToString() ?? string.Empty;
            if (Session.Challenge?.ApproveMode == ApproveMode.ApproveClick)
            {
                await LoginAsync(); // the remote user decides; no password involved
            }
            else if (AutoPassword is { } auto)
            {
                Password = auto;
                AutoPassword = null;
                NeedsPassword = true;
                await LoginAsync();
            }
            else
            {
                NeedsPassword = true;
                Status = Strings.Get("session.password");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            IsBusy = false;
            Log.LogWarning(e, "Connect failed");
            ErrorText = Strings.Format("error.connectFailed", e.Message);
            Status = Strings.Get("session.closed");
        }
    }

    [RelayCommand]
    private async Task LoginAsync()
    {
        if (Session is null)
        {
            return;
        }

        try
        {
            IsBusy = true;
            ErrorText = string.Empty;
            Status = Strings.Get("session.waitingApproval");
            LoginResult result = await Session.LoginAsync(NeedsPassword ? Password : null, Cts.Token);
            IsBusy = false;
            if (result.Success)
            {
                NeedsPassword = false;
                IsConnected = true;
                App.RememberPeer(Target, result.PeerInfo?.Hostname, result.PeerInfo?.Platform);
                await OnAuthorizedAsync();
                return;
            }

            LoginError error = result.Error ?? new LoginError();
            ErrorText = error.Code == LoginError.Types.Code.TooManyAttempts && error.RetryAfterMs > 0
                ? Strings.Format("session.retryAfter", error.RetryAfterMs / 1000)
                : Strings.Format("error.loginFailed", error.Message.Length > 0 ? error.Message : error.Code.ToString());
            Status = Strings.Get("session.password");
            NeedsPassword = Session.Challenge?.ApproveMode != ApproveMode.ApproveClick;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            IsBusy = false;
            ErrorText = Strings.Format("error.loginFailed", e.Message);
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke();

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        if (Session is not null)
        {
            await Session.CloseAsync("closed by user");
        }

        CloseRequested?.Invoke();
    }

    protected void RequestClose() => CloseRequested?.Invoke();

    protected async Task SendAsync(Message message, Core.Session.MessagePriority priority = Core.Session.MessagePriority.Control)
    {
        if (Session is not { State: ControllerSessionState.Authorized })
        {
            return;
        }

        try
        {
            await Session.SendAsync(message, Cts.Token, priority);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.LogDebug(e, "Send failed");
        }
    }

    // ---- IControllerCallbacks (session thread) ----

    public virtual void OnStateChanged(ControllerSessionState state) => Dispatcher.UIThread.Post(() =>
    {
        if (state == ControllerSessionState.Closed && IsConnected)
        {
            IsConnected = false;
            Status = Strings.Get("session.closed");
        }
    });

    public virtual void OnPeerInfo(PeerInfo info) => Dispatcher.UIThread.Post(() =>
    {
        Title = $"{info.Hostname} ({Target}) - {Strings.Get("app.title")}";
        ShortTitle = info.Hostname.Length > 0 ? info.Hostname : Target;
        PeerPlatform = info.Platform;
    });

    public virtual void OnChat(ChatMessage message)
    {
    }

    public virtual void OnPermission(PermissionInfo info)
    {
    }

    public virtual void OnDisplaysChanged(DisplaysChanged info)
    {
    }

    public virtual void OnSecureDesktop(SecureDesktop info)
    {
    }

    public virtual void OnClosed(string reason) => Dispatcher.UIThread.Post(() =>
    {
        IsStalled = false;
        IsConnected = false;
        IsBusy = false;
        NeedsPassword = false;
        Status = Strings.Get("session.closed");
        if (reason.Length > 0)
        {
            ErrorText = reason;
        }
    });

    /// <summary>
    /// The host has gone quiet. The picture on screen is the last one that arrived and is now out of date,
    /// so say so over it rather than leaving a frozen desktop that looks live. The session is not over: it
    /// has until the read timeout to come back, and a lid, a change of network or a moment of congestion
    /// usually does come back.
    /// </summary>
    public virtual void OnStalled(bool stalled) => Dispatcher.UIThread.Post(() => IsStalled = stalled);

    public virtual void OnRoundTrip(TimeSpan rtt)
    {
    }

    public virtual void OnAudioFormat(AudioStreamFormat format)
    {
    }

    // Interface members with default implementations must be re-declared here: a derived class's methods
    // never map onto an interface its base class implements, so without these the frames would vanish silently.
    public virtual void OnVideoPacket(int display, uint seq, bool isKeyFrame)
    {
    }

    public virtual void OnVideoEncoded(int display, ReadOnlySpan<byte> frame, bool key, long ptsMs, int width, int height, DeskPair.Platform.Abstractions.Codec.VideoCodec codec)
    {
    }

    public virtual void OnAudioPcm(ReadOnlySpan<float> interleaved, long ptsMs)
    {
    }

    public virtual void OnVideoFrame(int display, in DecodedFrame frame)
    {
    }

    public virtual void OnCursorShape(CursorData shape)
    {
    }

    public virtual void OnCursorId(ulong id)
    {
    }

    public virtual void OnCursorPosition(CursorPosition position)
    {
    }

    public virtual void OnDisplaySwitched(int display)
    {
    }

    public virtual void OnDisplaySubscription(DisplaySubscription subscription)
    {
    }

    public virtual void OnTerminal(TerminalResponse response)
    {
    }

    public virtual async ValueTask DisposeAsync()
    {
        Cts.Cancel();
        if (Session is not null)
        {
            await Session.DisposeAsync();
        }

        Cts.Dispose();
        GC.SuppressFinalize(this);
    }
}
