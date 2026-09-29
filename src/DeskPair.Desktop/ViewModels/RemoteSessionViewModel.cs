using System.Diagnostics;
using System.Collections.ObjectModel;
using Avalonia.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using DeskPair.Core.Session.Controller;
using DeskPair.Desktop.Controls;
using DeskPair.Desktop.Input;
using DeskPair.Desktop.Localization;
using DeskPair.Desktop.Services;
using DeskPair.Platform.Abstractions.Audio;
using DeskPair.Platform.Abstractions.Codec;
using DeskPair.Protocol.Messages;
using DeskPair.Protocol.Rendezvous;

namespace DeskPair.Desktop.ViewModels;

/// <summary>A display the toolbar offers to open in a window of its own.</summary>
public sealed record DisplayChoice(int Index, string Label);

/// <summary>One remote-control session: toolbar state and the bridge between the core session and the display control.</summary>
public partial class RemoteSessionViewModel : SessionViewModelBase
{
    private RemoteDisplayView? _view;
    private IDisposable? _timerLease;
    private IAudioPlayback? _audio;
    private bool _suppressOptions;
    private readonly List<DisplayInfo> _displayInfos = [];
    private bool _keyboardAllowed = true;
    private DisplayResolution? _pendingResolution;

    /// <summary>A display asked to be added (true) or removed (false), until the host's answer arrives.</summary>
    private bool? _pendingVirtual;
    private SessionRecordingController? _recording;
    private DispatcherTimer? _recordingTimer;

    /// <summary>Keeps the tab's display the size of the tab, while <see cref="IsFollowing"/>.</summary>
    private readonly ResolutionFollower _follower;

    // ---- displays in windows of their own ----

    private readonly object _screensLock = new();
    private readonly List<RemoteScreenViewModel> _screens = [];

    /// <summary>
    /// For the video thread: the picture showing each display other than the tab's. Replaced whole rather than
    /// changed, so the thread reads it without a lock.
    /// </summary>
    private volatile Dictionary<int, RemoteDisplayView> _screenViews = [];

    /// <summary>The tab's display, for the video thread; <see cref="CurrentDisplay"/> belongs to the UI thread.</summary>
    private volatile int _tabDisplay;

    /// <summary>The host's name for the tab's display: how the tab keeps it when indices move.</summary>
    private string? _tabName;

    /// <summary>
    /// Set once this session has asked for a set of displays (a window was opened). From then on the host's
    /// SwitchDisplay only says where the focus is, and the tab changes display by subscribing, not switching.
    /// </summary>
    private bool _subscribing;

    /// <summary>The display with the viewer's attention: the tab's, or whichever display window was last in front.</summary>
    private int _focusDisplay = -1;

    /// <summary>The host said, at login, that it can stream several displays at once.</summary>
    private bool _hostMulti;

    /// <summary>
    /// What the host is actually sending. The recorder stores frames without re-encoding them, so it has to
    /// open the file for the right codec; assuming H.264 was safe only while H.264 was the only one we could
    /// negotiate. Frames carry the codec, so the last one seen is the answer.
    /// </summary>
    private Platform.Abstractions.Codec.VideoCodec _streamCodec = Platform.Abstractions.Codec.VideoCodec.H264;

    public RemoteSessionViewModel(string target, string myId, DesktopConfig config, ILoggerFactory logs)
        : base(target, myId, config, logs)
    {
        RttText = string.Empty;
        _follower = NewFollower();
        _follower.StateChanged += () => Dispatcher.UIThread.Post(UpdateMatchWindowStatus);
        FitToWindow = config.FitToWindow;
        MatchWindow = config.MatchWindowResolution;
        ShowRemoteCursor = config.ShowRemoteCursor;
        AudioEnabled = config.AudioEnabled;
        TranslateMode = config.KeyboardTranslateMode;
        SmoothPlayback = config.SmoothPlayback;
        LosslessRefinement = config.LosslessRefinement;
        LockAfterSessionEnd = config.LockAfterSessionEnd;
        CustomBitrateKbps = config.CustomBitrateKbps;
        QualityIndex = config.DefaultQuality switch { "low" => 0, "best" => 2, "custom" => 3, _ => 1 };

        // The settings page's frame rate belongs to its custom quality; any other default starts automatic.
        (IReadOnlyList<int> rates, IReadOnlyList<string> labels, int selected) =
            FrameRateChoices.Build(config.DefaultQuality == "custom" ? config.CustomFps : 0);
        _frameRates = rates;
        foreach (string label in labels)
        {
            FrameRates.Add(label);
        }

        FrameRateIndex = selected;
        Chat = new ChatViewModel(text => Session?.SendChatAsync(text, Cts.Token).AsTask() ?? Task.CompletedTask);
    }

    public ChatViewModel Chat { get; }

    public ObservableCollection<string> Displays { get; } = [];

    /// <summary>The modes the current remote display can be switched to, "original" first.</summary>
    public ObservableCollection<string> Resolutions { get; } = [];

    /// <summary>The toolbar's frame rates, "automatic" first; <see cref="FrameRate"/> is the chosen one as a number.</summary>
    public ObservableCollection<string> FrameRates { get; } = [];

    private readonly IReadOnlyList<int> _frameRates = [0];

    /// <summary>Displays that can be opened in a window of their own: every one but the tab's and those already open.</summary>
    public ObservableCollection<DisplayChoice> WindowChoices { get; } = [];

    /// <summary>The host streams several displays at once and has more than one.</summary>
    [ObservableProperty]
    public partial bool CanOpenDisplayWindows { get; set; }

    [ObservableProperty]
    public partial bool HasWindowChoices { get; set; }

