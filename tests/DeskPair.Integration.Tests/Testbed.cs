using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using DeskPair.Core.Config;
using DeskPair.Core.Services;
using DeskPair.Core.Session.Controller;
using DeskPair.Core.Session.Host;
using DeskPair.Core.Session.Host.Auth;
using DeskPair.Core.Testing;
using DeskPair.Core.Transport;
using DeskPair.Platform.Abstractions.Audio;
using DeskPair.Platform.Abstractions.Codec;
using DeskPair.Protocol.Crypto;
using DeskPair.Protocol.Messages;
using SessionOptions = DeskPair.Protocol.Messages.SessionOptions;
using DeskPair.Relay;
using DeskPair.Relay.Net;
using DeskPair.Rendezvous;
using DeskPair.Rendezvous.Core;
using DeskPair.Rendezvous.Net;

namespace DeskPair.Integration.Tests;

/// <summary>In-process rendezvous + relay on ephemeral ports, plus factories for hosts and controllers.</summary>
public sealed class Testbed : IAsyncDisposable
{
    private readonly List<IAsyncDisposable> _disposables = [];
    private readonly List<string> _tempDirs = [];

    private Testbed(WebApplication relay, WebApplication rendezvous, ServerKeys keys, int rendezvousPort, int relayPort, ILoggerFactory logs)
    {
        Relay = relay;
        Rendezvous = rendezvous;
        Keys = keys;
        RendezvousPort = rendezvousPort;
        RelayPort = relayPort;
        Logs = logs;
    }

    public WebApplication Relay { get; }
    public WebApplication Rendezvous { get; }
    public ServerKeys Keys { get; }
    public int RendezvousPort { get; }
    public int RelayPort { get; }
    public ILoggerFactory Logs { get; }

    /// <summary>The fake host's displays and the modes they can be switched to; set by <see cref="StartHostAsync"/> when media is enabled.</summary>
    public FakeDisplayEnumerator? Displays { get; private set; }

    public HostTerminalModule? Terminals { get; private set; }

    public FakeDisplayModes? DisplayModes { get; private set; }

    /// <summary>The fake virtual display driver; always present with media, allowed only when asked for.</summary>
    public FakeVirtualDisplays? VirtualDisplays { get; private set; }

    /// <summary>The fake private session screen; always present with media, allowed only when asked for.</summary>
    public FakeSessionScreen? SessionScreen { get; private set; }

    /// <summary>Displays that open for the first viewer and close after the last, when the host was started with them.</summary>
    public FakeDisplaySession? DisplaySession { get; private set; }

    /// <summary>Set by <see cref="StartHostAsync"/> when media is enabled.</summary>
    public HostMediaModule? Media { get; private set; }
    public FakeInputInjector? Injector { get; private set; }
    public FakeScreenCapturerFactory? Capturers { get; private set; }
    public FakeClipboard? HostClipboard { get; private set; }

    public int NatTestPort { get; private set; }

    public PeerSettings Settings => SettingsWith(forceRelay: false);

    /// <summary>Host-side switch for offering the UDP media channel (default on).</summary>
    public bool HostUdpMedia { get; set; } = true;

    /// <summary>What a site with no internet has: no rendezvous, no relay, nothing but the local network.</summary>
    public static PeerSettings NoServerSettings => new()
    {
        RendezvousServer = string.Empty,
        Version = "test",
        ConnectTimeout = TimeSpan.FromSeconds(10),
    };

    public PeerSettings SettingsWith(bool forceRelay) => new()
    {
        UdpMedia = HostUdpMedia,
        RendezvousServer = $"127.0.0.1:{RendezvousPort}",
        ServerPublicKeyBase64 = Keys.PublicKeyBase64,
        Version = "test",
        ConnectTimeout = TimeSpan.FromSeconds(10),
        NatTestPort = NatTestPort,
        ForceRelay = forceRelay,
    };

    public static async Task<Testbed> StartAsync(Action<RendezvousOptions>? rendezvous = null)
    {
        // FreePort finds a port that was free a moment ago: another test starting alongside can bind it first, and
        // the server's own bind then fails ("address already in use" failed a test at its start in a full local run).
        // A clash starts over, on new ports.
        for (int attempt = 1; ; attempt++)
        {
            if (await StartOnceAsync(rendezvous, lastAttempt: attempt == 5) is { } bed)
            {
                return bed;
            }
        }
    }

