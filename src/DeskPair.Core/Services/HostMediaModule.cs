using Microsoft.Extensions.Logging;
using DeskPair.Core.Clipboard;
using DeskPair.Core.Qos;
using DeskPair.Core.Session;
using DeskPair.Core.Session.Host;
using DeskPair.Core.Session.Host.Handlers;
using DeskPair.Platform.Abstractions.Audio;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Abstractions.Clipboard;
using DeskPair.Platform.Abstractions.Codec;
using DeskPair.Platform.Abstractions.Cursor;
using DeskPair.Platform.Abstractions.Input;
using DeskPair.Protocol.Messages;
using DeskPair.Protocol.Rendezvous;

namespace DeskPair.Core.Services;

/// <summary>The platform pieces a host needs to stream and be controlled.</summary>
public sealed record HostPlatform(
    IDisplayEnumerator Displays,
    IScreenCapturerFactory Capturers,
    IVideoEncoderFactory Encoders,
    IInputInjector Input,
    ICursorProvider Cursor,
    Func<string?, IAudioCapture>? AudioCapture = null,
    IClipboard? Clipboard = null,
    IDisplayModeSwitcher? DisplayModes = null,
    IVirtualDisplayProvider? VirtualDisplays = null,
    IArbitraryModeSink? ModeTeacher = null,
    IDisplaySession? DisplaySession = null,
    ISessionScreen? SessionScreen = null);

/// <summary>
/// Wires the publisher services into a <see cref="HostRuntime"/>: subscribes remote-control sessions to
/// video/cursor/audio according to their permissions, and handles display switching.
/// </summary>
public sealed class HostMediaModule : IAsyncDisposable
{
    private static readonly TimeSpan EchoWindow = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// How long after the last <see cref="IDisplayEnumerator.DisplaysChanged"/> before acting on it. Windows
    /// sends one message per monitor a change touched, and a monitor being plugged in flickers through a
    /// few configurations before it settles; acting on the first would restart every stream several times.
    /// </summary>
    public static readonly TimeSpan DisplayChangeSettle = TimeSpan.FromMilliseconds(500);

    private readonly HostPlatform _platform;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _logs;
    private readonly ILogger _log;
    private readonly Dictionary<int, VideoService> _video = new();
    private readonly Dictionary<int, Subscription> _subscriptions = new();

    /// <summary>
    /// Authorized remote-control sessions, whatever they are watching. The last one leaving is what puts the
    /// screen back the way its owner left it -- counted here rather than from the subscriptions, because a
    /// viewer watching no display (only transferring files, or chatting) is still at the desk.
    /// </summary>
    private readonly HashSet<int> _remoteSessions = new();

    /// <summary>
    /// The host's own word on why its displays are what they are (waiting for consent, sharing refused or stopped),
    /// sent with every list until something replaces it; empty when there is nothing to say.
    /// </summary>
    private string _notice = string.Empty;

    /// <summary>The attempt to open displays that exist only while somebody watches, while one is running.</summary>
    private Task? _opening;
    private readonly object _lock = new();
    private readonly InputHandler _input;
    private readonly DisplayModeService _modes;
    private readonly DisplayChangeQueue _changes;
    private HostRuntime? _runtime;
    private ITimer? _displayChangeTimer;
    private string _topology = string.Empty;
    private IReadOnlyList<DisplayDescriptor> _topologyDisplays = [];
    private readonly CursorService _cursor;
    private readonly AudioService? _audio;
    private readonly ClipboardService? _clipboard;

    public HostMediaModule(HostPlatform platform, ILoggerFactory logs, TimeProvider? time = null)
    {
        _platform = platform;
        _logs = logs;
        _log = logs.CreateLogger<HostMediaModule>();
        _time = time ?? TimeProvider.System;
        Qos = new VideoQosController(_time);
        _input = new InputHandler(platform.Input, platform.Displays, _time);
        _modes = new DisplayModeService(platform.Displays, platform.DisplayModes, _time, logs.CreateLogger<DisplayModeService>(), platform.ModeTeacher);
        _changes = new DisplayChangeQueue(_time, logs.CreateLogger<DisplayChangeQueue>());
        _cursor = new CursorService(platform.Cursor, _time, logs.CreateLogger<CursorService>())
        {
            IsEchoFor = id => _input.InjectedRecently(id, EchoWindow),
        };
        if (platform.AudioCapture is not null)
        {
            _audio = new AudioService(() => platform.AudioCapture(AudioCaptureDeviceId), _time, logs.CreateLogger<AudioService>());
        }

        var handlers = new List<ISessionHandler<HostSessionContext>> { _input, new VideoControlHandler(this) };
        if (platform.Clipboard is not null)
        {
            _clipboard = new ClipboardService(platform.Clipboard, _time, logs.CreateLogger<ClipboardService>());
            handlers.Add(new ClipboardHandler(_clipboard));
        }

        Handlers = handlers;
    }

    public VideoQosController Qos { get; }

    /// <summary>Two displays: a viewer's own two screens, which is the case this exists for.</summary>
    public const int DefaultMaxDisplaysPerViewer = 2;

    /// <summary>Four encoders at once is what a consumer GPU's hardware encoder sessions comfortably allow.</summary>
    public const int DefaultMaxConcurrentStreams = 4;

    /// <summary>At most this many displays streamed to one viewer at once.</summary>
    public int MaxDisplaysPerViewer { get; set; } = DefaultMaxDisplaysPerViewer;

    /// <summary>At most this many streams (encoders) at once, over every viewer.</summary>
    public int MaxConcurrentStreams { get; set; } = DefaultMaxConcurrentStreams;

    /// <summary>
    /// A viewer may plug in displays that do not exist (and a host with no screen gets one of its own at the
    /// start of a session). Off unless the owner allowed it: it installs nothing, but it does change the desk.
    /// </summary>
    public bool AllowVirtualDisplays { get; set; }

    /// <summary>At most this many displays plugged in from here at once.</summary>
    public const int MaxVirtualDisplays = 4;

    /// <summary>
    /// A viewer following its window ("Match window") may have a private session screen: a display of the window's size,
    /// with every other display off, for as long as a viewer follows (<see cref="ISessionScreen"/>). Off unless the owner
    /// allowed it: the person at the computer sees their screens go dark.
    /// </summary>
    public bool AllowSessionScreen { get; set; }

    /// <summary>How long after the session screen opened a display coming back is still its own doing, not somebody's at the computer.</summary>
    private static readonly TimeSpan SessionScreenSettle = TimeSpan.FromSeconds(2);

    /// <summary>The queue's key for opening and closing it: one decision at a time, the latest standing.</summary>
    private const string SessionScreenKey = "\0session-screen";

    /// <summary>The options of each viewer that follows its window, by connection.</summary>
    private readonly Dictionary<int, SessionOptions> _following = [];

    private long _sessionScreenSince;

    /// <summary>Somebody at the computer turned the displays back on: no session screen again until nobody follows.</summary>
    private bool _sessionScreenTakenBack;

    /// <summary>How long a display that was added (or removed) has to show up in (or leave) the list.</summary>
    private static readonly TimeSpan VirtualDisplaySettle = TimeSpan.FromSeconds(5);

    /// <summary>How many of a viewer's other sizes an added display is asked for with: its screens, a few times over.</summary>
    private const int MaxRequestedSizes = 32;