    /// <summary>The host can be asked for a display it does not have: only a Windows host can make one.</summary>
    [ObservableProperty]
    public partial bool CanAddDisplays { get; set; }

    /// <summary>The tab shows a display that was added on request, and so can be taken away again.</summary>
    [ObservableProperty]
    public partial bool CurrentDisplayIsAdded { get; set; }

    protected override ConnType ConnType => ConnType.ConnRemote;

    [ObservableProperty]
    public partial string RttText { get; set; }

    [ObservableProperty]
    public partial int CurrentDisplay { get; set; }

    [ObservableProperty]
    public partial int ResolutionIndex { get; set; }

    /// <summary>The host advertised modes for this display and lets this viewer use the keyboard.</summary>
    [ObservableProperty]
    public partial bool CanChangeResolution { get; set; }

    /// <summary>
    /// The host said it has no display at all -- a server without a card, run by the Linux daemon. Not a
    /// stall and not an error: the session is up, the picture area is simply empty, and the banner says why.
    /// </summary>
    [ObservableProperty]
    public partial bool HasNoDisplays { get; set; }

    /// <summary>
    /// The host's own word on why its displays are what they are: a Wayland desktop waiting for its person to allow
    /// screen sharing, sharing refused or stopped there. In the host's words, shown in place of "no screen", which
    /// would be untrue of a machine whose screen is simply not shared yet.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHostNotice))]
    public partial string HostNotice { get; set; } = string.Empty;

    public bool HasHostNotice => HostNotice.Length > 0;

    [ObservableProperty]
    public partial int QualityIndex { get; set; }

    [ObservableProperty]
    public partial bool TranslateMode { get; set; }

    [ObservableProperty]
    public partial bool FitToWindow { get; set; }

    /// <summary>
    /// The viewer wants the remote display the size of its window, as a Windows remote desktop session is: shown 1:1,
    /// sharp whatever the window. A wish: it is acted on (<see cref="IsFollowing"/>) while the display can be changed.
    /// </summary>
    [ObservableProperty]
    public partial bool MatchWindow { get; set; }

    /// <summary>The tab's display is being kept the size of the tab. Fit and the resolution picker wait meanwhile.</summary>
    [ObservableProperty]
    public partial bool IsFollowing { get; set; }

    /// <summary>The display shown in the tab can be changed from here: it has modes or takes any size, and the keyboard is allowed.</summary>
    [ObservableProperty]
    public partial bool CanMatchWindow { get; set; }

    /// <summary>What following the window is doing, when there is something to say: resizing, refused, taken over.</summary>
    [ObservableProperty]
    public partial string MatchWindowStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ShowRemoteCursor { get; set; }

    [ObservableProperty]
    public partial bool AudioEnabled { get; set; }

    /// <summary>Jitter buffer: steadier motion at the cost of a frame or two of delay.</summary>
    [ObservableProperty]
    public partial bool SmoothPlayback { get; set; }

    [ObservableProperty]
    public partial bool LosslessRefinement { get; set; }

    [ObservableProperty]
    public partial bool LockAfterSessionEnd { get; set; }

    [ObservableProperty]
    public partial int CustomBitrateKbps { get; set; }

    [ObservableProperty]
    public partial int FrameRateIndex { get; set; }

    /// <summary>The frame rate asked of the host: 0 lets it choose (its own cap, lowered on a slow link).</summary>
    public int FrameRate => FrameRateIndex >= 0 && FrameRateIndex < _frameRates.Count ? _frameRates[FrameRateIndex] : 0;

    /// <summary>True while the session is being written to a file.</summary>
    [ObservableProperty]
    public partial bool IsRecording { get; set; }

    [ObservableProperty]
    public partial string RecordingText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ChatVisible { get; set; }

    /// <summary>
    /// True for the tab the user is looking at. The session runs either way — it stays connected and keeps
    /// receiving — but only the active one is drawn.
    /// </summary>
    [ObservableProperty]
    public partial bool IsActive { get; set; } = true;

    public void Attach(RemoteDisplayView view)
    {
        _view = view;
        _timerLease ??= DesktopPlatform.RequestHighResolutionTimer();
        view.FitToWindow = FitToWindow;
        view.ShowRemoteCursor = ShowRemoteCursor;
        view.SmoothPlayback = SmoothPlayback;
        view.MouseInput += e => _ = SendAsync(new Message { MouseEvent = e }, Core.Session.MessagePriority.Input);
        view.KeyInput += OnKey;
        view.RenderFailed += e => Log.LogError(e, "Rendering the remote frame failed");
        view.FillingSizeChanged += ReportWindow;
        ReportWindow();
    }

    /// <summary>A follower for one of this session's pictures: it asks the host through this session.</summary>
    internal ResolutionFollower NewFollower() => new(TimeProvider.System, (display, mode) =>
        Session is { State: ControllerSessionState.Authorized } session
            ? session.SetResolutionAsync(display, mode, Cts.Token).AsTask()
            : Task.CompletedTask);

    /// <summary>Tells the follower how much of the display the tab holds at 1:1. Reported whether or not it follows, so it knows at once.</summary>
    private void ReportWindow()
    {
        if (_view is { } view)
        {
            (int width, int height, double uiScale) = view.FillingSize;
            _follower.Window(width, height, uiScale);
        }

        SyncFollowOption();
    }

    /// <summary>What the host was last told of this viewer's following: whether it follows, and whether the window's size came with it.</summary>
    private (bool Follows, bool Sized)? _followTold;