    /// <summary>
    /// Whether a listener of <paramref name="app"/> could not bind its port because something else had it. Asked of
    /// the listeners: one that fails while the host starts stops the host, and StartAsync then says only that it was
    /// cancelled, while the listener's BoundPort keeps the reason.
    /// </summary>
    private static bool PortWasTaken(WebApplication? app)
    {
        if (app is null)
        {
            return false;
        }

        TcpListenersService? tcp = app.Services.GetService<TcpListenersService>();
        Task?[] bound =
        [
            app.Services.GetService<RelayListener>()?.BoundPort,
            app.Services.GetService<UdpRelayListener>()?.BoundPort,
            app.Services.GetService<UdpListener>()?.BoundPort,
            tcp?.Rendezvous.BoundPort,
            tcp?.NatTest.BoundPort,
        ];
        return bound.Any(t => t is { IsFaulted: true } && IsAddressInUse(t.Exception));
    }

    private static bool IsAddressInUse(Exception? e) => e switch
    {
        null => false,
        SocketException s => s.SocketErrorCode == SocketError.AddressAlreadyInUse,
        Microsoft.AspNetCore.Connections.AddressInUseException => true,
        AggregateException a => a.InnerExceptions.Any(IsAddressInUse),
        _ => IsAddressInUse(e.InnerException),
    };

    /// <summary>A started test bed; null when a port it chose was taken first and it is worth another go.</summary>
    private static async Task<Testbed?> StartOnceAsync(Action<RendezvousOptions>? rendezvous, bool lastAttempt)
    {
        ILoggerFactory logs = LoggerFactory.Create(b => b.AddSimpleConsole().SetMinimumLevel(LogLevel.Debug));
        // The relay's UDP media port shares the TCP port number, so both are chosen up front.
        int relayPort = FreePort();

        // A real port rather than 0, because the rendezvous now polls the relay's admin API to decide
        // whether and how much to use it, and it cannot poll a port nothing told it about. Tests that let
        // Kestrel choose would exercise the "monitoring is broken, use everything" fallback instead of the
        // health and load path that is the point of having one.
        int relayHttpPort = FreePort();

        // The relay trusts this key's tickets and nothing else, the way a deployed one is configured.
        var keys = new ServerKeys(IdentityKey.Create());
        WebApplication relay = RelayServer.Build(["--Logging:LogLevel:Default=Warning"], o =>
        {
            o.Port = relayPort;
            o.UdpPort = relayPort;
            o.HttpPort = relayHttpPort;
            o.MaxBitrateKbps = 1_000_000;
            o.RendezvousPublicKey = keys.PublicKeyBase64;
        });
        WebApplication? rdv = null;
        try
        {
            await relay.StartAsync();
            await relay.Services.GetRequiredService<RelayListener>().BoundPort;
            await relay.Services.GetRequiredService<UdpRelayListener>().BoundPort;

            // UDP and TCP must share one port number because peers use a single address for both.
            int port = FreePort();
            rdv = RendezvousServer.Build(["--Logging:LogLevel:Default=Warning"], o =>
            {
                o.UdpPort = port;
                o.TcpPort = port;
                o.NatTestPort = 0;
                o.HttpPort = 0;
                o.KeyPath = string.Empty;
                o.DatabasePath = string.Empty;
                o.Relays = [new RelayEndpoint
                {
                    Address = $"127.0.0.1:{relayPort}",
                    StatsAddress = $"127.0.0.1:{relayHttpPort}",
                }];
                o.RelayHealthInterval = TimeSpan.FromSeconds(1);
                o.RegisterRateLimitPerMinute = 1000;
                o.PunchRateLimitPerMinute = 1000;
                rendezvous?.Invoke(o);
            }, keys);
            await rdv.StartAsync();
            await rdv.Services.GetRequiredService<UdpListener>().BoundPort;
            await rdv.Services.GetRequiredService<TcpListenersService>().Rendezvous.BoundPort;
            int natTestPort = await rdv.Services.GetRequiredService<TcpListenersService>().NatTest.BoundPort;
            return new Testbed(relay, rdv, keys, port, relayPort, logs) { NatTestPort = natTestPort };
        }
        catch (Exception e)
        {
            bool clash = IsAddressInUse(e) || PortWasTaken(relay) || PortWasTaken(rdv);

            // Whatever this attempt started goes, so that a retry's servers are the only ones running.
            if (rdv is not null)
            {
                await StopQuietlyAsync(rdv);
            }

            await StopQuietlyAsync(relay);
            logs.Dispose();
            if (clash && !lastAttempt)
            {
                return null;
            }

            throw;
        }
    }

    private static async Task StopQuietlyAsync(WebApplication app)
    {
        try
        {
            await app.StopAsync();
        }
        catch (Exception)
        {
            // It did not start; there is nothing to stop.
        }

        await app.DisposeAsync();
    }