    /// <summary>Codecs no encoder of this machine would start for (<see cref="NextCodec"/>), as <c>SupportedCodecs</c> bits.</summary>
    private int _unstartable;

    /// <summary>One change of topology is followed at a time: the platform's own notice and a viewer's request can arrive together.</summary>
    private readonly SemaphoreSlim _topologyGate = new(1, 1);

    /// <summary>Pass to <see cref="HostRuntime"/>'s extra handlers.</summary>
    public IReadOnlyList<ISessionHandler<HostSessionContext>> Handlers { get; }

    /// <summary>
    /// The codec the host would rather use, from configuration. It is a preference, not an instruction: a
    /// viewer that cannot decode it gets something it can, because a stream the viewer cannot read is worth
    /// less than a stream in a codec nobody chose. Null means no preference at all ("auto").
    /// </summary>
    public Platform.Abstractions.Codec.VideoCodec? Codec { get; init; }

    /// <summary>Sound device the desk captures from; empty follows the system default. Applies to the next session.</summary>
    public string? AudioCaptureDeviceId { get; set; }

    /// <summary>
    /// Lets a file list a viewer copied become a real promise on this machine's clipboard, fetched through
    /// that viewer's own transfer engine when someone pastes. Without this call a file list is dropped.
    /// </summary>
    public void EnableFilePromises(HostFileModule files)
    {
        if (_clipboard is null)
        {
            return;
        }

        var staging = new ClipboardStaging(_logs.CreateLogger<ClipboardStaging>());
        staging.SweepStale();
        _clipboard.PromiseRouter = new ClipboardPromiseRouter(staging, _logs.CreateLogger<ClipboardPromiseRouter>());
        _clipboard.FileEngineFor = files.GetEngine;
        _clipboard.Offer = new LocalFileOffer(_logs.CreateLogger<LocalFileOffer>());
    }

    /// <summary>The display modes a viewer may ask for, and what "original" means; owned here so restore follows the sessions.</summary>
    public DisplayModeService DisplayModes => _modes;

    public void Attach(HostRuntime runtime)
    {
        _runtime = runtime;
        runtime.SupportsMultiDisplay = true;
        _topologyDisplays = _platform.Displays.GetDisplays();
        _topology = Signature(_topologyDisplays);
        _platform.Displays.DisplaysChanged += OnDisplaysChanged;
        if (_platform.DisplaySession is { } shared)
        {
            shared.Closed += OnDisplaySessionClosed;
            shared.Reopenable += OnDisplaySessionReopenable;
        }

        runtime.DisplayProvider = DescribeDisplays;
        runtime.EncodingProvider = () => CodecWire.ToEncoding(_platform.Encoders.Probe());
        runtime.SessionAuthorized += OnSessionAuthorizedAsync;
        runtime.SessionClosing += OnSessionClosingAsync;
        runtime.RoundTripReported = (id, rtt) => Qos.ReportDelay(id, rtt);
        runtime.MediaFeedbackReported = (id, display, seq, givenUp, loss) =>
        {
            Qos.ReportGivenUp(id, display, givenUp, seq);
            Qos.FrameAcked(id, display, seq);
            if (loss is { } linkLoss)
            {
                Qos.ReportLoss(id, linkLoss);
            }
        };
        runtime.BandwidthEstimated = (id, bps) => Qos.ReportBandwidth(id, bps);
        runtime.MediaBitrateCeilingBps = id => Qos.BitrateCeilingKbpsFor(id) * 1000.0;

        // Quality, frame rate, bitrate and refinement chosen mid-session only reached the permission set before,
        // so the encoder ignored them until the next connection.
        runtime.OptionsUpdated = (id, options) =>
        {
            Qos.UpdateOptions(id, options);
            bool remote;
            lock (_lock)
            {
                if (options.FollowWindow == BoolOption.BoYes)
                {
                    _following[id] = options.Clone();
                }
                else
                {
                    _following.Remove(id);
                }

                remote = _remoteSessions.Contains(id);
            }

            if (remote)
            {
                ReconsiderSessionScreen();
            }

            // A viewer that is not drawing the remote pointer has no use for its position; it still needs the
            // shape, because its own pointer wears it. BO_NOT_SET means a viewer that never said, so it gets both.
            _cursor.SetWantsPosition(id, options.ShowRemoteCursor != Protocol.Messages.BoolOption.BoNo);
        };
        runtime.MediaChannelClosed = id =>
        {
            // Frames that were in flight on UDP are gone; start the TCP accounting clean and with a keyframe.
            List<int> displays;
            lock (_lock)
            {
                displays = _video.Keys.ToList();
            }

            foreach (int display in displays)
            {
                Qos.ResetStream(id, display);
                Qos.ReportLoss(id, 0);
                Qos.ReportBandwidth(id, null);
                GetVideoService(display)?.RequestUrgentKeyFrame(id, "UDP media channel closed");
            }
        };
    }

    public VideoService? GetVideoService(int display)
    {
        lock (_lock)
        {
            return _video.GetValueOrDefault(display);
        }
    }

    public void RequestKeyFrame(int connectionId, int display, string reason = "requested")
    {
        GetVideoService(display)?.RequestKeyFrame(connectionId, reason);
    }

    /// <summary>
    /// What one viewer watches: the displays streamed to it and the one with its attention. A viewer that only
    /// ever switches has a set of one; <see cref="SpeaksSubscription"/> says whether it has asked for a set.
    /// </summary>
    private sealed class Subscription
    {
        public HashSet<int> Displays { get; } = [];

        public int Focus { get; set; } = -1;

        /// <summary>The viewer has sent a DisplaySubscription, so it is answered with one; others only ever hear SwitchDisplay.</summary>
        public bool SpeaksSubscription { get; set; }

        /// <summary>
        /// The viewer's displays all went at once, taking its set with them; when displays come back it is put on the
        /// primary, unlike a viewer who chose to watch nothing (for file transfer or chat) and is left alone.
        /// </summary>
        public bool LostDisplays { get; set; }
    }

    /// <summary>The displays a viewer is subscribed to, for tests and diagnostics.</summary>
    public IReadOnlyList<int> SubscribedDisplays(int connectionId)
    {
        lock (_lock)
        {
            return _subscriptions.TryGetValue(connectionId, out Subscription? s) ? [.. s.Displays.Order()] : [];
        }
    }