    /// <summary>
    /// Tells the host when this viewer starts or stops following its window, and the window's size once it is known:
    /// a host whose owner allows it gives a following viewer a private screen of that size. Not on every resize --
    /// once the screen is there, the follower sets its size like any other display's.
    /// </summary>
    private void SyncFollowOption()
    {
        if (Session is not { State: ControllerSessionState.Authorized })
        {
            _followTold = null;
            return;
        }

        (int width, int height, _) = _view?.FillingSize ?? default;
        (bool, bool) now = (MatchWindow, MatchWindow && width > 0 && height > 0);
        if (_followTold != now)
        {
            _followTold = now;
            _ = ApplyOptionsAsync();
        }
    }

    /// <summary>
    /// Starts or stops following to match what the viewer wants and what the host allows, for the tab and every display
    /// window. <paramref name="again"/> starts over a follower already running: back after a dropped connection, the
    /// host has put its screen back and remembers nothing of what this viewer set.
    /// </summary>
    private void ApplyFollowing(bool again = false)
    {
        bool authorized = Session is { State: ControllerSessionState.Authorized };
        bool on = MatchWindow && CanMatchWindow && authorized;
        if (on && (!IsFollowing || again))
        {
            IsFollowing = true;
            FitToWindow = true;
            _follower.Start(CurrentDisplay, InfoOf(CurrentDisplay));
            ReportWindow();
        }
        else if (!on && IsFollowing)
        {
            IsFollowing = false;
            _ = _follower.StopAsync();
        }

        SyncFollowOption();

        foreach (RemoteScreenViewModel screen in ScreensSnapshot())
        {
            screen.Follow(MatchWindow && authorized && _keyboardAllowed && CanBeChanged(InfoOf(screen.DisplayIndex)), InfoOf(screen.DisplayIndex), again);
        }

        UpdateMatchWindowStatus();
    }

    /// <summary>Passes what the host said about its displays to every follower: an answer, somebody else's change, a new list.</summary>
    private void NotifyFollowers(int changed = -1, string failure = "")
    {
        if (IsFollowing)
        {
            _follower.Heard(CurrentDisplay, InfoOf(CurrentDisplay), changed, failure);
        }

        foreach (RemoteScreenViewModel screen in ScreensSnapshot())
        {
            screen.Heard(InfoOf(screen.DisplayIndex), changed, failure);
        }
    }

    /// <summary>A request of a follower's is waiting for its answer: a refusal now is its to report, not a toast's.</summary>
    private bool FollowerWaiting() => (IsFollowing && _follower.IsWaiting) || ScreensSnapshot().Any(s => s.IsWaiting);

    internal DisplayInfo? InfoOf(int display) => display >= 0 && display < _displayInfos.Count ? _displayInfos[display] : null;

    internal static bool CanBeChanged(DisplayInfo? display) => display is not null && (display.AnySize is not null || display.Modes.Count > 0);

    private void UpdateMatchWindowStatus() => MatchWindowStatus = FollowStatus(
        MatchWindow && Session is { State: ControllerSessionState.Authorized }, CanMatchWindow, _follower);

    /// <summary>The words for a follower's state; empty when all is as asked or nothing is being followed.</summary>
    internal static string FollowStatus(bool wanted, bool possible, ResolutionFollower follower) =>
        !wanted ? string.Empty
        : !possible ? Strings.Get("session.matchWindow.unsupported")
        : follower.State switch
        {
            FollowState.Waiting => Strings.Get("session.matchWindow.waiting"),
            FollowState.Failed => Strings.Format("session.matchWindow.failed", follower.Failure.Length > 0 ? follower.Failure : Strings.Get("session.matchWindow.noAnswer")),
            FollowState.Overridden => Strings.Get("session.matchWindow.overridden"),
            FollowState.Unsupported => Strings.Get("session.matchWindow.unsupported"),
            _ => string.Empty,
        };

    protected override ControllerSessionOptions ConfigureOptions(ControllerSessionOptions options)
    {
        _audio = DesktopPlatform.CreateAudioPlayback(Logs, Config.AudioPlaybackDeviceId, Config.AudioExclusive);
        // The user's defaults ride along with the login, so the first frame already uses them.
        return options with
        {
            Decoders = DesktopPlatform.CreateDecoders(Logs),
            AudioPlayback = _audio,
            Clipboard = DesktopPlatform.CreateClipboard(Logs),
            SessionOptions = BuildOptions(),
        };
    }

    protected override async Task OnAuthorizedAsync()
    {
        _view?.Focus();
        await ApplyOptionsAsync();
        ApplyFollowing(again: true);
        if (!MatchWindow)
        {
            // Following the window decides the size itself; a remembered choice would only be undone by it.
            await ReapplySavedResolutionAsync();
        }

        // Back after a dropped connection: the display windows are still open, so ask for them again.
        if (_subscribing)
        {
            await SubscribeAsync();
        }
    }

    /// <summary>
    /// The resolution this viewer chose for the display last time is asked for again, once, if the host
    /// still offers it and is not already there. Read from the session's own copy of the peer info: the
    /// UI's lists are filled on the UI thread and may not be yet.
    /// </summary>
    private async Task ReapplySavedResolutionAsync()
    {
        if (Session is not { State: ControllerSessionState.Authorized, PeerInfo: { } info })
        {
            return;
        }

        int index = info.CurrentDisplay;
        if (index < 0 || index >= info.Displays.Count)
        {
            return;
        }

        DisplayInfo display = info.Displays[index];
        if (Config.ResolutionFor(Target, display.Name) is not { } saved)
        {
            return;
        }

        var wanted = new Resolution { Width = saved.Width, Height = saved.Height, Scale = saved.Scale };
        if (ResolutionChoices.Matches(wanted, display) || !display.Modes.Any(m => ResolutionChoices.Same(m, wanted)))
        {
            return;
        }

        Log.LogInformation("Asking {Target} for the remembered {W}x{H} on {Display}", Target, saved.Width, saved.Height, display.Name);
        await Session.SetResolutionAsync(index, wanted, Cts.Token);
    }