    /// <summary>Disposes the object together with the test bed (in reverse order).</summary>
    public void Track(IAsyncDisposable disposable) => _disposables.Add(disposable);

    public string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sunllo-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    public async Task<(HostRuntime Runtime, HostPasswords Passwords, TestApprover Approver)> StartHostAsync(
        HostPolicy? policy = null, int directPort = 0, bool media = false, int displays = 1, bool files = false,
        DeskPair.Platform.Abstractions.Codec.IVideoEncoderFactory? encoders = null, HostPlatform? platform = null,
        PeerSettings? settings = null, DeskPair.Platform.Abstractions.Codec.VideoCodec? codec = null,
        DeskPair.Platform.Abstractions.Terminal.ITerminalHost? terminal = null, bool virtualDisplays = false, bool displaySession = false,
        bool sessionScreen = false)
    {
        string dir = Path.Combine(Path.GetTempPath(), "sunllo-test-" + Guid.NewGuid().ToString("N"));
        _tempDirs.Add(dir);
        var store = new FileSecretStore(dir);
        PeerIdentityStore identity = await PeerIdentityStore.LoadOrCreateAsync(store, await StoredMachineIdProvider.LoadOrCreateAsync(store));
        HostPasswords passwords = await HostPasswords.LoadAsync(store);
        var approver = new TestApprover();

        HostMediaModule? module = null;
        if (platform is not null)
        {
            module = new HostMediaModule(platform, Logs) { Codec = codec };
            Media = module;
            _disposables.Add(module);
        }
        else if (media)
        {
            Injector = new FakeInputInjector();
            HostClipboard = new FakeClipboard();
            Capturers = new FakeScreenCapturerFactory { UnchangedEvery = 7 };
            Displays = new FakeDisplayEnumerator(count: displaySession ? 0 : displays);
            DisplayModes = new FakeDisplayModes(Displays);
            VirtualDisplays = new FakeVirtualDisplays(Displays, DisplayModes);
            SessionScreen = new FakeSessionScreen(Displays, DisplayModes);
            DisplaySession = displaySession ? new FakeDisplaySession(Displays, displays) : null;
            platform = new HostPlatform(
                Displays,
                Capturers,
                encoders ?? new FakeVideoEncoderFactory(),
                Injector,
                new FakeCursorProvider(),
                _ => new FakeAudioCapture(),
                HostClipboard,
                DisplayModes,
                VirtualDisplays,
                VirtualDisplays,
                DisplaySession,
                SessionScreen);
            module = new HostMediaModule(platform, Logs) { Codec = codec, AllowVirtualDisplays = virtualDisplays, AllowSessionScreen = sessionScreen };
            Media = module;
            _disposables.Add(module);
        }

        HostFileModule? fileModule = null;
        var handlers = new List<DeskPair.Core.Session.ISessionHandler<HostSessionContext>>();
        if (module is not null)
        {
            handlers.AddRange(module.Handlers);
        }

        if (files)
        {
            fileModule = new HostFileModule(DeskPair.Core.FileTransfer.LocalFileSystem.Instance, Logs);
            handlers.Add(fileModule);
            _disposables.Add(fileModule);
        }

        HostTerminalModule? terminalModule = null;
        if (terminal is not null)
        {
            terminalModule = new HostTerminalModule(terminal, Logs);
            Terminals = terminalModule;
            handlers.Add(terminalModule);
            _disposables.Add(terminalModule);
        }

        PeerSettings hostSettings = settings ?? Settings;
        var runtime = new HostRuntime(hostSettings, identity, passwords, policy ?? new HostPolicy(), approver, Logs, extraHandlers: handlers)
        {
            DirectAccessPort = directPort,
        };
        module?.Attach(runtime);
        fileModule?.Attach(runtime);
        terminalModule?.Attach(runtime);
        _disposables.Add(runtime);
        await runtime.StartAsync();
        // A host always has an id now (a local one derived from its key), so wait for the server to replace
        // it with the assigned one; without a server there is nothing to wait for.
        if (hostSettings.RendezvousServer.Length > 0)
        {
            await WaitUntilAsync(() => !runtime.Identity.IsLocalId, "host id assignment");
        }

        return (runtime, passwords, approver);
    }