    /// <summary>The old single-display request: a set of one, answered the old way. An index that is not a display is ignored, as it always was.</summary>
    public async Task SwitchDisplayAsync(HostSessionContext context, int display, CancellationToken ct)
    {
        if (display < 0 || display >= _platform.Displays.GetDisplays().Count)
        {
            return;
        }

        await ApplySubscriptionAsync(context, [display], display, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A viewer's whole set of displays and its focus. Refused, with the set it keeps and the reason, when it
    /// names a display that does not exist, asks for more than one viewer may have, or would take the host past
    /// the number of streams it runs at once.
    /// </summary>
    public async Task SubscribeAsync(HostSessionContext context, DisplaySubscription request, CancellationToken ct)
    {
        IReadOnlyList<DisplayDescriptor> displays = _platform.Displays.GetDisplays();
        HashSet<int> wanted = [.. request.Displays];
        string? failure = null;
        if (wanted.FirstOrDefault(d => d < 0 || d >= displays.Count, -1) is var missing and >= 0)
        {
            failure = $"There is no display {missing + 1} on this computer.";
        }
        else if (wanted.Count > MaxDisplaysPerViewer)
        {
            failure = $"This computer streams at most {MaxDisplaysPerViewer} displays to one viewer.";
        }
        else if (StreamsWith(context.ConnectionId, wanted) > MaxConcurrentStreams)
        {
            failure = $"This computer is already streaming as many displays as it will ({MaxConcurrentStreams}).";
        }

        int[] kept;
        int keptFocus;
        lock (_lock)
        {
            Subscription sub = SubscriptionOf(context.ConnectionId);
            sub.SpeaksSubscription = true;
            kept = [.. sub.Displays.Order()];
            keptFocus = sub.Focus;
        }

        if (failure is not null)
        {
            _log.LogInformation("Session {Id}: display subscription {Displays} refused: {Why}", context.ConnectionId, string.Join(",", wanted), failure);
            await SendSubscriptionAsync(context, kept, keptFocus, failure, ct).ConfigureAwait(false);
            return;
        }

        int focus = wanted.Contains(request.Focus) ? request.Focus : wanted.Count > 0 ? wanted.Min() : -1;
        await ApplySubscriptionAsync(context, wanted, focus, ct, answerAlways: true).ConfigureAwait(false);
    }

    /// <summary>How many streams the host would run if this viewer's set became <paramref name="wanted"/>.</summary>
    private int StreamsWith(int connectionId, HashSet<int> wanted)
    {
        lock (_lock)
        {
            var all = new HashSet<int>(wanted);
            foreach ((int id, Subscription s) in _subscriptions)
            {
                if (id != connectionId)
                {
                    all.UnionWith(s.Displays);
                }
            }

            return all.Count;
        }
    }

    private Subscription SubscriptionOf(int connectionId)
    {
        if (!_subscriptions.TryGetValue(connectionId, out Subscription? s))
        {
            s = new Subscription();
            _subscriptions[connectionId] = s;
        }

        return s;
    }

    /// <summary>
    /// Moves a viewer to a new set. Streams it leaves are left before streams it joins are joined, so trading
    /// one display for another never briefly costs two encoders. Every viewer hears the focus as SwitchDisplay,
    /// as it always has; one that asked for a set also hears the set.
    /// </summary>
    private async Task ApplySubscriptionAsync(HostSessionContext context, IReadOnlyCollection<int> wanted, int focus, CancellationToken ct, bool answerAlways = false)
    {
        IReadOnlyList<DisplayDescriptor> displays = _platform.Displays.GetDisplays();
        int id = context.ConnectionId;
        int[] leaving, joining, now;
        bool focusMoved, answer;
        lock (_lock)
        {
            Subscription sub = SubscriptionOf(id);
            leaving = [.. sub.Displays.Where(d => !wanted.Contains(d))];
            joining = [.. wanted.Where(d => !sub.Displays.Contains(d) && d < displays.Count)];
            sub.Displays.ExceptWith(leaving);
            sub.Displays.UnionWith(joining);
            focusMoved = sub.Focus != focus;
            sub.Focus = focus;
            now = [.. sub.Displays.Order()];
            answer = sub.SpeaksSubscription;
        }

        if (leaving.Length == 0 && joining.Length == 0 && !focusMoved && !answerAlways)
        {
            return;
        }

        foreach (int display in leaving)
        {
            if (GetVideoService(display) is { } old)
            {
                await old.UnsubscribeAsync(id).ConfigureAwait(false);
            }

            Qos.ResetStream(id, display);
        }

        if (focus >= 0)
        {
            context.CurrentDisplay = focus;
        }

        Qos.SetSubscription(id, now, focus);
        foreach (int display in joining)
        {
            VideoService video = VideoFor(displays[display], context);
            video.Subscribe(context);
            await RenegotiateAsync(video).ConfigureAwait(false);
        }

        if (answer)
        {
            await SendSubscriptionAsync(context, now, focus, string.Empty, ct).ConfigureAwait(false);
        }

        if (focus >= 0 && (leaving.Length > 0 || joining.Length > 0 || focusMoved))
        {
            await context.SendAsync(new Message { Misc = new Misc { SwitchDisplay = new SwitchDisplay { Display = focus } } }, MessagePriority.Control, ct).ConfigureAwait(false);
        }
    }

    private ValueTask SendSubscriptionAsync(HostSessionContext context, int[] displays, int focus, string failure, CancellationToken ct)
    {
        IReadOnlyList<DisplayDescriptor> now = _platform.Displays.GetDisplays();
        var answer = new DisplaySubscription { Focus = focus, Failure = failure };
        answer.Displays.AddRange(displays);
        answer.Names.AddRange(displays.Select(d => d < now.Count ? now[d].Name : string.Empty));
        return context.SendAsync(new Message { Misc = new Misc { DisplaySubscription = answer } }, MessagePriority.Control, ct);
    }

    private async Task OnSessionAuthorizedAsync(HostSession session, CancellationToken ct)
    {
        HostSessionContext ctx = session.Context;
        if (ctx.ConnType != ConnType.ConnRemote)
        {
            return;
        }

        Qos.AddUser(ctx.ConnectionId, ctx.Options);
        bool follows;
        lock (_lock)
        {
            _remoteSessions.Add(ctx.ConnectionId);
            follows = _following.ContainsKey(ctx.ConnectionId);
        }

        ctx.Permissions.Changed += (p, enabled) => OnPermissionChanged(ctx, p, enabled);
        if (follows)
        {
            ReconsiderSessionScreen();
        }

        if (_platform.DisplaySession is { IsOpen: false } shared && !StartOpening(shared) && _notice.Length > 0)
        {
            // Displays that exist only while somebody watches (a Wayland portal session) are opened for the first
            // viewer, in the background: it may wait for a person at the machine, and the session has to go on
            // meanwhile. One arriving during that wait hears what the others were already told.
            await ctx.SendAsync(new Message { Misc = new Misc { DisplaysChanged = Describe(ctx.CurrentDisplay, -1, string.Empty) } }, MessagePriority.Control, ct).ConfigureAwait(false);
        }

        IReadOnlyList<DisplayDescriptor> displays = _platform.Displays.GetDisplays();
        if (displays.Count == 0 && AllowVirtualDisplays && _platform.VirtualDisplays is { UnavailableReason: null } provider)
        {
            // A host with no screen at all: without one the session has nothing to show, so it gets one --
            // taken away again with everything else when the last viewer leaves.
            (string? failure, _) = await PlugAsync(provider, null, [], ct).ConfigureAwait(false);
            if (failure is null)
            {
                await FollowDisplaysAsync(-1).ConfigureAwait(false);
                displays = _platform.Displays.GetDisplays();
            }
            else
            {
                _log.LogWarning("Session {Id}: this computer has no display and adding one failed: {Why}", ctx.ConnectionId, failure);
            }
        }

        if (displays.Count > 0)
        {
            int primary = displays.ToList().FindIndex(d => d.IsPrimary);
            await SwitchDisplayAsync(ctx, primary < 0 ? 0 : primary, ct).ConfigureAwait(false);
        }

        _cursor.Subscribe(ctx);
        if (_audio is not null && ctx.Permissions.Has(Permission.PermAudio))
        {
            _audio.Subscribe(ctx);
        }

        if (_clipboard is not null && ctx.Permissions.Has(Permission.PermClipboard))
        {
            _clipboard.Subscribe(ctx);
        }
    }

    private void OnPermissionChanged(HostSessionContext ctx, Permission permission, bool enabled)
    {
        if (permission == Permission.PermKeyboard)
        {
            ReconsiderSessionScreen(); // only a viewer who may type turns the owner's screens off
        }

        PublisherService? service = permission switch
        {
            Permission.PermAudio => _audio,
            Permission.PermClipboard => _clipboard,
            _ => null,
        };
        if (service is null)
        {
            return;
        }

        if (enabled)
        {
            service.Subscribe(ctx);
        }
        else
        {
            _ = service.UnsubscribeAsync(ctx.ConnectionId);
        }
    }

    /// <summary>Lock the desk when the viewer asked for it, so an unattended machine is not left signed in.</summary>
    public Action? LockWorkstation { get; init; }

    private async Task OnSessionClosingAsync(HostSession session)
    {
        int id = session.Context.ConnectionId;
        Qos.RemoveUser(id);
        if (session.Context.ConnType == ConnType.ConnRemote && session.Context.Options.LockAfterSessionEnd == BoolOption.BoYes)
        {
            try
            {
                (LockWorkstation ?? _platform.Input.LockWorkstation).Invoke();
            }
            catch (Exception e)
            {
                _log.LogWarning(e, "Could not lock the desk after the session ended");
            }
        }

        int[] watched;
        bool last;
        lock (_lock)
        {
            watched = _subscriptions.Remove(id, out Subscription? sub) ? [.. sub.Displays] : [];
            _remoteSessions.Remove(id);
            _following.Remove(id);
            last = _remoteSessions.Count == 0;
        }

        _changes.Drop(id);
        if (last)
        {
            // A change already under way lands first; one landing after the restore would outlive every viewer.
            await _changes.QuiesceAsync().ConfigureAwait(false);
            _sessionScreenTakenBack = false;
            if (_platform.SessionScreen is { IsOpen: true })
            {
                // The owner's screens come back before anything else is put back: they are what the person there sees.
                await CloseSessionScreenAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        else
        {
            ReconsiderSessionScreen();
        }

        if (last && _modes.HasChanges)
        {
            // The last viewer has gone: the screen goes back to how its owner left it. Counted by sessions,
            // not by subscriptions -- one watching nothing but still connected is still somebody's session.
            await _modes.RestoreAllAsync().ConfigureAwait(false);
        }

        if (last && _platform.VirtualDisplays is { Count: > 0 } added)
        {
            // Nothing this program plugged in outlives the sessions it was plugged in for.
            await added.RemoveAllAsync(CancellationToken.None).ConfigureAwait(false);
            await FollowDisplaysAsync(-1).ConfigureAwait(false);
        }

        if (last && _platform.DisplaySession is { } shared)
        {
            // Nothing is shared for nobody: the "being shared" indicator goes out, and a question still on the
            // machine's screen is taken back.
            await shared.CloseAsync().ConfigureAwait(false);
            _notice = string.Empty;
            await FollowDisplaysAsync(-1).ConfigureAwait(false);
        }

        foreach (int display in watched)
        {
            if (GetVideoService(display) is { } v)
            {
                await v.UnsubscribeAsync(id).ConfigureAwait(false);
            }
        }

        await _cursor.UnsubscribeAsync(id).ConfigureAwait(false);
        if (_audio is not null)
        {
            await _audio.UnsubscribeAsync(id).ConfigureAwait(false);
        }

        if (_clipboard is not null)
        {
            await _clipboard.UnsubscribeAsync(id).ConfigureAwait(false);
        }

        _platform.Input.ReleaseAll();
    }

    /// <summary>
    /// The stream for a display, creating it if this is the first viewer. The joining viewer is part of the
    /// first choice rather than something to renegotiate around afterwards, so an ordinary single-viewer
    /// session settles on its codec before the first frame instead of starting and restarting.
    /// </summary>
    private VideoService VideoFor(DisplayDescriptor display, HostSessionContext joining)
    {
        lock (_lock)
        {
            if (!_video.TryGetValue(display.Index, out VideoService? v))
            {
                v = new VideoService(display, _platform.Capturers, _platform.Encoders, Qos, _time, _logs.CreateLogger<VideoService>())
                {
                    Codec = CodecNegotiation.Choose(Encodable(), [ViewerOf(joining)], Codec),
                    RefreshDisplay = index => _platform.Displays.GetDisplays().FirstOrDefault(d => d.Index == index),
                };
                VideoService created = v;
                v.CodecFailed = failed => NextCodec(created, failed);
                _video[display.Index] = v;
            }

            return v;
        }
    }

    private static CodecNegotiation.Viewer ViewerOf(IServiceSubscriber subscriber) =>
        new(subscriber.DecodableCodecs, subscriber.PreferredCodec);

    /// <summary>The codec every current subscriber of this stream can read, preferring what the host asked for.</summary>
    private Platform.Abstractions.Codec.VideoCodec Choose(VideoService video) => CodecNegotiation.Choose(
        Encodable(),
        [.. video.SubscriberCapabilities()],
        Codec);

    /// <summary>What this machine's encoders say they can do, less the codecs none of them would start for.</summary>
    private Platform.Abstractions.Codec.SupportedCodecs Encodable() =>
        _platform.Encoders.Probe() & ~(Platform.Abstractions.Codec.SupportedCodecs)Volatile.Read(ref _unstartable);

    /// <summary>
    /// No encoder of <paramref name="failed"/> would start for <paramref name="video"/>, though this machine lists one:
    /// the codec is struck off for as long as the host runs, and the stream goes on in the next codec its viewers all
    /// read -- null when none is left, and the viewers are told.
    /// </summary>
    private Platform.Abstractions.Codec.VideoCodec? NextCodec(VideoService video, Platform.Abstractions.Codec.VideoCodec failed)
    {
        int struck = (int)Platform.Abstractions.Codec.SupportedCodecsExtensions.Pair(failed);
        int before;
        do
        {
            before = Volatile.Read(ref _unstartable);
        }
        while (Interlocked.CompareExchange(ref _unstartable, before | struck, before) != before);

        List<CodecNegotiation.Viewer> viewers = [.. video.SubscriberCapabilities()];
        Platform.Abstractions.Codec.VideoCodec next = CodecNegotiation.Choose(Encodable(), viewers, Codec);
        bool usable = next != failed && Encodable().Supports(next) && viewers.All(v => v.CanDecode(next));
        _logs.CreateLogger<HostMediaModule>().LogWarning(
            "Display {Display}: no {Codec} encoder on this computer would start; {Next}", video.Display.Index, failed, usable ? $"going on in {next}" : "no other codec suits every viewer");
        return usable ? next : null;
    }

    /// <summary>
    /// A stream has one encoder and every subscriber reads it, so a viewer joining a running stream can
    /// invalidate the choice made for the viewers already on it. Restarting is the only way to change the
    /// codec; it costs a keyframe, and only happens when the alternative is a viewer that sees nothing.
    /// Nothing renegotiates upwards when that viewer leaves: the choice only ever narrows, which avoids
    /// restarting the stream every time someone disconnects for a codec nobody asked to go back to.
    /// </summary>
    private async Task RenegotiateAsync(VideoService video)
    {
        Platform.Abstractions.Codec.VideoCodec wanted = Choose(video);
        if (wanted == video.Codec)
        {
            return;
        }

        _logs.CreateLogger<HostMediaModule>().LogInformation(
            "Display {Display}: switching from {From} to {To} for the viewers now attached", video.Display.Index, video.Codec, wanted);
        video.Codec = wanted;
        await video.RestartForCodecChangeAsync().ConfigureAwait(false);
    }

    // ---- the private session screen ----

    /// <summary>Opens or closes the session screen as the viewers now want it, in turn with every other change of displays.</summary>
    private void ReconsiderSessionScreen()
    {
        if (_platform.SessionScreen is not { } screen)
        {
            return;
        }

        // Nothing to decide while nobody follows and it is shut: every change of options or permissions would otherwise
        // put a step in the queue, and the pause after it holds up whatever comes next -- a viewer leaving, say.
        bool anyone;
        lock (_lock)
        {
            anyone = _following.Count > 0;
        }

        if (anyone || screen.IsOpen)
        {
            _changes.Enqueue(-1, SessionScreenKey, UpdateSessionScreenAsync);
        }
    }

    private async Task UpdateSessionScreenAsync(CancellationToken ct)
    {
        if (_platform.SessionScreen is not { } screen)
        {
            return;
        }

        SessionOptions? asker = WhoWantsTheSessionScreen();
        if (asker is null)
        {
            _sessionScreenTakenBack = false;
            if (screen.IsOpen)
            {
                await CloseSessionScreenAsync(ct).ConfigureAwait(false);
            }

            return;
        }

        if (screen.IsOpen || _sessionScreenTakenBack || !AllowSessionScreen)
        {
            return;
        }

        if (screen.UnavailableReason is { } why)
        {
            _log.LogInformation("No private session screen: {Why}", why);
            return;
        }

        var size = new DisplayMode(asker.Viewport.Width, asker.Viewport.Height);
        List<DisplayMode> sizes = [.. asker.Screens.Where(s => s is { Width: > 0, Height: > 0 }).Take(MaxRequestedSizes).Select(s => new DisplayMode(s.Width, s.Height))];
        DisplayActionResult opened = await screen.OpenAsync(size, sizes, ct).ConfigureAwait(false);
        if (!opened.Succeeded)
        {
            _log.LogWarning("No private session screen: {Why}", opened.Failure);
            return;
        }

        Volatile.Write(ref _sessionScreenSince, _time.GetTimestamp());
        int index = _platform.Displays.GetDisplays().ToList().FindIndex(screen.IsSessionScreen);
        await FollowDisplaysAsync(index).ConfigureAwait(false);
    }

    /// <summary>
    /// The options of a viewer the session screen is for: authorized, following its window, allowed to type -- only such a
    /// viewer turns the owner's screens off -- and able to say how large its window is. Null when there is none.
    /// </summary>
    private SessionOptions? WhoWantsTheSessionScreen()
    {
        List<(int Id, SessionOptions Options)> following;
        lock (_lock)
        {
            following = [.. _following.Where(f => _remoteSessions.Contains(f.Key)).Select(f => (f.Key, f.Value))];
        }

        foreach ((int id, SessionOptions options) in following)
        {
            if (options.Viewport is { Width: > 0, Height: > 0 }
                && _runtime?.Sessions.FirstOrDefault(s => s.Context.ConnectionId == id)?.Context.Permissions.Has(Permission.PermKeyboard) == true)
            {
                return options;
            }
        }

        return null;
    }

    private async Task CloseSessionScreenAsync(CancellationToken ct)
    {
        if (_platform.SessionScreen is not { IsOpen: true } screen)
        {
            return;
        }

        string? name = _platform.Displays.GetDisplays().FirstOrDefault(screen.IsSessionScreen).Name;
        await screen.CloseAsync(ct).ConfigureAwait(false);
        if (name is not null)
        {
            _modes.ForgetDisplay(name);
        }

        await FollowDisplaysAsync(-1).ConfigureAwait(false);
    }

    private IEnumerable<DisplayInfo> DescribeDisplays() =>
        _platform.Displays.GetDisplays().Select(d =>
        {
            var info = new DisplayInfo
            {
                X = d.X,
                Y = d.Y,
                Width = d.Width,
                Height = d.Height,
                Name = d.Name,
                Online = true,
                Primary = d.IsPrimary,
                Scale = d.Scale,
                VirtualDisplay = _platform.VirtualDisplays?.IsVirtual(d) == true,
                SessionScreen = _platform.SessionScreen?.IsSessionScreen(d) == true,
            };
            info.Modes.AddRange(_modes.ModesFor(d).Select(m => new Resolution { Width = m.Width, Height = m.Height, Scale = m.Scale }));
            if (_modes.MadeUpSizesFor(d) is { } sizes)
            {
                info.AnySize = new SizeRange
                {
                    MinWidth = sizes.MinWidth,
                    MinHeight = sizes.MinHeight,
                    MaxWidth = sizes.MaxWidth,
                    MaxHeight = sizes.MaxHeight,
                    Step = sizes.Step,
                };
            }

            if (_modes.OriginalFor(d.Name) is { } original)
            {
                info.Original = new Resolution { Width = original.Width, Height = original.Height, Scale = original.Scale };
            }

            return info;
        });

    /// <summary>
    /// A viewer asked for a display mode. Refused without keyboard permission -- somebody who may only watch
    /// must not reshape the screen -- and for anything the display cannot be given. The change is made by
    /// <see cref="DisplayChangeQueue"/>, so the session goes on reading this viewer's input meanwhile, and a
    /// later request for the same display replaces this one if it has not started.
    /// </summary>
    public async Task SetResolutionAsync(HostSessionContext context, DisplayResolution request, CancellationToken ct)
    {
        IReadOnlyList<DisplayDescriptor> displays = _platform.Displays.GetDisplays();
        string? failure = !context.Permissions.Has(Permission.PermKeyboard) ? "Changing the resolution needs keyboard and mouse permission."
            : request.Display < 0 || request.Display >= displays.Count ? "That display is not there any more."
            : null;
        if (failure is not null)
        {
            await context.SendAsync(new Message { Misc = new Misc { DisplaysChanged = Describe(context.CurrentDisplay, request.Display, failure) } }, MessagePriority.Control, ct).ConfigureAwait(false);
            return;
        }

        // By name: indices move when a monitor comes or goes, and the change may wait behind another.
        string name = displays[request.Display].Name;
        DisplayMode? wanted = request.Resolution is { } r && (r.Width > 0 || r.Height > 0)
            ? new DisplayMode(r.Width, r.Height, r.Scale)
            : null;
        _changes.Enqueue(context.ConnectionId, name, token => ChangeResolutionAsync(context, name, wanted, token));
    }

    /// <summary>
    /// Makes one queued resolution change. On a change, the streams of the displays whose geometry moved restart --
    /// a Windows mode change can move its neighbours too, the rest are left running -- and every remote viewer is
    /// told, so each picker keeps telling the truth. A refusal, or a request for what the display already is,
    /// restarts nothing and is answered to the asker alone.
    /// </summary>
    private async Task ChangeResolutionAsync(HostSessionContext context, string name, DisplayMode? wanted, CancellationToken ct)
    {
        if (!IsRemoteSession(context.ConnectionId))
        {
            return; // gone while the change waited: nobody left to change it for
        }

        string? failure;
        int index;
        bool broadcast = false;
        await _topologyGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            index = _platform.Displays.GetDisplays().ToList().FindIndex(d => d.Name == name);
            failure = index < 0 ? "That display is not there any more."
                : !context.Permissions.Has(Permission.PermKeyboard) ? "Changing the resolution needs keyboard and mouse permission."
                : await _modes.ChangeAsync(index, wanted, ct).ConfigureAwait(false);

            IReadOnlyList<DisplayDescriptor> displays = _platform.Displays.GetDisplays();
            string now = Signature(displays);
            if (failure is null && now != _topology)
            {
                // Handled here, under the gate: the platform's own notice of this change, still to arrive, finds
                // nothing new and restarts nothing again.
                IReadOnlyList<DisplayDescriptor> built = _topologyDisplays;
                _topology = now;
                _topologyDisplays = displays;
                if (built.Select(d => d.Name).SequenceEqual(displays.Select(d => d.Name), StringComparer.Ordinal))
                {
                    await RestartStreamsForNewGeometryAsync(built, displays).ConfigureAwait(false);
                    await BroadcastDisplaysAsync(index, CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    // Displays came or went meanwhile: subscriptions move by name, as for any change of topology.
                    await ApplyDisplaysAsync(built, displays, index).ConfigureAwait(false);
                }

                broadcast = true;
            }
        }
        finally
        {
            _topologyGate.Release();
        }

        if (!broadcast)
        {
            try
            {
                await context.SendAsync(new Message { Misc = new Misc { DisplaysChanged = Describe(context.CurrentDisplay, index, failure ?? string.Empty) } }, MessagePriority.Control, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // The asker left meanwhile.
            }
        }
    }

    private bool IsRemoteSession(int connectionId)
    {
        lock (_lock)
        {
            return _remoteSessions.Contains(connectionId);
        }
    }

    /// <summary>
    /// The platform says the displays changed: a monitor came or went, or somebody at the desk (or another
    /// program) changed a mode. Nothing is done here but to arm the settle timer; the work happens once the
    /// notices stop coming.
    /// </summary>
    private void OnDisplaysChanged(object? sender, EventArgs e)
    {
        lock (_lock)
        {
            _displayChangeTimer ??= _time.CreateTimer(_ => _ = SettleDisplayChangeAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _displayChangeTimer.Change(DisplayChangeSettle, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Re-enumerates and compares with what the streams were built for. The platform's notice says nothing
    /// about what changed, and it also arrives for changes this module made itself, so the comparison is
    /// what tells a real change from an echo. On a real one: viewers whose display is gone move to the
    /// primary, streams for displays that no longer exist are dropped, the rest restart at their new
    /// geometry, and every viewer hears the new list.
    /// </summary>
    private async Task SettleDisplayChangeAsync()
    {
        try
        {
            await FollowDisplaysAsync(-1).ConfigureAwait(false);
            if (_platform.SessionScreen is { IsOpen: true } screen && _time.GetElapsedTime(Volatile.Read(ref _sessionScreenSince)) > SessionScreenSettle
                && !screen.IsAlone())
            {
                // Somebody at the computer turned the other displays back on (Win+P): theirs to do, and the end of it.
                _log.LogInformation("The displays were turned back on at the computer; closing the private session screen");
                _sessionScreenTakenBack = true;
                _changes.Enqueue(-1, SessionScreenKey, CloseSessionScreenAsync);
            }
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Could not follow a display change");
        }
    }

    /// <summary>Acts on the displays as they are now, if they differ from what the streams were built for; <paramref name="changed"/> is what the broadcast names.</summary>
    private async Task FollowDisplaysAsync(int changed)
    {
        await _topologyGate.WaitAsync().ConfigureAwait(false);
        try
        {
            IReadOnlyList<DisplayDescriptor> displays = _platform.Displays.GetDisplays();
            string now = Signature(displays);
            if (now == _topology)
            {
                return;
            }

            IReadOnlyList<DisplayDescriptor> before = _topologyDisplays;
            _topology = now;
            _topologyDisplays = displays;
            _log.LogInformation("Displays changed: {Displays}", displays.Count == 0 ? "none" : now);
            await ApplyDisplaysAsync(before, displays, changed).ConfigureAwait(false);
        }
        finally
        {
            _topologyGate.Release();
        }
    }

    // ---- displays that exist only while somebody watches ----

    /// <summary>Starts opening the shared displays; false when an attempt was already under way, which this joins.</summary>
    private bool StartOpening(IDisplaySession shared)
    {
        lock (_lock)
        {
            if (_opening is { IsCompleted: false })
            {
                return false;
            }

            // A new attempt: whatever the last one ended with (a refusal, say) is no longer the news.
            _notice = string.Empty;
            _opening = Task.Run(() => OpenDisplaySessionAsync(shared));
            return true;
        }
    }

    /// <summary>
    /// Opens the shared displays and follows them: every viewer hears the new list, and everyone who arrived to no
    /// display watches the primary. A failure is said to every viewer, since they are all looking at nothing.
    /// </summary>
    private async Task OpenDisplaySessionAsync(IDisplaySession shared)
    {
        string? why;
        try
        {
            why = await shared.OpenAsync(notice => _ = AnnounceAsync(notice), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Opening the shared displays failed");
            why = "The remote computer's screen could not be shared: " + e.Message;
        }

        bool anyone;
        lock (_lock)
        {
            anyone = _remoteSessions.Count > 0;
        }

        if (!anyone)
        {
            // Everybody left while it opened; the last one out already closed what there was to close.
            await shared.CloseAsync().ConfigureAwait(false);
            return;
        }

        _notice = why ?? string.Empty;
        await FollowDisplaysAsync(-1).ConfigureAwait(false);
        if (why is not null)
        {
            _log.LogInformation("The shared displays did not open: {Why}", why);
            await BroadcastDisplaysAsync(-1, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        IReadOnlyList<DisplayDescriptor> displays = _platform.Displays.GetDisplays();
        if (displays.Count == 0 || _runtime is null)
        {
            return;
        }

        int primary = Math.Max(0, displays.ToList().FindIndex(d => d.IsPrimary));
        foreach (HostSession session in _runtime.Sessions)
        {
            HostSessionContext ctx = session.Context;
            bool watching;
            lock (_lock)
            {
                watching = !_remoteSessions.Contains(ctx.ConnectionId) ||
                    (_subscriptions.TryGetValue(ctx.ConnectionId, out Subscription? sub) && sub.Displays.Count > 0);
            }

            if (!watching)
            {
                try
                {
                    await SwitchDisplayAsync(ctx, primary, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
                {
                    // A session on its way out.
                }
            }
        }
    }

    private async Task AnnounceAsync(string notice)
    {
        _notice = notice;
        try
        {
            await BroadcastDisplaysAsync(-1, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _log.LogDebug(e, "Could not tell the viewers: {Notice}", notice);
        }
    }

    /// <summary>The shared displays went away by themselves (the person stopped sharing): everyone is told why.</summary>
    private void OnDisplaySessionClosed(string reason) => _ = FollowClosedAsync(reason);

    /// <summary>
    /// What closed the shared displays by themselves is over (the screen was unlocked): they are opened again for the
    /// viewers still connected, who were told why the picture went and would otherwise wait for it for ever.
    /// </summary>
    private void OnDisplaySessionReopenable()
    {
        bool anyone;
        lock (_lock)
        {
            anyone = _remoteSessions.Count > 0;
        }

        if (anyone && _platform.DisplaySession is { IsOpen: false } shared)
        {
            _log.LogInformation("The shared displays can be opened again; opening them for the viewers still connected");
            StartOpening(shared);
        }
    }

    private async Task FollowClosedAsync(string reason)
    {
        _log.LogInformation("The shared displays closed: {Reason}", reason);
        _notice = reason;
        try
        {
            await FollowDisplaysAsync(-1).ConfigureAwait(false);
            await BroadcastDisplaysAsync(-1, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Could not follow the shared displays closing");
        }
    }

    // ---- displays that do not exist ----

    /// <summary>
    /// A viewer asked to plug in a display, or to unplug one that was plugged in that way. The answer is the
    /// one a resolution change gets: the new list to everyone, or the reason to the asker alone.
    /// </summary>
    public Task VirtualDisplayAsync(HostSessionContext context, VirtualDisplayRequest request, CancellationToken ct)
    {
        // Queued like a resolution change, and for the same reason: plugging a display in takes seconds.
        string? removing = null;
        if (request.Action == VirtualDisplayRequest.Types.Action.VdRemove)
        {
            IReadOnlyList<DisplayDescriptor> displays = _platform.Displays.GetDisplays();
            removing = request.Display >= 0 && request.Display < displays.Count ? displays[request.Display].Name : string.Empty;
        }

        _changes.Enqueue(context.ConnectionId, null, token => ChangeVirtualDisplaysAsync(context, request, removing, token));
        return Task.CompletedTask;
    }

    private async Task ChangeVirtualDisplaysAsync(HostSessionContext context, VirtualDisplayRequest request, string? removing, CancellationToken ct)
    {
        if (!IsRemoteSession(context.ConnectionId))
        {
            return;
        }

        (string? failure, int changed) = removing is not null
            ? await UnplugAsync(context, removing, ct).ConfigureAwait(false)
            : await AddForAsync(context, request, ct).ConfigureAwait(false);
        if (failure is not null)
        {
            _log.LogInformation("Session {Id}: virtual display {Action} refused: {Why}", context.ConnectionId, request.Action, failure);
            try
            {
                await context.SendAsync(new Message { Misc = new Misc { DisplaysChanged = Describe(context.CurrentDisplay, -1, failure) } }, MessagePriority.Control, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // The asker left meanwhile.
            }

            return;
        }

        await FollowDisplaysAsync(changed).ConfigureAwait(false);
    }

    private string? WhyNoVirtualDisplays(HostSessionContext context)
    {
        if (_platform.VirtualDisplays is not { } provider)
        {
            return "This computer cannot add displays.";
        }

        if (provider.UnavailableReason is { } why)
        {
            return why;
        }

        if (!AllowVirtualDisplays)
        {
            return "The owner of this computer has not allowed adding displays from here.";
        }

        return context.Permissions.Has(Permission.PermKeyboard) ? null : "Adding or removing a display needs keyboard and mouse permission.";
    }

    private async Task<(string? Failure, int Changed)> AddForAsync(HostSessionContext context, VirtualDisplayRequest request, CancellationToken ct)
    {
        if (WhyNoVirtualDisplays(context) is { } why)
        {
            return (why, -1);
        }

        IVirtualDisplayProvider provider = _platform.VirtualDisplays!;
        if (provider.Count >= MaxVirtualDisplays)
        {
            return ($"This computer already has as many added displays as it will ({MaxVirtualDisplays}).", -1);
        }

        if (_platform.SessionScreen is { IsOpen: true })
        {
            // A display more is a set of displays Windows has no private arrangement for: it would light the owner's up.
            return ("This computer is showing a private screen; turn Match window off to add a display.", -1);
        }

        DisplayMode? mode = request.Resolution is { Width: > 0, Height: > 0 } r ? new DisplayMode(r.Width, r.Height, r.Scale) : null;
        List<DisplayMode> sizes = [.. request.Sizes.Where(s => s is { Width: > 0, Height: > 0 }).Take(MaxRequestedSizes).Select(s => new DisplayMode(s.Width, s.Height, s.Scale))];
        return await PlugAsync(provider, mode, sizes, ct).ConfigureAwait(false);
    }

    /// <summary>Adds a display and waits for the list to show it: it is there when the enumerator says so, not when the driver said yes.</summary>
    private async Task<(string? Failure, int Changed)> PlugAsync(IVirtualDisplayProvider provider, DisplayMode? mode, IReadOnlyList<DisplayMode> sizes, CancellationToken ct)
    {
        HashSet<string> before = [.. _platform.Displays.GetDisplays().Select(d => d.Name)];
        DisplayActionResult added = await provider.AddAsync(mode, sizes, ct).ConfigureAwait(false);
        if (!added.Succeeded)
        {
            return (added.Failure ?? "The display could not be added.", -1);
        }

        int index = await WaitForDisplaysAsync(list => list.FindIndex(d => !before.Contains(d.Name)), ct).ConfigureAwait(false);
        if (index < 0)
        {
            return ("The display was added but has not appeared.", -1);
        }

        // Asked for at a size is not plugged in at it: Windows brings a monitor back at whatever size it last had.
        // Set like any resolution request, so it is taught first if need be; the display is there either way.
        if (mode is { } wanted && _platform.Displays.GetDisplays() is var displays && index < displays.Count && !wanted.Matches(displays[index])
            && await _modes.ChangeAsync(index, wanted, ct).ConfigureAwait(false) is { } refused)
        {
            _log.LogInformation("Added display {Index} stays at {W}x{H}: {Why}", index, displays[index].Width, displays[index].Height, refused);
        }

        return (null, index);
    }

    private async Task<(string? Failure, int Changed)> UnplugAsync(HostSessionContext context, string name, CancellationToken ct)
    {
        if (WhyNoVirtualDisplays(context) is { } why)
        {
            return (why, -1);
        }

        IVirtualDisplayProvider provider = _platform.VirtualDisplays!;
        IReadOnlyList<DisplayDescriptor> displays = _platform.Displays.GetDisplays();
        int index = displays.ToList().FindIndex(d => d.Name == name);
        if (index < 0 || !provider.IsVirtual(displays[index]))
        {
            return ("Only a display that was added from here can be removed.", -1);
        }

        DisplayActionResult removed = await provider.RemoveAsync(displays[index], ct).ConfigureAwait(false);
        if (!removed.Succeeded)
        {
            return (removed.Failure ?? "The display could not be removed.", -1);
        }

        _modes.ForgetDisplay(name);
        int gone = await WaitForDisplaysAsync(list => list.Exists(d => d.Name == name) ? -1 : 0, ct).ConfigureAwait(false);
        return gone == 0 ? (null, -1) : ("The display was removed but is still listed.", -1);
    }

    /// <summary>Polls the list until <paramref name="probe"/> finds what it is waiting for (a non-negative answer), or gives up.</summary>
    private async Task<int> WaitForDisplaysAsync(Func<List<DisplayDescriptor>, int> probe, CancellationToken ct)
    {
        long start = _time.GetTimestamp();
        while (true)
        {
            int found = probe([.. _platform.Displays.GetDisplays()]);
            if (found >= 0 || _time.GetElapsedTime(start) > VirtualDisplaySettle)
            {
                return found;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), _time, ct).ConfigureAwait(false);
        }
    }

    private async Task ApplyDisplaysAsync(IReadOnlyList<DisplayDescriptor> before, IReadOnlyList<DisplayDescriptor> displays, int changed)
    {
        int primary = Math.Max(0, displays.ToList().FindIndex(d => d.IsPrimary));

        // When a monitor goes, every index after it moves down one, and a subscription held by index would
        // silently start showing the neighbouring display. So each viewer's set is carried over by name --
        // names are unique on every platform (a device path, an output name, a display id) -- and whatever
        // is gone is dropped. A viewer left with nothing, or whose focus went, lands on the primary. Each
        // hears its new set (and the focus as SwitchDisplay) before the new list arrives.
        if (_runtime is not null && displays.Count > 0)
        {
            foreach (HostSession session in _runtime.Sessions)
            {
                HostSessionContext ctx = session.Context;
                if (ctx.ConnType != ConnType.ConnRemote || ctx.State != HostSessionState.Authorized)
                {
                    continue;
                }

                int[] watched;
                int focus;
                lock (_lock)
                {
                    if (!_subscriptions.TryGetValue(ctx.ConnectionId, out Subscription? sub))
                    {
                        continue;
                    }

                    bool lost = sub.LostDisplays;
                    sub.LostDisplays = false;
                    if (sub.Displays.Count == 0 && !lost)
                    {
                        continue;
                    }

                    watched = [.. sub.Displays];
                    focus = sub.Focus;
                }

                int Carry(int index) => index >= 0 && index < before.Count
                    ? displays.ToList().FindIndex(d => d.Name == before[index].Name)
                    : -1;
                HashSet<int> carried = [.. watched.Select(Carry).Where(i => i >= 0)];
                int newFocus = Carry(focus);
                if (carried.Count == 0)
                {
                    carried.Add(primary);
                }

                if (!carried.Contains(newFocus))
                {
                    newFocus = carried.Contains(primary) ? primary : carried.Min();
                }

                try
                {
                    await ApplySubscriptionAsync(ctx, carried, newFocus, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
                {
                    // A session on its way out.
                }
            }
        }
        else if (displays.Count == 0)
        {
            // Every display went at once -- a Wayland share ended, the last monitor was unplugged -- and whatever each
            // viewer watched went with them. Kept, a set would read as "already watching" when displays come back, and
            // no stream would start for it; so it is emptied, and marked so that the viewer lands on the primary then.
            lock (_lock)
            {
                foreach (Subscription sub in _subscriptions.Values)
                {
                    if (sub.Displays.Count > 0)
                    {
                        sub.Displays.Clear();
                        sub.Focus = -1;
                        sub.LostDisplays = true;
                    }
                }
            }
        }

        // A stream for a display that no longer exists has nothing to capture and nobody left on it.
        List<VideoService> gone;
        lock (_lock)
        {
            gone = _video.Where(kv => kv.Key >= displays.Count).Select(kv => kv.Value).ToList();
            foreach (VideoService v in gone)
            {
                _video.Remove(v.Display.Index);
            }
        }

        foreach (VideoService v in gone)
        {
            await v.DisposeAsync().ConfigureAwait(false);
        }

        await RestartStreamsForNewGeometryAsync().ConfigureAwait(false);
        await BroadcastDisplaysAsync(changed, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Everything about the displays a running stream depends on, in one comparable string.</summary>
    private static string Signature(IReadOnlyList<DisplayDescriptor> displays) =>
        string.Join("|", displays.Select(d => $"{d.Name}:{d.X},{d.Y},{d.Width}x{d.Height}@{d.Scale}:{d.Rotation}:{(d.IsPrimary ? "P" : "-")}"));

    private DisplaysChanged Describe(int currentDisplay, int changed, string failure)
    {
        var msg = new DisplaysChanged { CurrentDisplay = currentDisplay, Changed = changed, Failure = failure, Notice = _notice };
        msg.Displays.AddRange(DescribeDisplays());
        return msg;
    }

    /// <summary>
    /// Running streams are re-created at whatever size their display now has, and the QoS starts each of their viewers
    /// clean. Given the displays before and after, only the streams of displays that moved or changed size restart:
    /// one viewer's window following its size should not cost everybody watching another display a keyframe.
    /// </summary>
    private async Task RestartStreamsForNewGeometryAsync(IReadOnlyList<DisplayDescriptor>? before = null, IReadOnlyList<DisplayDescriptor>? after = null)
    {
        bool Changed(int index) => before is null || after is null || index >= before.Count || index >= after.Count || before[index] != after[index];

        VideoService[] videos;
        List<(int Connection, int Display)> subscribers;
        lock (_lock)
        {
            videos = [.. _video.Where(kv => Changed(kv.Key)).Select(kv => kv.Value)];
            subscribers = [.. _subscriptions.SelectMany(kv => kv.Value.Displays.Where(Changed).Select(d => (kv.Key, d)))];
        }

        foreach (VideoService v in videos.Where(v => v.IsRunning))
        {
            await v.RestartForDisplayChangeAsync().ConfigureAwait(false);
        }

        foreach ((int connection, int display) in subscribers)
        {
            Qos.ResetStream(connection, display);
        }
    }

    private async Task BroadcastDisplaysAsync(int changed, CancellationToken ct)
    {
        if (_runtime is null)
        {
            return;
        }

        foreach (HostSession session in _runtime.Sessions)
        {
            HostSessionContext ctx = session.Context;
            if (ctx.ConnType != ConnType.ConnRemote || ctx.State != HostSessionState.Authorized)
            {
                continue;
            }

            try
            {
                await ctx.SendAsync(new Message { Misc = new Misc { DisplaysChanged = Describe(ctx.CurrentDisplay, changed, string.Empty) } }, MessagePriority.Control, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // A session on its way out; it will not need the news.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _changes.DisposeAsync().ConfigureAwait(false);
        _platform.Displays.DisplaysChanged -= OnDisplaysChanged;
        if (_platform.DisplaySession is { } shared)
        {
            shared.Closed -= OnDisplaySessionClosed;
            shared.Reopenable -= OnDisplaySessionReopenable;
            await shared.CloseAsync().ConfigureAwait(false);
        }
        ITimer? timer;
        lock (_lock)
        {
            timer = _displayChangeTimer;
            _displayChangeTimer = null;
        }

        timer?.Dispose();

        if (_platform.SessionScreen is { IsOpen: true } sessionScreen)
        {
            await sessionScreen.CloseAsync(CancellationToken.None).ConfigureAwait(false);
        }

        if (_modes.HasChanges)
        {
            await _modes.RestoreAllAsync().ConfigureAwait(false);
        }

        if (_platform.VirtualDisplays is { Count: > 0 } added)
        {
            await added.RemoveAllAsync(CancellationToken.None).ConfigureAwait(false);
        }

        VideoService[] videos;
        lock (_lock)
        {
            videos = _video.Values.ToArray();
            _video.Clear();
        }

        foreach (VideoService v in videos)
        {
            await v.DisposeAsync().ConfigureAwait(false);
        }

        await _cursor.DisposeAsync().ConfigureAwait(false);
        if (_audio is not null)
        {
            await _audio.DisposeAsync().ConfigureAwait(false);
        }

        if (_clipboard is not null)
        {
            await _clipboard.DisposeAsync().ConfigureAwait(false);
        }
    }
}