    [RelayCommand]
    private Task CtrlAltDelAsync() => SendAsync(new Message { KeyEvent = KeyMapper.Special(Protocol.Messages.ControlKey.CkCtrlAltDel, true) });

    [RelayCommand]
    private Task LockAsync() => SendAsync(new Message { KeyEvent = KeyMapper.Special(Protocol.Messages.ControlKey.CkLockScreen, true) });

    [RelayCommand]
    private Task RefreshAsync() => Session?.RefreshVideoAsync(CurrentDisplay, Cts.Token).AsTask() ?? Task.CompletedTask;

    [RelayCommand]
    private void OpenFiles() => App.OpenFileTransfer(Target);

    [RelayCommand]
    private void OpenTerminal() => App.OpenTerminal(Target);

    partial void OnChatVisibleChanged(bool value)
    {
        if (value)
        {
            Chat.MarkRead();
        }
    }

    partial void OnCurrentDisplayChanged(int value)
    {
        if (value < 0 || _suppressOptions || Session is not { State: ControllerSessionState.Authorized })
        {
            return;
        }

        ShowInTab(value);
        if (_subscribing)
        {
            // A window showing the display the tab has just moved to closes: one display, one place.
            foreach (RemoteScreenViewModel screen in ScreensSnapshot().Where(s => s.DisplayIndex == value))
            {
                RemoveScreen(screen, closeWindow: true);
            }

            _focusDisplay = value;
            _ = SubscribeAsync();
        }
        else
        {
            _ = Session.SwitchDisplayAsync(value, Cts.Token).AsTask();
        }

        PopulateResolutions();
        RefreshWindowChoices();
    }

    /// <summary>Points the tab's picture at a display. UI thread.</summary>
    private void ShowInTab(int display)
    {
        _tabDisplay = display;
        _tabName = display >= 0 && display < _displayInfos.Count ? _displayInfos[display].Name : null;
        CurrentDisplayIsAdded = display >= 0 && display < _displayInfos.Count && _displayInfos[display].VirtualDisplay;
        if (_view is not null)
        {
            _view.DisplayIndex = display;
            _view.Origin = OriginOf(display);
        }
    }

    // ---- displays in windows of their own ----

    /// <summary>Where a display's top-left corner sits on the host's desktop.</summary>
    internal (int X, int Y) OriginOf(int display) =>
        display >= 0 && display < _displayInfos.Count ? (_displayInfos[display].X, _displayInfos[display].Y) : (0, 0);

    internal void SendMouse(MouseEvent e) => _ = SendAsync(new Message { MouseEvent = e }, Core.Session.MessagePriority.Input);

    internal void SendKey(KeyEventArgs e, bool down) => OnKey(e, down);

    internal Task RefreshDisplayAsync(int display) => Session?.RefreshVideoAsync(display, Cts.Token).AsTask() ?? Task.CompletedTask;

    /// <summary>The tab's window came to the front.</summary>
    internal void TabActivated() => FocusDisplay(CurrentDisplay);

    /// <summary>
    /// A display has the viewer's attention: the host is told, so the display being looked at gets the larger
    /// share of the link. Nothing is sent while only the tab is open -- there is nothing to share.
    /// </summary>
    internal void FocusDisplay(int display)
    {
        if (!_subscribing || display < 0 || display == _focusDisplay)
        {
            return;
        }

        _focusDisplay = display;
        _ = SubscribeAsync();
    }

    /// <summary>
    /// Asks the host for a display it does not have, at <paramref name="width"/>x<paramref name="height"/> -- the
    /// viewer's own screen, so the window it opens in can fill that screen -- and able to take <paramref name="sizes"/>
    /// later on. The answer is the new display list, and the new display opens in a window of its own.
    /// </summary>
    internal void AddDisplay(int width, int height, IEnumerable<(int Width, int Height)> sizes)
    {
        if (Session is not { State: ControllerSessionState.Authorized } session || !CanAddDisplays)
        {
            return;
        }

        _pendingVirtual = true;
        _ = session.AddVirtualDisplayAsync(
            width > 0 && height > 0 ? new Resolution { Width = width, Height = height } : null,
            sizes.Where(s => s.Width > 0 && s.Height > 0).Distinct().Select(s => new Resolution { Width = s.Width, Height = s.Height }),
            Cts.Token).AsTask();
    }

    /// <summary>Takes away the added display the tab is showing; the tab moves to one that is still there.</summary>
    [RelayCommand]
    private async Task RemoveDisplayAsync()
    {
        if (Session is not { State: ControllerSessionState.Authorized } session || !CurrentDisplayIsAdded)
        {
            return;
        }

        _pendingVirtual = false;
        await session.RemoveVirtualDisplayAsync(CurrentDisplay, Cts.Token);
    }

    /// <summary>
    /// The display-window button: the one display there is to open opens now, and nothing comes back; with
    /// several the choices come back to be offered.
    /// </summary>
    internal IReadOnlyList<DisplayChoice> OpenOrOfferDisplayWindow()
    {
        if (WindowChoices.Count == 1)
        {
            OpenDisplayWindow(WindowChoices[0].Index);
            return [];
        }

        return [.. WindowChoices];
    }