    public (ControllerSession Session, TestCallbacks Callbacks, PeerConnector Connector) CreateController(
        string myId = "controller", SessionOptions? options = null, TestCallbacks? callbacks = null, IAudioPlayback? playback = null,
        DeskPair.Protocol.Rendezvous.ConnType connType = DeskPair.Protocol.Rendezvous.ConnType.ConnRemote, FakeClipboard? clipboard = null,
        DeskPair.Platform.Abstractions.Codec.IVideoDecoderFactory? decoders = null, bool forceRelay = false, bool losslessTiles = true,
        DeskPair.Platform.Abstractions.Codec.VideoCodec? preferredCodec = null,
        bool udpMedia = true, double mediaLoss = 0, Func<DeskPair.Core.Transport.Udp.IDatagramSocket, DeskPair.Core.Transport.Udp.IDatagramSocket>? mediaSocketWrapper = null,
        PeerSettings? settings = null)
    {
        callbacks ??= new TestCallbacks();
        var sessionOptions = new ControllerSessionOptions(myId, "Test Controller", "test", "test", connType)
        {
            SessionOptions = options ?? new SessionOptions(),
            Decoders = decoders ?? new FakeVideoDecoderFactory(),
            AudioPlayback = playback,
            Clipboard = clipboard,
            LosslessTiles = losslessTiles,
            PreferredCodec = preferredCodec,
            UdpMedia = udpMedia,
            MediaDebugLoss = mediaLoss,
            MediaSocketWrapper = mediaSocketWrapper,
        };
        var session = new ControllerSession(sessionOptions, callbacks, Logs.CreateLogger("controller"));
        _disposables.Add(session);
        return (session, callbacks, new PeerConnector(settings ?? SettingsWith(forceRelay), Logs.CreateLogger("connector")));
    }

    public static async Task WaitUntilAsync(Func<bool> condition, string what, int timeoutMs = 10_000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }

            await Task.Delay(25);
        }
    }

    // A port must be bindable for both UDP and TCP. Windows keeps large excluded ranges (Hyper-V) inside the
    // ephemeral range that the OS still hands out to UDP bind(0), so probe explicit candidates instead.
    /// <summary>A port nothing is listening on, for a direct-access host or a probe that should fail.</summary>
    public static int SparePort() => FreePort();

    private static int FreePort()
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            int port = Random.Shared.Next(20_000, 40_000);
            try
            {
                using var tcp = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp) { DualMode = true };
                tcp.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
                using var udp = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = true };
                udp.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
                return port;
            }
            catch (SocketException)
            {
            }
        }

        throw new InvalidOperationException("No port free for both UDP and TCP.");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (IAsyncDisposable d in Enumerable.Reverse(_disposables))
        {
            await d.DisposeAsync();
        }

        await Rendezvous.StopAsync();
        await Rendezvous.DisposeAsync();
        await Relay.StopAsync();
        await Relay.DisposeAsync();
        Logs.Dispose();
        foreach (string dir in _tempDirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}

public sealed class TestApprover : IConnectionApprover
{
    public bool Decision { get; set; } = true;

    public TimeSpan Delay { get; set; }

    public List<HostSessionEvent> Events { get; } = [];

    public List<ConnectionSummary> Requests { get; } = [];

    public async Task<bool> RequestAsync(ConnectionSummary summary, CancellationToken ct)
    {
        lock (Requests)
        {
            Requests.Add(summary);
        }

        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, ct);
        }

        return Decision;
    }

    public void Notify(HostSessionEvent evt)
    {
        lock (Events)
        {
            Events.Add(evt);
        }
    }

    public IEnumerable<string> Chats
    {
        get
        {
            lock (Events)
            {
                return Events.Where(e => e.Kind == HostEventKind.ChatReceived).Select(e => e.Text!).ToList();
            }
        }
    }
}

public sealed class TestCallbacks : IControllerCallbacks
{
    private readonly object _lock = new();

    public List<ControllerSessionState> States { get; } = [];
    public List<ChatMessage> Chats { get; } = [];
    public List<PermissionInfo> Permissions { get; } = [];
    public PeerInfo? PeerInfo { get; private set; }
    public string? CloseReason { get; private set; }
    public TimeSpan? LastRtt { get; private set; }

    public int VideoFrames { get; private set; }
    public int KeyFrames { get; private set; }
    public bool? FirstFrameWasKey { get; private set; }
    public (int W, int H) LastFrameSize { get; private set; }

    /// <summary>The codec of the last decoded frame, which is what negotiation actually produced.</summary>
    public DeskPair.Platform.Abstractions.Codec.VideoCodec? LastCodec { get; private set; }
    public Dictionary<int, int> FramesByDisplay { get; } = new();
    public List<int> DisplaySwitches { get; } = [];

    public List<DisplaySubscription> DisplaySubscriptions { get; } = [];
    public List<DisplaysChanged> DisplaysChanges { get; } = [];

    public List<TerminalResponse> Terminal { get; } = [];

    /// <summary>Everything a terminal has output so far, as text.</summary>
    public string TerminalText(int id)
    {
        lock (Terminal)
        {
            return string.Concat(Terminal.Where(r => r.UnionCase == TerminalResponse.UnionOneofCase.Output && r.Output.Id == id).Select(r => r.Output.Data.ToStringUtf8()));
        }
    }

    public void OnTerminal(TerminalResponse response)
    {
        lock (Terminal)
        {
            Terminal.Add(response);
        }
    }
    public List<CursorData> CursorShapes { get; } = [];
    public int CursorIds { get; private set; }
    public int CursorPositions { get; private set; }
    public AudioStreamFormat? AudioFormat { get; private set; }

    /// <summary>Blocks the pump thread per frame to emulate a slow viewer (acks are sent after the callback).</summary>
    public TimeSpan AckDelay { get; set; }

    /// <summary>Keep a tightly packed BGRA copy of the latest composed frame in <see cref="LastFrame"/>.</summary>
    public bool KeepLastFrame { get; set; }

    public byte[]? LastFrame { get; private set; }

    public void OnStateChanged(ControllerSessionState state)
    {
        lock (_lock)
        {
            States.Add(state);
        }
    }

    public void OnPeerInfo(PeerInfo info) => PeerInfo = info;

    public void OnChat(ChatMessage message)
    {
        lock (_lock)
        {
            Chats.Add(message);
        }
    }

    public void OnPermission(PermissionInfo info)
    {
        lock (_lock)
        {
            Permissions.Add(info);
        }
    }

    public void OnClosed(string reason) => CloseReason = reason;

    public void OnRoundTrip(TimeSpan rtt) => LastRtt = rtt;

    public void OnVideoPacket(int display, uint seq, bool isKeyFrame)
    {
        lock (_lock)
        {
            FirstFrameWasKey ??= isKeyFrame;
            if (isKeyFrame)
            {
                KeyFrames++;
            }
        }

        if (AckDelay > TimeSpan.Zero)
        {
            Thread.Sleep(AckDelay);
        }
    }

    /// <summary>Encoded frames arrive before decoding and carry the codec, which is what negotiation produced.</summary>
    public void OnVideoEncoded(int display, ReadOnlySpan<byte> frame, bool key, long ptsMs, int width, int height, DeskPair.Platform.Abstractions.Codec.VideoCodec codec)
    {
        lock (_lock)
        {
            LastCodec = codec;
        }
    }

    public void OnVideoFrame(int display, in DecodedFrame frame)
    {
        lock (_lock)
        {
            VideoFrames++;
            LastFrameSize = (frame.Width, frame.Height);
            FramesByDisplay[display] = FramesByDisplay.GetValueOrDefault(display) + 1;
            if (KeepLastFrame && !frame.IsGpuSurface)
            {
                int rowBytes = frame.Width * 4;
                if (LastFrame is null || LastFrame.Length != rowBytes * frame.Height)
                {
                    LastFrame = new byte[rowBytes * frame.Height];
                }

                for (int y = 0; y < frame.Height; y++)
                {
                    frame.Cpu.Span.Slice(y * frame.Stride, rowBytes).CopyTo(LastFrame.AsSpan(y * rowBytes));
                }
            }
        }
    }

    public void OnCursorShape(CursorData shape)
    {
        lock (_lock)
        {
            CursorShapes.Add(shape);
        }
    }

    public void OnCursorId(ulong id)
    {
        lock (_lock)
        {
            CursorIds++;
        }
    }

    public void OnCursorPosition(CursorPosition position)
    {
        lock (_lock)
        {
            CursorPositions++;
        }
    }

    public void OnDisplaySwitched(int display)
    {
        lock (_lock)
        {
            DisplaySwitches.Add(display);
        }
    }

    public void OnDisplaysChanged(DisplaysChanged info)
    {
        lock (_lock)
        {
            DisplaysChanges.Add(info);
        }

        DisplaysChangedHook?.Invoke(info);
    }

    /// <summary>Called with each list as it arrives, once it is recorded: how a follower under test hears the host.</summary>
    public Action<DisplaysChanged>? DisplaysChangedHook { get; set; }

    public void OnDisplaySubscription(DisplaySubscription subscription)
    {
        lock (_lock)
        {
            DisplaySubscriptions.Add(subscription);
        }
    }

    public void OnAudioFormat(AudioStreamFormat format) => AudioFormat = format;
}