    /// <summary>Opens a display in a window of its own, or brings its window forward if it is already open.</summary>
    [RelayCommand]
    private void OpenDisplayWindow(int index)
    {
        // Each refusal is logged: from the outside all of them look the same, a button that did nothing.
        if (Session is not { State: ControllerSessionState.Authorized } session)
        {
            Log.LogInformation("Display window {Index} not opened: the session is {State}", index, Session?.State.ToString() ?? "not started");
            return;
        }

        if (!_hostMulti || index == CurrentDisplay || index < 0 || index >= _displayInfos.Count)
        {
            Log.LogInformation("Display window {Index} not opened: host streams several {Multi}, tab shows {Tab}, {Count} displays", index, _hostMulti, CurrentDisplay, _displayInfos.Count);
            return;
        }

        if (ScreensSnapshot().FirstOrDefault(s => s.DisplayIndex == index) is { } open)
        {
            App.ShowScreen(open);
            return;
        }

        Log.LogInformation("Opening display {Index} ({Name}) of {Host} in a window of its own", index, _displayInfos[index].Name, session.PeerInfo?.Hostname);
        var screen = new RemoteScreenViewModel(this, _displayInfos[index].Name, index) { Title = ScreenTitle(index) };
        lock (_screensLock)
        {
            _screens.Add(screen);
        }

        App.OpenScreenWindow(screen);
        RebuildRouting();
        RefreshWindowChoices();
        _focusDisplay = index;
        _ = SubscribeAsync();
        ApplyFollowing();
    }

    /// <summary>The user closed a display window: that display stops streaming; the session carries on.</summary>
    internal void CloseScreen(RemoteScreenViewModel screen)
    {
        RemoveScreen(screen, closeWindow: false);
        if (_focusDisplay == screen.DisplayIndex)
        {
            _focusDisplay = CurrentDisplay;
        }

        _ = SubscribeAsync();
    }

    private void RemoveScreen(RemoteScreenViewModel screen, bool closeWindow)
    {
        bool removed;
        lock (_screensLock)
        {
            removed = _screens.Remove(screen);
        }

        if (removed)
        {
            screen.StopFollowing();
        }

        if (removed && closeWindow)
        {
            screen.RequestClose();
        }

        RebuildRouting();
        RefreshWindowChoices();
    }

    private List<RemoteScreenViewModel> ScreensSnapshot()
    {
        lock (_screensLock)
        {
            return [.. _screens];
        }
    }

    /// <summary>Asks for the tab's display and every open window's, the whole set at once.</summary>
    private async Task SubscribeAsync()
    {
        if (Session is not { State: ControllerSessionState.Authorized } session)
        {
            return;
        }

        (int[] displays, int focus) = DisplayWindowPlan.Request(CurrentDisplay, ScreensSnapshot().Select(s => s.DisplayIndex), _focusDisplay);
        _subscribing = true;
        try
        {
            await session.SubscribeDisplaysAsync(displays, focus, Cts.Token);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.LogDebug(e, "Display subscription failed to send");
        }
    }

    private void RebuildRouting()
    {
        var views = new Dictionary<int, RemoteDisplayView>();
        foreach (RemoteScreenViewModel screen in ScreensSnapshot())
        {
            if (screen.View is { } view)
            {
                views[screen.DisplayIndex] = view;
            }
        }

        _screenViews = views;
    }

    private void RefreshWindowChoices()
    {
        HashSet<int> open = [.. ScreensSnapshot().Select(s => s.DisplayIndex)];
        WindowChoices.Clear();
        for (int i = 0; i < _displayInfos.Count && i < Displays.Count; i++)
        {
            if (i != CurrentDisplay && !open.Contains(i))
            {
                WindowChoices.Add(new DisplayChoice(i, Displays[i]));
            }
        }

        HasWindowChoices = WindowChoices.Count > 0;
        CanOpenDisplayWindows = _hostMulti && _displayInfos.Count > 1;
    }

    private string ScreenTitle(int index) => Strings.Format("screen.title", Title, index + 1);

    private IEnumerable<RemoteDisplayView> AllViews()
    {
        if (_view is not null)
        {
            yield return _view;
        }

        foreach (RemoteDisplayView view in _screenViews.Values)
        {
            yield return view;
        }
    }

    partial void OnResolutionIndexChanged(int value)
    {
        if (value < 0 || _suppressOptions || Session is not { State: ControllerSessionState.Authorized })
        {
            return;
        }

        if (CurrentDisplay < 0 || CurrentDisplay >= _displayInfos.Count)
        {
            return;
        }

        Resolution? chosen = ResolutionChoices.ModeAt(_displayInfos[CurrentDisplay], value);
        _pendingResolution = new DisplayResolution { Display = CurrentDisplay, Resolution = chosen };
        _ = Session.SetResolutionAsync(CurrentDisplay, chosen, Cts.Token).AsTask();
    }

    /// <summary>Rebuilds the display list from what the host says. UI thread, under <see cref="_suppressOptions"/>.</summary>
    private void PopulateDisplays(IEnumerable<DisplayInfo> displays, int current)
    {
        _displayInfos.Clear();
        _displayInfos.AddRange(displays);
        HasNoDisplays = _displayInfos.Count == 0;
        Displays.Clear();
        for (int i = 0; i < _displayInfos.Count; i++)
        {
            DisplayInfo d = _displayInfos[i];
            Displays.Add($"{i + 1}: {d.Width}x{d.Height}{(d.Primary ? " *" : string.Empty)}{(d.VirtualDisplay ? " · " + Strings.Get("session.addedDisplay") : string.Empty)}");
        }

        // With display windows open the tab keeps its own display, found by name: the host's "current" is
        // wherever the focus is, which may well be a window.
        if (_subscribing && _tabName is not null && _displayInfos.FindIndex(d => d.Name == _tabName) is var kept and >= 0)
        {
            current = kept;
        }

        CurrentDisplay = -1; // force the combo box binding to pick up the new item list
        CurrentDisplay = current;
        ShowInTab(current);

        // Window titles and origins follow the new list; which display each window shows was settled by name
        // when the host's subscription answer arrived.
        foreach (RemoteScreenViewModel screen in ScreensSnapshot())
        {
            screen.Title = ScreenTitle(screen.DisplayIndex);
            if (screen.View is { } view)
            {
                view.Origin = OriginOf(screen.DisplayIndex);
            }
        }

        PopulateResolutions();
        RefreshWindowChoices();
    }

    /// <summary>The resolution list for the current display, selected at the mode it is in now.</summary>
    private void PopulateResolutions()
    {
        bool was = _suppressOptions;
        _suppressOptions = true;
        try
        {
            Resolutions.Clear();
            int selected = 0;
            bool any = false;
            if (CurrentDisplay >= 0 && CurrentDisplay < _displayInfos.Count)
            {
                DisplayInfo display = _displayInfos[CurrentDisplay];
                (IReadOnlyList<string> labels, selected) = ResolutionChoices.Build(display);
                foreach (string label in labels)
                {
                    Resolutions.Add(label);
                }

                any = display.Modes.Count > 0;
            }

            ResolutionIndex = -1;
            ResolutionIndex = selected;
            CanChangeResolution = any && _keyboardAllowed;
            CanMatchWindow = _keyboardAllowed && CanBeChanged(InfoOf(CurrentDisplay));
        }
        finally
        {
            _suppressOptions = was;
        }

        ApplyFollowing();
        NotifyFollowers();
    }

    /// <summary>A confirmed change of this viewer's own asking is remembered for next time; "original" forgets it.</summary>
    private void RememberResolution(DisplaysChanged info)
    {
        if (_pendingResolution is not { } asked || asked.Display != info.Changed || asked.Display >= info.Displays.Count)
        {
            return;
        }

        DisplayInfo display = info.Displays[asked.Display];
        PeerResolution? keep = null;
        if (asked.Resolution is { } wanted)
        {
            if (!ResolutionChoices.Matches(wanted, display))
            {
                return; // somebody else's change landed in between; it is not what this viewer asked for
            }

            keep = new PeerResolution(Target, display.Name, wanted.Width, wanted.Height, wanted.Scale);
        }

        _pendingResolution = null;
        App.Config = App.Config.WithPeerResolution(Target, display.Name, keep);
        try
        {
            App.Config.Save();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.LogWarning(e, "Could not save the resolution choice");
        }
    }

    /// <summary>The options the host is told about; shared by the login message and every later change.</summary>
    internal SessionOptions BuildOptions() => new()
    {
        ImageQuality = QualityIndex switch
        {
            0 => ImageQuality.IqLow,
            2 => ImageQuality.IqBest,
            3 => ImageQuality.IqCustom,
            _ => ImageQuality.IqBalanced,
        },
        CustomBitrateKbps = QualityIndex == 3 ? CustomBitrateKbps : 0,

        // With any quality: the host caps the frame rate at what a viewer asks whatever the quality, and keeping
        // it apart means a lower frame rate does not also fix the bitrate, as the custom quality does.
        CustomFps = FrameRate,
        ShowRemoteCursor = ShowRemoteCursor ? BoolOption.BoYes : BoolOption.BoNo,
        DisableAudio = AudioEnabled ? BoolOption.BoNo : BoolOption.BoYes,
        LosslessRefinement = LosslessRefinement ? BoolOption.BoYes : BoolOption.BoNo,
        LockAfterSessionEnd = LockAfterSessionEnd ? BoolOption.BoYes : BoolOption.BoNo,
        FollowWindow = MatchWindow ? BoolOption.BoYes : BoolOption.BoNo,
        Viewport = MatchWindow && _view?.FillingSize is (> 0 and var width, > 0 and var height, _) ? new Resolution { Width = width, Height = height } : null,
        Screens = { MatchWindow && _view is { } view ? RemoteDisplayView.ScreenSizes(view).Select(s => new Resolution { Width = s.Width, Height = s.Height }) : [] },
    };

    partial void OnQualityIndexChanged(int value) => _ = ApplyOptionsAsync();

    partial void OnFrameRateIndexChanged(int value) => _ = ApplyOptionsAsync();

    partial void OnLosslessRefinementChanged(bool value) => _ = ApplyOptionsAsync();

    partial void OnLockAfterSessionEndChanged(bool value) => _ = ApplyOptionsAsync();

    partial void OnSmoothPlaybackChanged(bool value)
    {
        foreach (RemoteDisplayView view in AllViews())
        {
            view.SmoothPlayback = value;
        }
    }

    partial void OnAudioEnabledChanged(bool value) => _ = ApplyOptionsAsync();

    partial void OnShowRemoteCursorChanged(bool value)
    {
        foreach (RemoteDisplayView view in AllViews())
        {
            view.ShowRemoteCursor = value;
            view.InvalidateVisual();
        }

        _ = ApplyOptionsAsync();
    }

    partial void OnMatchWindowChanged(bool value) => ApplyFollowing();

    partial void OnFitToWindowChanged(bool value)
    {
        if (_view is not null)
        {
            _view.FitToWindow = value;
            _view.InvalidateMeasure();
            _view.InvalidateVisual();
        }
    }

    private async Task ApplyOptionsAsync()
    {
        if (Session is not { State: ControllerSessionState.Authorized } || _suppressOptions)
        {
            return;
        }

        SessionOptions options = BuildOptions();
        try
        {
            await Session.SetOptionsAsync(options, Cts.Token);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.LogDebug(e, "Options update failed");
        }
    }

    private void OnKey(KeyEventArgs e, bool down)
    {
        KeyEvent? wire = KeyMapper.Map(e, down, TranslateMode);
        if (wire is not null)
        {
            _ = SendAsync(new Message { KeyEvent = wire }, Core.Session.MessagePriority.Input);
        }
    }

    // ---- callbacks (session thread) ----

    public override void OnPeerInfo(PeerInfo info)
    {
        base.OnPeerInfo(info);
        _hostMulti = info.MultiDisplay;
        Dispatcher.UIThread.Post(() =>
        {
            CanAddDisplays = info.MultiDisplay && info.Platform == "Windows";
            _suppressOptions = true;
            PopulateDisplays(info.Displays, info.CurrentDisplay);
            _suppressOptions = false;
            Log.LogInformation("Peer {Host}: {Displays} display(s), current {Current}, version {Version}", info.Hostname, info.Displays.Count, info.CurrentDisplay, info.Version);
        });
    }

    public override void OnChat(ChatMessage message) => Dispatcher.UIThread.Post(() =>
    {
        Chat.Received(message.Text);
        if (ChatVisible)
        {
            Chat.MarkRead();
        }
    });

    private long _statTicks, _statVideoBytes, _statTileBytes;

    public override void OnRoundTrip(TimeSpan rtt)
    {
        string streams = string.Empty;
        if (Session is { } s)
        {
            long now = Stopwatch.GetTimestamp();
            long videoBytes = s.VideoBytesReceived, tileBytes = s.TileBytesReceived;
            double seconds = _statTicks == 0 ? 0 : Stopwatch.GetElapsedTime(_statTicks, now).TotalSeconds;
            if (seconds > 0.2)
            {
                double videoMbps = (videoBytes - _statVideoBytes) * 8 / seconds / 1_000_000;
                double tileKBps = (tileBytes - _statTileBytes) / seconds / 1024;
                string path = s.MediaPath is { } mp ? $" · UDP {mp.Kind}" : string.Empty;
                streams = $"  {Strings.Get("session.stream.video")} {videoMbps:F1} Mb/s · {Strings.Get("session.stream.tiles")} {tileKBps:F0} KB/s · {s.DecodedFps:F0} fps{path}";
            }

            _statTicks = now;
            _statVideoBytes = videoBytes;
            _statTileBytes = tileBytes;
        }

        Dispatcher.UIThread.Post(() => RttText = $"{TransportName}  {Strings.Get("session.rtt")} {rtt.TotalMilliseconds:F0} ms{streams}");
    }

    private static readonly bool NoRender = Environment.GetEnvironmentVariable("SUNLLO_NO_RENDER") is { Length: > 0 }; // diagnostics

    private long _framesShown;

    /// <summary>
    /// With one display the host streams exactly the one subscribed to, so every decoded frame is shown in the
    /// tab. With display windows open each frame goes to the picture showing its display.
    /// </summary>
    public override void OnVideoFrame(int display, in DecodedFrame frame)
    {
        if (NoRender)
        {
            return;
        }

        if (!_subscribing || display == _tabDisplay)
        {
            _view?.SubmitFrame(frame);
        }
        else if (_screenViews.TryGetValue(display, out RemoteDisplayView? window))
        {
            window.SubmitFrame(frame);
        }

        if (++_framesShown % 300 == 1)
        {
            Log.LogInformation("Video: {Frames} frames shown, display {Display} {W}x{H}", _framesShown, display, frame.Width, frame.Height);
        }
    }

    public override void OnVideoEncoded(int display, ReadOnlySpan<byte> frame, bool key, long ptsMs, int width, int height, Platform.Abstractions.Codec.VideoCodec codec)
    {
        if (display == CurrentDisplay)
        {
            _streamCodec = codec;
            _recording?.WriteVideo(frame, key, ptsMs, width, height, codec);
        }
    }

    public override void OnAudioPcm(ReadOnlySpan<float> interleaved, long ptsMs) => _recording?.WriteAudio(interleaved, ptsMs);

    public override void OnAudioFormat(Platform.Abstractions.Audio.AudioStreamFormat format)
    {
        base.OnAudioFormat(format);
        _recording?.SetAudioFormat(format);
    }

    /// <summary>Starts or stops writing this session to a file.</summary>
    [RelayCommand]
    private async Task ToggleRecordingAsync()
    {
        if (_recording is { IsActive: true })
        {
            await _recording.StopAsync();
            IsRecording = false;
            _recordingTimer?.Stop();
            RecordingText = _recording.LastPath is { } path ? Strings.Format("session.recordSaved", Path.GetFileName(path)) : string.Empty;
            return;
        }

        if (DesktopPlatform.CreateRecorders(Logs) is not { } factory)
        {
            RecordingText = Strings.Get("session.recordUnsupported");
            return;
        }

        _recording ??= new SessionRecordingController(factory, Config.RecordingFolder, Target, Config.RecordAudio, Log);
        _recording.Failed += reason => Dispatcher.UIThread.Post(() =>
        {
            IsRecording = false;
            RecordingText = Strings.Format("session.recordFailed", reason);
        });
        if (!_recording.Start(_streamCodec))
        {
            RecordingText = Strings.Get("session.recordUnsupported");
            return;
        }

        IsRecording = true;
        RecordingText = Strings.Get("session.recordStarting");

        // A file has to open on a keyframe, so ask for one instead of waiting for the next natural one.
        if (Session is not null)
        {
            await Session.RefreshVideoAsync(CurrentDisplay, Cts.Token);
        }

        _recordingTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            if (_recording is { IsActive: true } active)
            {
                RecordingText = active.HasStarted
                    ? FormatRecording(active.Duration, active.Bytes)
                    : Strings.Get("session.recordStarting");
            }
        });
        _recordingTimer.Start();
    }

    private static string FormatRecording(TimeSpan duration, long bytes) =>
        string.Create(System.Globalization.CultureInfo.CurrentCulture, $"{(int)duration.TotalMinutes:00}:{duration.Seconds:00} - {bytes / 1024 / 1024} MB");

    // The pointer is the host's, not a display's: every picture gets its shape and position, and each draws it
    // only when it is over its own display.
    public override void OnCursorShape(CursorData shape)
    {
        foreach (RemoteDisplayView view in AllViews())
        {
            view.SetCursorShape(shape);
        }
    }

    public override void OnCursorId(ulong id)
    {
        foreach (RemoteDisplayView view in AllViews())
        {
            view.SetCursorId(id);
        }
    }

    public override void OnCursorPosition(CursorPosition position)
    {
        foreach (RemoteDisplayView view in AllViews())
        {
            view.SetCursorPosition(position.X, position.Y);
        }
    }

    public override void OnDisplaySwitched(int display) => Dispatcher.UIThread.Post(() =>
    {
        // With display windows open this only says where the focus is; the tab keeps its display.
        if (_subscribing)
        {
            return;
        }

        _suppressOptions = true;
        CurrentDisplay = display;
        ShowInTab(display);
        PopulateResolutions();
        RefreshWindowChoices();
        _suppressOptions = false;
    });

    /// <summary>
    /// The host's answer to a subscription: the displays it streams now, by index and name. Windows are matched to
    /// them by name -- indices move when a monitor is pulled, and this answer can arrive before the new list does.
    /// A refusal carries the set the host kept, so a window just opened for a display it would not add closes.
    /// </summary>
    public override void OnDisplaySubscription(DisplaySubscription subscription) => Dispatcher.UIThread.Post(() =>
    {
        if (subscription.Failure.Length > 0)
        {
            Services.Toasts.Current.Show(Strings.Format("screen.failed", subscription.Failure), true);
        }

        List<RemoteScreenViewModel> screens = ScreensSnapshot();
        DisplayWindowPlan.Outcome plan = DisplayWindowPlan.Match([.. screens.Select(s => s.DisplayName)], _tabName, subscription);
        bool gone = false;
        foreach (RemoteScreenViewModel screen in screens)
        {
            if (plan.Keep.TryGetValue(screen.DisplayName, out int index))
            {
                screen.DisplayIndex = index;
            }
            else
            {
                gone |= subscription.Failure.Length == 0;
                RemoveScreen(screen, closeWindow: true);
            }
        }

        if (gone)
        {
            Services.Toasts.Current.Show(Strings.Format("screen.gone", Title), false);
        }

        if (plan.Tab >= 0 && plan.Tab != CurrentDisplay)
        {
            _suppressOptions = true;
            CurrentDisplay = plan.Tab;
            ShowInTab(plan.Tab);
            PopulateResolutions();
            _suppressOptions = false;
        }

        RebuildRouting();
        RefreshWindowChoices();
    });

    /// <summary>
    /// The host's displays changed shape -- at this viewer's request, another viewer's, or not at all
    /// because the request was refused. A refusal is shown and the list snaps back to what the host has.
    /// </summary>
    public override void OnDisplaysChanged(DisplaysChanged info) => Dispatcher.UIThread.Post(() =>
    {
        HostNotice = info.Notice;

        // The same answer comes back for a resolution and for a display added or removed; which one this is, only
        // this side knows.
        bool? virtualRequest = _pendingVirtual;
        _pendingVirtual = null;
        if (info.Failure.Length > 0)
        {
            if (virtualRequest is null && _pendingResolution is null && FollowerWaiting())
            {
                // A follower's own request: it says so on the toolbar, where the window is, not in a toast.
                NotifyFollowers(info.Changed, info.Failure);
                return;
            }

            string failed = virtualRequest switch
            {
                true => "session.addDisplay.failed",
                false => "session.removeDisplay.failed",
                null => "session.resolution.failed",
            };
            Services.Toasts.Current.Show(Strings.Format(failed, info.Failure), true);
            if (virtualRequest is null)
            {
                _pendingResolution = null;
                PopulateResolutions();
            }

            return;
        }

        RememberResolution(info);
        _suppressOptions = true;
        PopulateDisplays(info.Displays, info.CurrentDisplay);
        _suppressOptions = false;
        NotifyFollowers(info.Changed);

        // Asked for so that there is a second screen to look at: it opens where it can be dragged to one.
        if (virtualRequest == true && info.Changed >= 0 && info.Changed != CurrentDisplay)
        {
            OpenDisplayWindow(info.Changed);
        }
    });

    public override void OnPermission(PermissionInfo info)
    {
        base.OnPermission(info);
        if (info.Permission != Permission.PermKeyboard)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            _keyboardAllowed = info.Enabled;
            PopulateResolutions();
        });
    }

    public override async ValueTask DisposeAsync()
    {
        // The tab is going, and its display windows are views of it: they go first.
        foreach (RemoteScreenViewModel screen in ScreensSnapshot())
        {
            RemoveScreen(screen, closeWindow: true);
        }

        _recordingTimer?.Stop();
        if (_recording is not null)
        {
            // A file that is not finalised cannot be played, so finish it whatever ended the session.
            await _recording.DisposeAsync();
        }

        await base.DisposeAsync();
        _follower.Dispose();
        if (_audio is not null)
        {
            await _audio.DisposeAsync();
        }
    }
}
