using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using DeskPair.Core.Config;
using DeskPair.Platform.Abstractions.Clipboard;
using DeskPair.Core.Session.Controller;
using DeskPair.Core.Session.Host;
using DeskPair.Core.Session.Host.Auth;
using DeskPair.Core.Services;
using DeskPair.Core.Testing;
using DeskPair.Core.Transport;
using DeskPair.Protocol.Messages;
using DeskPair.Tools.PeerCli;
using AbstractCodec = DeskPair.Platform.Abstractions.Codec.VideoCodec;

// Read and write the console as UTF-8 so typed non-ASCII (e.g. CJK) survives the pipe; the Windows console
// defaults to the OEM code page otherwise and mangles it before it ever reaches the host.
try
{
    Console.InputEncoding = System.Text.Encoding.UTF8;
    Console.OutputEncoding = System.Text.Encoding.UTF8;
}
catch (Exception)
{
    // A redirected console without an encoding to set; the default is already fine there.
}

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Usage();
    return 1;
}

Dictionary<string, string> opts = Args.Parse(args.Skip(1));
string verb = args[0];
using ILoggerFactory logs = LoggerFactory.Create(b => b
    .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss.fff "; })
    .SetMinimumLevel(opts.ContainsKey("verbose") ? LogLevel.Debug : LogLevel.Information));
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    return verb switch
    {
        "host" => await RunHostAsync(opts, logs, cts.Token),
        "connect" => await RunConnectAsync(opts, logs, cts.Token),
        "ipc-password" => await SetPasswordOverIpcAsync(opts, logs, cts.Token),
        "ipc-sharing" => await DesktopSharingOverIpcAsync(opts, logs, cts.Token),
        _ => Usage(),
    };
}
catch (OperationCanceledException)
{
    return 0;
}

static int Usage()
{
    Console.Error.WriteLine("""
        Sunllo DeskPair headless peer

          peercli host    --server HOST[:PORT] --key BASE64 [--data DIR] [--password PW] [--approve password|click|both] [--auto-accept] [--clipboard] [--terminal] [--terminal-as highest|user] [--direct-port N] [--verbose]
          peercli connect TARGET --server HOST[:PORT] --key BASE64 [--data DIR] [--password PW] [--file-transfer|--terminal] [--force-relay] [--verbose]
                          [--save-frame PATH.png] [--frame-stats]
          peercli ipc-password --token HEX --password PW [--socket PATH]
          peercli ipc-sharing --token HEX [--ask] [--socket PATH]
                          what the engine remembers about sharing this account's Wayland desktop through the portal;
                          with --ask, the engine asks for it on this account's screen, as Settings > Security does with
                          unattended access installed, and this waits for the answer
                          sets the temporary password of the DeskPair engine running as this account, over its IPC
                          socket; for a test engine started with --server --ipc-token HEX, whose password is otherwise
                          nobody's to know

        TARGET is a 9-digit id, or ip[:port] / host:port for a direct connection.
        In connect mode, lines typed on stdin are sent as chat; Ctrl+C disconnects. Lines starting with ':'
        are script commands (move/click/type/key/lock/sleep/save PATH.png/...). --frame-stats prints one line
        per decoded frame -- a hash, how much is not black, and how much changed since the previous frame -- so
        a stale picture, a black picture and the wrong picture are each a number rather than a look.
        """);
    return 1;
}

static async Task<int> SetPasswordOverIpcAsync(Dictionary<string, string> o, ILoggerFactory logs, CancellationToken ct)
{
    if (!o.TryGetValue("token", out string? hex) || !o.TryGetValue("password", out string? password))
    {
        return Usage();
    }

    // The user's own socket unless told otherwise: the machine-wide one, tried first by the app, is the daemon's.
    var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
    await socket.ConnectAsync(new System.Net.Sockets.UnixDomainSocketEndPoint(o.GetValueOrDefault("socket") ?? DeskPair.Core.Ipc.IpcEndpoint.Default.UserSocketPath), ct);
    await using DeskPair.Core.Ipc.IpcClient client = await DeskPair.Core.Ipc.IpcClient.ConnectAsync(
        new System.Net.Sockets.NetworkStream(socket, ownsSocket: true), Convert.FromHexString(hex), DeskPair.Core.Ipc.IpcRoles.Ui, logs.CreateLogger("ipc"), ct);
    DeskPair.Protocol.Ipc.IpcMessage answer = await client.RequestAsync(
        new DeskPair.Protocol.Ipc.IpcMessage { SetTemporaryPassword = new DeskPair.Protocol.Ipc.SetTemporaryPassword { Password = password } }, ct);
    Console.WriteLine($"*** {answer.UnionCase}");
    return 0;
}

static async Task<int> DesktopSharingOverIpcAsync(Dictionary<string, string> o, ILoggerFactory logs, CancellationToken ct)
{
    if (!o.TryGetValue("token", out string? hex))
    {
        return Usage();
    }

    var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
    await socket.ConnectAsync(new System.Net.Sockets.UnixDomainSocketEndPoint(o.GetValueOrDefault("socket") ?? DeskPair.Core.Ipc.IpcEndpoint.Default.UserSocketPath), ct);
    await using DeskPair.Core.Ipc.IpcClient client = await DeskPair.Core.Ipc.IpcClient.ConnectAsync(
        new System.Net.Sockets.NetworkStream(socket, ownsSocket: true), Convert.FromHexString(hex), DeskPair.Core.Ipc.IpcRoles.Ui, logs.CreateLogger("ipc"), ct);

    // Asking waits for a person, and the portal gives up on a dialog at 45 seconds.
    bool ask = o.ContainsKey("ask");
    DeskPair.Protocol.Ipc.IpcMessage answer = await client.RequestAsync(
        new DeskPair.Protocol.Ipc.IpcMessage { DesktopSharingRequest = new DeskPair.Protocol.Ipc.DesktopSharingRequest { Ask = ask } },
        ct, ask ? TimeSpan.FromSeconds(60) : null);
    DeskPair.Protocol.Ipc.DesktopSharingState state = answer.DesktopSharingState ?? new DeskPair.Protocol.Ipc.DesktopSharingState();
    string[] states = ["unavailable", "not yet", "watch only", "allowed"];
    string[] outcomes = ["not asked", "allowed", "watch only", "declined", "unanswered", "failed"];
    Console.WriteLine(
        $"*** Desktop sharing: {(state.State is >= 0 and < 4 ? states[state.State] : state.State.ToString())}" +
        (ask ? $"; asked: {(state.Outcome is >= 0 and < 6 ? outcomes[state.Outcome] : state.Outcome.ToString())}" : string.Empty) +
        (state.Detail.Length > 0 ? $" ({state.Detail})" : string.Empty));
    return 0;
}

static async Task<int> RunHostAsync(Dictionary<string, string> o, ILoggerFactory logs, CancellationToken ct)
{
    PeerSettings settings = Settings(o);
    var store = new FileSecretStore(o.GetValueOrDefault("data", Path.Combine(AppContext.BaseDirectory, "peercli-host")));
    using PeerIdentityStore identity = await PeerIdentityStore.LoadOrCreateAsync(store, await StoredMachineIdProvider.LoadOrCreateAsync(store, ct), ct);
    HostPasswords passwords = await HostPasswords.LoadAsync(store, ct);
    if (o.TryGetValue("password", out string? permanent))
    {
        await passwords.SetPermanentAsync(permanent, ct);
    }

    var policy = new HostPolicy
    {
        ApproveMode = o.GetValueOrDefault("approve", "password") switch
        {
            "click" => ApproveMode.ApproveClick,
            "both" => ApproveMode.ApproveBoth,
            _ => ApproveMode.ApprovePassword,
        },

        // --terminal: a real shell on this machine, as the account running PeerCli. The lab's way to drive
        // the whole terminal path on a Mac or a Linux box without redeploying the product installed there.
        TerminalEnabled = o.ContainsKey("terminal"),
    };
    var approver = new ConsoleApprover(o.ContainsKey("auto-accept"));
    // Until platform projects exist the host streams a synthetic desktop and records injected input.
    // --clipboard is the exception: it wires this machine's real one, so a controller pasting text or a
    // picture can be checked against what actually lands on the desktop.
    IClipboard? clipboard = o.ContainsKey("clipboard") ? RealClipboard(logs.CreateLogger("clipboard")) : null;
    var platform = new HostPlatform(new FakeDisplayEnumerator(1280, 720), new FakeScreenCapturerFactory { UnchangedEvery = 5 }, new FakeVideoEncoderFactory(), new FakeInputInjector(), new FakeCursorProvider(), _ => new FakeAudioCapture(), clipboard);
    await using var media = new HostMediaModule(platform, logs);
    await using var terminals = new HostTerminalModule(RealTerminal(logs.CreateLogger("terminal")), logs)
    {
        RunAs = o.GetValueOrDefault("terminal-as") == "user"
            ? DeskPair.Platform.Abstractions.Terminal.TerminalRunAs.User
            : DeskPair.Platform.Abstractions.Terminal.TerminalRunAs.Highest,
    };
    await using var runtime = new HostRuntime(settings, identity, passwords, policy, approver, logs, extraHandlers: [.. media.Handlers, terminals])
    {
        DirectAccessPort = int.TryParse(o.GetValueOrDefault("direct-port"), out int dp) ? dp : 0,
    };
    media.Attach(runtime);
    terminals.Attach(runtime);
    if (policy.TerminalEnabled)
    {
        Console.WriteLine($"*** Terminal enabled: shells run as {terminals.DescribeIdentity()}");
    }
    runtime.Rendezvous.IdAssigned += id => Console.WriteLine($"*** ID: {id}");
    passwords.TemporaryPasswordChanged += pw => Console.WriteLine($"*** Temporary password: {pw}");

    await runtime.StartAsync(ct);
    Console.WriteLine($"*** ID: {(identity.Id.Length == 0 ? "(registering...)" : identity.Id)}");
    Console.WriteLine($"*** Temporary password: {passwords.TemporaryPassword}");
    Console.WriteLine($"*** Permanent password: {(passwords.HasPermanentPassword ? "set" : "not set")}, approve mode: {policy.ApproveMode}");
    Console.WriteLine("*** Host running; Ctrl+C to stop.");
    try
    {
        await Task.Delay(Timeout.Infinite, ct);
    }
    catch (OperationCanceledException)
    {
    }

    return 0;
}

// This machine's own clipboard, for a lab host that has to show what a controller actually pasted.
// Windows is deliberately absent: it has real tests, and PeerCli must stay buildable on every OS.
static DeskPair.Platform.Abstractions.Terminal.ITerminalHost RealTerminal(ILogger log)
{
    if (OperatingSystem.IsMacOS())
    {
        return DeskPair.Platform.MacOS.Terminal.MacTerminal.Create(log);
    }

    if (OperatingSystem.IsLinux())
    {
        return DeskPair.Platform.Linux.Terminal.LinuxTerminal.Create(log);
    }

#if WINDOWS
    return new DeskPair.Platform.Windows.Terminal.WindowsTerminalHost(log);
#else
    return new DeskPair.Platform.Abstractions.Terminal.UnavailableTerminalHost("This build of PeerCli has no terminal for this system.");
#endif
}

static IClipboard? RealClipboard(ILogger log)
{
    try
    {
        if (OperatingSystem.IsMacOS())
        {
            return new DeskPair.Platform.MacOS.Clipboard.MacClipboard(log);
        }

        if (OperatingSystem.IsLinux())
        {
            return new DeskPair.Platform.Linux.Clipboard.X11Clipboard(log);
        }

        log.LogWarning("--clipboard is for macOS and Linux; this host will have none.");
        return null;
    }
    catch (Exception e)
    {
        // No display, or no shim. Worth saying out loud: a silent fallback here would look like a
        // clipboard that simply does not work.
        log.LogWarning(e, "Could not open this machine's clipboard; the host will have none.");
        return null;
    }
}

static async Task<int> RunConnectAsync(Dictionary<string, string> o, ILoggerFactory logs, CancellationToken ct)
{
    if (!o.TryGetValue("_target", out string? target))
    {
        return Usage();
    }

    PeerSettings settings = Settings(o);
    var connector = new PeerConnector(settings, logs.CreateLogger("connector"));
    string? savePath = o.GetValueOrDefault("save-frame");
    bool frameStats = o.ContainsKey("frame-stats");

    // A real decoder is used only when asked to save a PNG (Windows Media Foundation, which decodes the
    // H.264/H.265 a Windows or macOS host sends); otherwise the fake decoder just counts frames. The fake
    // advertises every codec so a host that only offers VP9 still negotiates a stream.
    DeskPair.Platform.Abstractions.Codec.IVideoDecoderFactory decoders =
        new FakeVideoDecoderFactory
        {
            Codecs = [AbstractCodec.H264, AbstractCodec.H265, AbstractCodec.Vp8, AbstractCodec.Vp9, AbstractCodec.Av1],
        };
#if WINDOWS
    if (savePath is not null || frameStats)
    {
        // Media Foundation plus VP9, not Media Foundation alone: a host whose only encoder is libvpx -- which
        // is every Linux host -- would otherwise share no codec with this viewer, and the session would close
        // with "this computer has no H264 encoder" before a single frame arrived.
        decoders = new DeskPair.Platform.Abstractions.Codec.FallbackVideoDecoderFactory(
            new DeskPair.Platform.Windows.Codec.MfVideoDecoderFactory(logs),
            new DeskPair.Codec.Vpx.VpxVideoDecoderFactory(logs));
    }
#else
    if (savePath is not null)
    {
        Console.Error.WriteLine("--save-frame needs the Windows build (Media Foundation decoder).");
    }
#endif

    var scriptClipboard = new ScriptClipboard();
    var options = new ControllerSessionOptions("cli", Environment.UserName, Environment.OSVersion.Platform.ToString(), settings.Version,
        o.ContainsKey("file-transfer") ? DeskPair.Protocol.Rendezvous.ConnType.ConnFileTransfer
            : o.ContainsKey("terminal") ? DeskPair.Protocol.Rendezvous.ConnType.ConnTerminal
            : DeskPair.Protocol.Rendezvous.ConnType.ConnRemote)
    {
        Decoders = decoders,
        Clipboard = scriptClipboard,
        FileSystem = DeskPair.Core.FileTransfer.LocalFileSystem.Instance,
    };
    var callbacks = new ConsoleCallbacks(savePath, frameStats);
    await using var session = new ControllerSession(options, callbacks, logs.CreateLogger("session"));

    Console.WriteLine($"*** Connecting to {target} ...");
    await session.ConnectAsync(connector, target, ct);
    Console.WriteLine($"*** Connected via {session.TransportKind}; host approve mode {session.Challenge!.ApproveMode}");

    string? password = o.GetValueOrDefault("password");
    if (password is null && session.Challenge.ApproveMode != ApproveMode.ApproveClick)
    {
        Console.Write("Password: ");
        password = Console.ReadLine();
    }

    LoginResult result = await session.LoginAsync(password, ct);
    if (!result.Success)
    {
        Console.WriteLine($"*** Login failed: {result.Error!.Code} {result.Error.Message}");
        return 2;
    }

    PeerInfo info = result.PeerInfo!;
    Console.WriteLine($"*** Logged in to {info.Hostname} ({info.Platform}, {info.Username}); displays: {info.Displays.Count}; granted: {string.Join(",", info.Granted)}");
    Console.WriteLine("*** Type chat lines; Ctrl+C to disconnect.");
    _ = Task.Run(async () =>
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(ct))
        {
            string perDisplay = callbacks.PacketsByDisplay.Count > 1
                ? " (" + string.Join(", ", callbacks.PacketsByDisplay.OrderBy(kv => kv.Key).Select(kv => $"display {kv.Key}: {kv.Value}")) + ")"
                : string.Empty;
            Console.WriteLine($"*** video: {session.VideoFramesReceived} received{perDisplay} / {session.VideoFramesDecoded} decoded, audio: {session.AudioFramesReceived} frames, cursor moves: {callbacks.CursorMoves}");
        }
    }, ct);

    // File-transfer progress: print only when the file or the state changes, so a resume is visible (the
    // first snapshot of a resumed file already reports a non-zero transferred count) without flooding the
    // console with a line per block.
    var ftFile = new System.Collections.Concurrent.ConcurrentDictionary<int, string>();
    session.Files.Progress += s =>
    {
        string tag = $"{s.CurrentFile}|{s.State}";
        if (ftFile.TryGetValue(s.Id, out string? prev) && prev == tag)
        {
            return;
        }

        ftFile[s.Id] = tag;
        Console.WriteLine($"*** FT {(s.IsSending ? "send" : "recv")} id={s.Id} file={s.CurrentFile} {s.TransferredBytes}/{s.TotalBytes} {s.State}");
    };

    // Input/clipboard driving for automated tests: lines beginning with ':' are commands (see InputScript),
    // anything else is chat. Coordinates are absolute pixels in the host's virtual screen.
    var input = new InputScript(session, scriptClipboard, callbacks);
    Task stdin = Task.Run(async () =>
    {
        while (!ct.IsCancellationRequested)
        {
            string? line = await Console.In.ReadLineAsync(ct);
            if (line is null)
            {
                break;
            }

            if (line.StartsWith(':'))
            {
                await input.RunAsync(line[1..].Trim(), ct);
            }
            else if (line.Length > 0)
            {
                await session.SendChatAsync(line, ct);
            }
        }
    }, ct);

    await Task.WhenAny(session.Completion, stdin, Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => { }, TaskScheduler.Default));
    if (session.Completion.IsCompleted)
    {
        Console.WriteLine($"*** Session ended: {await session.Completion}");
    }
    else
    {
        await session.CloseAsync("user quit");
    }

    return 0;
}

static PeerSettings Settings(Dictionary<string, string> o)
{
    if (!o.TryGetValue("server", out string? server))
    {
        Console.Error.WriteLine("--server is required");
        Environment.Exit(1);
    }

    return new PeerSettings
    {
        RendezvousServer = server,
        ServerPublicKeyBase64 = o.GetValueOrDefault("key", string.Empty),
        Version = "peercli",
        ForceRelay = o.ContainsKey("force-relay"),
    };
}

namespace DeskPair.Tools.PeerCli
{
    internal static class Args
    {
        /// <summary>--name value / --flag; the first bare argument is stored under "_target".</summary>
        public static Dictionary<string, string> Parse(IEnumerable<string> args)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            string? pending = null;
            foreach (string a in args)
            {
                if (a.StartsWith("--", StringComparison.Ordinal))
                {
                    if (pending is not null)
                    {
                        result[pending] = string.Empty;
                    }

                    pending = a[2..];
                }
                else if (pending is not null)
                {
                    result[pending] = a;
                    pending = null;
                }
                else
                {
                    result.TryAdd("_target", a);
                }
            }

            if (pending is not null)
            {
                result[pending] = string.Empty;
            }

            return result;
        }
    }

    internal sealed class ConsoleApprover(bool autoAccept) : IConnectionApprover
    {
        public Task<bool> RequestAsync(ConnectionSummary summary, CancellationToken ct)
        {
            Console.WriteLine($"*** Incoming {summary.ConnType} from {summary.PeerId} ({summary.PeerName}) at {summary.RemoteEndPoint}: {(autoAccept ? "auto-accepted" : "rejected (use --auto-accept)")}");
            return Task.FromResult(autoAccept);
        }

        public void Notify(HostSessionEvent evt) => Console.WriteLine($"*** [{evt.ConnectionId}] {evt.Kind}{(evt.Text is null ? string.Empty : ": " + evt.Text)}");
    }

    internal sealed class ConsoleCallbacks : IControllerCallbacks
    {
        public int CursorMoves;

        private readonly bool _stats;
        private string? _savePath;
        private byte[]? _previous;
        private int _frames;

        public ConsoleCallbacks(string? savePath = null, bool stats = false)
        {
            _savePath = savePath;
            _stats = stats;
        }

        private int _saveDisplay = -1;

        /// <summary>
        /// Writes the next decoded frame -- of <paramref name="display"/> when it is not -1 -- to <paramref name="path"/>
        /// (Windows build only).
        /// </summary>
        public void SaveNext(string path, int display = -1)
        {
            Volatile.Write(ref _saveDisplay, display);
            Volatile.Write(ref _savePath, path);
        }

        public void OnStateChanged(ControllerSessionState state) => Console.WriteLine($"*** State: {state}");

        public void OnCursorPosition(CursorPosition position) => Interlocked.Increment(ref CursorMoves);

        /// <summary>Video packets per display, so a subscription to several shows each one arriving.</summary>
        public readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> PacketsByDisplay = new();

        public void OnVideoPacket(int display, uint seq, bool isKeyFrame) => PacketsByDisplay.AddOrUpdate(display, 1, (_, n) => n + 1);

        public void OnCursorShape(CursorData shape)
        {
            // Count non-transparent pixels so a real cursor image (XFixes on Linux) is distinguishable from
            // an empty or all-zero placeholder. BGRA, so the alpha byte is at index 3 of each 4-byte pixel.
            ReadOnlySpan<byte> bgra = shape.Bgra.Span;
            int opaque = 0;
            for (int i = 3; i < bgra.Length; i += 4)
            {
                if (bgra[i] != 0)
                {
                    opaque++;
                }
            }

            Console.WriteLine($"*** Cursor shape id={shape.Id} {shape.Width}x{shape.Height} hot=({shape.Hotx},{shape.Hoty}) bytes={bgra.Length} opaquePixels={opaque}");
        }

        public void OnVideoFrame(int display, in DeskPair.Platform.Abstractions.Codec.DecodedFrame frame)
        {
            if (frame.Cpu.IsEmpty)
            {
                return;
            }

            if (_stats)
            {
                // Packed BGRA copy so frames of the same size compare byte for byte whatever their stride.
                byte[] pixels = Packed(frame);
                int n = Interlocked.Increment(ref _frames);
                string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pixels))[..8].ToLowerInvariant();
                double diff = _previous is null ? 0 : Diff(_previous, pixels);
                Console.WriteLine($"*** frame #{n} display {display} {frame.Width}x{frame.Height} sha256={hash} nonBlack={NonBlack(pixels):P1} diffFromPrev={diff:F4}");
                _previous = pixels;
            }

            int wanted = Volatile.Read(ref _saveDisplay);
            if (Volatile.Read(ref _savePath) is null || (wanted >= 0 && wanted != display))
            {
                return;
            }

            string? path = Interlocked.Exchange(ref _savePath, null);
            if (path is null)
            {
                return;
            }
#if WINDOWS
            try
            {
                SaveBgraPng(frame, path);
                Console.WriteLine($"*** Saved a {frame.Width}x{frame.Height} frame of display {display} to {path}");
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"*** Could not save the frame: {e.Message}");
            }
#endif
        }

        private static byte[] Packed(in DeskPair.Platform.Abstractions.Codec.DecodedFrame frame)
        {
            int rowBytes = frame.Width * 4;
            byte[] pixels = new byte[rowBytes * frame.Height];
            ReadOnlySpan<byte> src = frame.Cpu.Span;
            for (int y = 0; y < frame.Height; y++)
            {
                src.Slice(y * frame.Stride, rowBytes).CopyTo(pixels.AsSpan(y * rowBytes, rowBytes));
            }

            return pixels;
        }

        /// <summary>The share of pixels brighter than near-black in any channel.</summary>
        private static double NonBlack(byte[] px)
        {
            long lit = 0;
            for (int i = 0; i < px.Length; i += 4)
            {
                if (px[i] > 16 || px[i + 1] > 16 || px[i + 2] > 16)
                {
                    lit++;
                }
            }

            return (double)lit / (px.Length / 4);
        }

        /// <summary>The share of pixels whose colour moved by more than 8 in any channel; 1 when the sizes differ.</summary>
        private static double Diff(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
            {
                return 1;
            }

            long changed = 0;
            for (int i = 0; i < a.Length; i += 4)
            {
                if (Math.Abs(a[i] - b[i]) > 8 || Math.Abs(a[i + 1] - b[i + 1]) > 8 || Math.Abs(a[i + 2] - b[i + 2]) > 8)
                {
                    changed++;
                }
            }

            return (double)changed / (a.Length / 4);
        }

#if WINDOWS
        private static void SaveBgraPng(in DeskPair.Platform.Abstractions.Codec.DecodedFrame frame, string path)
        {
            var info = new SkiaSharp.SKImageInfo(frame.Width, frame.Height, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Opaque);
            using var bmp = new SkiaSharp.SKBitmap(info);
            ReadOnlySpan<byte> src = frame.Cpu.Span;
            int rowBytes = frame.Width * 4;
            unsafe
            {
                byte* dst = (byte*)bmp.GetPixels();
                for (int y = 0; y < frame.Height; y++)
                {
                    src.Slice(y * frame.Stride, rowBytes).CopyTo(new Span<byte>(dst + (long)y * info.RowBytes, rowBytes));
                }
            }

            using SkiaSharp.SKImage image = SkiaSharp.SKImage.FromBitmap(bmp);
            using SkiaSharp.SKData data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
            using FileStream fs = File.Create(path);
            data.SaveTo(fs);
        }
#endif

        public void OnDisplaySwitched(int display) => Console.WriteLine($"*** Now viewing display {display}");

        public void OnDisplaySubscription(DisplaySubscription subscription) => Console.WriteLine(subscription.Failure.Length > 0
            ? $"*** Displays [{string.Join(",", subscription.Displays)}] kept (focus {subscription.Focus}): {subscription.Failure}"
            : $"*** Streaming displays [{string.Join(",", subscription.Displays)}], focus {subscription.Focus}");

        public void OnPeerInfo(PeerInfo info)
        {
            for (int i = 0; i < info.Displays.Count; i++)
            {
                DisplayInfo d = info.Displays[i];
                Console.WriteLine($"*** Display {i}: {d.Width}x{d.Height} scale {d.Scale}{(d.Primary ? " primary" : string.Empty)} \"{d.Name}\"; modes: {string.Join(", ", d.Modes.Select(Describe))}{(d.AnySize is { } any ? $"; any size {any.MinWidth}x{any.MinHeight}..{any.MaxWidth}x{any.MaxHeight} step {any.Step}" : string.Empty)}{(d.Original is { } o ? $"; original {Describe(o)}" : string.Empty)}");
            }
        }

        /// <summary>Hears every list as it arrives, before it is printed: how <c>:follow</c> learns the answers.</summary>
        public event Action<DisplaysChanged>? DisplaysChangedHook;

        public void OnDisplaysChanged(DisplaysChanged info)
        {
            DisplaysChangedHook?.Invoke(info);
            if (info.Notice.Length > 0)
            {
                Console.WriteLine($"*** Host says: {info.Notice}");
            }

            if (info.Displays.Count == 0 && info.Failure.Length == 0)
            {
                Console.WriteLine("*** Displays: none");
            }

            if (info.Failure.Length > 0)
            {
                Console.WriteLine($"*** Resolution refused: {info.Failure}");
                return;
            }

            for (int i = 0; i < info.Displays.Count; i++)
            {
                DisplayInfo d = info.Displays[i];
                Console.WriteLine($"*** Display {i} now {d.Width}x{d.Height}{(i == info.Changed ? " (changed)" : string.Empty)}{(d.Original is { } o ? $"; original {Describe(o)}" : string.Empty)}");
            }
        }

        private static string Describe(Resolution r) => r.Scale > 1.01 ? $"{r.Width}x{r.Height}@{r.Scale}x" : $"{r.Width}x{r.Height}";

        public void OnChat(ChatMessage message) => Console.WriteLine($"<host> {message.Text}");

        public void OnPermission(PermissionInfo info) => Console.WriteLine($"*** Permission {info.Permission}: {(info.Enabled ? "on" : "off")}");

        public void OnStalled(bool stalled) => Console.WriteLine(stalled ? "*** STALLED: waiting for the host..." : "*** RECOVERED: host is responding again");

        public void OnClosed(string reason) => Console.WriteLine($"*** Closed: {reason}");

        private readonly Dictionary<int, System.Text.StringBuilder> _terminalText = new();

        /// <summary>Everything terminal <paramref name="id"/> has output so far.</summary>
        public string TerminalText(int id)
        {
            lock (_terminalText)
            {
                return _terminalText.TryGetValue(id, out System.Text.StringBuilder? sb) ? sb.ToString() : string.Empty;
            }
        }

        public void OnTerminal(TerminalResponse response)
        {
            switch (response.UnionCase)
            {
                case TerminalResponse.UnionOneofCase.Opened:
                    Console.WriteLine($"*** term {response.Opened.Id}: opened as {response.Opened.Identity} ({response.Opened.Shell})");
                    break;
                case TerminalResponse.UnionOneofCase.Output:
                    string text = response.Output.Data.ToStringUtf8();
                    lock (_terminalText)
                    {
                        if (!_terminalText.TryGetValue(response.Output.Id, out System.Text.StringBuilder? sb))
                        {
                            _terminalText[response.Output.Id] = sb = new System.Text.StringBuilder();
                        }

                        sb.Append(text);
                    }

                    Console.WriteLine($"[term {response.Output.Id}] {InputScript.Escape(text)}");
                    break;
                case TerminalResponse.UnionOneofCase.Exit:
                    Console.WriteLine($"*** term {response.Exit.Id}: exited with code {response.Exit.Code} ({response.Exit.Reason})");
                    break;
                case TerminalResponse.UnionOneofCase.Error:
                    Console.WriteLine($"*** term {response.Error.Id}: ERROR {response.Error.Message}");
                    break;
            }
        }

        public void OnRoundTrip(TimeSpan rtt) => Console.WriteLine($"*** RTT {rtt.TotalMilliseconds:F0} ms");
    }

    /// <summary>
    /// Drives keyboard/mouse/clipboard for automated tests. Lines prefixed ':' become commands:
    ///   m X Y | move X Y      absolute mouse move (pixels of the display being viewed)
    ///   down|up [l|r|m]       press/release a button at the last move position
    ///   click [l|r|m]         a down+up
    ///   wheel DY [DX]         scroll (vertical DY, optional horizontal DX)
    ///   type TEXT             type a string (sent as a Seq, reproduced on the host layout)
    ///   key NAME              tap a control key (Return, Tab, Escape, Backspace, Delete, Left/Right/Up/Down,
    ///                         Home, End, PageUp, PageDown, Space, F1..F12)
    ///   mod NAME down|up      hold/release a modifier (Ctrl, Alt, Shift, Meta) to build a chord
    ///   cad | lock            Ctrl+Alt+Del / lock screen
    ///   switch N              switch to display N
    ///   subscribe A,B[ F]     watch displays A and B at once (F the focus, default the first); "subscribe -" for none
    ///   vdisplay add [WxH] | vdisplay remove N   plug in a display that does not exist, or unplug one
    ///   refresh [N]           ask for a keyframe of display N (default the current one): a still desk sends nothing
    ///   res WxH[@S] | res original   change the current display's resolution (S = scale, e.g. 3840x2160@2)
    ///   clip TEXT             put TEXT on this side's clipboard (controller -> host)
    ///   save PATH.png [N]     write the next picture (of display N) to a file. Against a real host pass
    ///                         --frame-stats: the default fake decoder cannot read real video, so pictures come only
    ///                         from lossless tiles, and after twelve frames it cannot read the display waits for a
    ///                         keyframe the fake never takes, refusing tiles and asking for a refresh every second
    ///   sleep MS              wait between steps
    ///   term [COLSxROWS]      open a terminal (needs --terminal); prints its id
    ///   tsend ID TEXT         type into terminal ID (\n \r \t \xHH and ^C style escapes)
    ///   tresize ID COLSxROWS | tsig ID int|term|kill|hup | tclose ID
    ///   expect ID REGEX       wait up to 10 s for the terminal's output so far to match
    ///   dump ID               print everything the terminal has output
    /// </summary>
    internal sealed class InputScript(ControllerSession session, ScriptClipboard clipboard, ConsoleCallbacks callbacks)
    {
        private int _x;
        private int _y;
        private int _display;
        private int _terminals;

        /// <summary>What <c>:follow</c> started: the display kept the size of a pretend window.</summary>
        private ResolutionFollower? _follower;
        private bool _hearing;

        private static (int Columns, int Rows) Size(string arg)
        {
            string[] parts = arg.Trim().Split('x', 2);
            return (int.Parse(parts[0]), int.Parse(parts[1]));
        }

        /// <summary>
        /// <c>:follow WxH[@S]</c> keeps the current display the size of a window WxH (at interface scale S) the way the
        /// desktop app's "Match window" does; each further <c>:follow</c> is the window resized. <c>:follow off</c> stops,
        /// giving the display back its own size.
        /// </summary>
        private async Task FollowAsync(string arg, CancellationToken ct)
        {
            if (arg.Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                if (_follower is { } running)
                {
                    _follower = null;
                    await running.StopAsync();
                    running.Dispose();
                    Console.WriteLine("*** Not following the window any more");
                }

                return;
            }

            string[] scaleSplit = arg.Split('@');
            (int w, int h) = Size(scaleSplit[0]);
            double uiScale = scaleSplit.Length > 1 ? double.Parse(scaleSplit[1], CultureInfo.InvariantCulture) : 1;
            if (_follower is null)
            {
                var follower = new ResolutionFollower(TimeProvider.System, (display, mode) => session.SetResolutionAsync(display, mode, ct).AsTask());
                follower.StateChanged += () => Console.WriteLine(
                    $"*** Following the window: {follower.State}{(follower.State == FollowState.Failed ? $" ({(follower.Failure.Length > 0 ? follower.Failure : "no answer")})" : string.Empty)}");
                if (!_hearing)
                {
                    _hearing = true;
                    callbacks.DisplaysChangedHook += info =>
                        _follower?.Heard(_display, _display < info.Displays.Count ? info.Displays[_display] : null, info.Changed, info.Failure);
                }

                _follower = follower;
                follower.Start(_display, session.PeerInfo is { } peer && _display < peer.Displays.Count ? peer.Displays[_display] : null);
            }

            _follower.Window(w, h, uiScale);
        }

        private static (int Id, string Text) IdAndRest(string arg)
        {
            int sp = arg.IndexOf(' ');
            return sp < 0 ? (int.Parse(arg), string.Empty) : (int.Parse(arg[..sp]), arg[(sp + 1)..]);
        }

        /// <summary>\n \r \t \\ \xHH and ^C-style control characters, so a script line can carry a keystroke.</summary>
        private static byte[] Unescape(string text)
        {
            var bytes = new List<byte>();
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\\' && i + 1 < text.Length)
                {
                    char n = text[++i];
                    switch (n)
                    {
                        case 'n': bytes.Add((byte)'\n'); continue;
                        case 'r': bytes.Add((byte)'\r'); continue;
                        case 't': bytes.Add((byte)'\t'); continue;
                        case 'e': bytes.Add(0x1B); continue;
                        case '\\': bytes.Add((byte)'\\'); continue;
                        case 'x' when i + 2 < text.Length:
                            bytes.Add(Convert.ToByte(text.Substring(i + 1, 2), 16));
                            i += 2;
                            continue;
                    }

                    bytes.Add((byte)n);
                }
                else if (c == '^' && i + 1 < text.Length && char.IsAsciiLetterUpper(text[i + 1]))
                {
                    bytes.Add((byte)(text[++i] - '@'));
                }
                else
                {
                    bytes.AddRange(System.Text.Encoding.UTF8.GetBytes(c.ToString()));
                }
            }

            return [.. bytes];
        }

        internal static string Escape(string text) => text.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\x1b", "\\e");

        public async Task RunAsync(string line, CancellationToken ct)
        {
            if (line.Length == 0)
            {
                return;
            }

            int sp = line.IndexOf(' ');
            string verb = (sp < 0 ? line : line[..sp]).ToLowerInvariant();
            string arg = sp < 0 ? string.Empty : line[(sp + 1)..];
            try
            {
                switch (verb)
                {
                    case "m" or "move":
                    {
                        // :m X Y [FWxFH] -- with a frame size, the point is on a picture of that size, as a viewer
                        // still showing the old picture during a resolution change would send it.
                        string[] parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        (_x, _y) = (int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture));
                        var move = new MouseEvent { Mask = 0, X = _x, Y = _y, Display = _display };
                        if (parts.Length > 2)
                        {
                            (move.FrameWidth, move.FrameHeight) = Size(parts[2]);
                        }

                        await session.SendMouseAsync(move, ct);
                        break;
                    }
                    case "down":
                        await session.SendMouseAsync(new MouseEvent { Mask = 1 | (Button(arg) << 3), X = _x, Y = _y, Display = _display }, ct);
                        break;
                    case "up":
                        await session.SendMouseAsync(new MouseEvent { Mask = 2 | (Button(arg) << 3), X = _x, Y = _y, Display = _display }, ct);
                        break;
                    case "click":
                        int b = Button(arg);
                        await session.SendMouseAsync(new MouseEvent { Mask = 1 | (b << 3), X = _x, Y = _y, Display = _display }, ct);
                        await session.SendMouseAsync(new MouseEvent { Mask = 2 | (b << 3), X = _x, Y = _y, Display = _display }, ct);
                        break;
                    case "wheel":
                        (int dy, int dx) = arg.Contains(' ') ? Swap(TwoInts(arg)) : (int.Parse(arg), 0);
                        await session.SendMouseAsync(new MouseEvent { Mask = 3, X = dx, Y = dy }, ct);
                        break;
                    case "type":
                        await session.SendKeyAsync(new KeyEvent { Seq = arg }, ct);
                        break;
                    case "key":
                        await session.SendKeyAsync(new KeyEvent { ControlKey = Ck(arg), Press = true }, ct);
                        break;
                    case "press":
                        // A positional key by scancode, the way the controller sends shortcut letters (so
                        // e.g. `mod ctrl down` + `press c` + `mod ctrl up` is Ctrl+C).
                        await session.SendKeyAsync(new KeyEvent { Chr = Scan(arg), Mode = KeyboardMode.KmMap, Press = true }, ct);
                        break;
                    case "mod":
                        (string name, string dir) = SplitLast(arg);
                        await session.SendKeyAsync(new KeyEvent { ControlKey = Ck(name), Down = dir != "up" }, ct);
                        break;
                    case "cad":
                        await session.SendKeyAsync(new KeyEvent { ControlKey = ControlKey.CkCtrlAltDel, Down = true }, ct);
                        break;
                    case "lock":
                        await session.SendKeyAsync(new KeyEvent { ControlKey = ControlKey.CkLockScreen, Down = true }, ct);
                        break;
                    case "switch":
                        _display = int.Parse(arg);
                        await session.SwitchDisplayAsync(_display, ct);
                        break;
                    case "refresh":
                        await session.RefreshVideoAsync(string.IsNullOrWhiteSpace(arg) ? _display : int.Parse(arg), ct);
                        break;
                    case "vdisplay":
                    {
                        string[] parts = arg.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length > 0 && parts[0] == "remove")
                        {
                            await session.RemoveVirtualDisplayAsync(int.Parse(parts[1]), ct);
                        }
                        else
                        {
                            Resolution? size = null;
                            if (parts.Length > 1)
                            {
                                (int w, int h) = Size(parts[1]);
                                size = new Resolution { Width = w, Height = h };
                            }

                            await session.AddVirtualDisplayAsync(size, ct);
                        }

                        break;
                    }
                    case "subscribe":
                    {
                        string[] parts = arg.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                        int[] set = parts.Length == 0 || parts[0] == "-" ? [] : [.. parts[0].Split(',').Select(int.Parse)];
                        int focus = parts.Length > 1 ? int.Parse(parts[1]) : set.Length > 0 ? set[0] : -1;
                        if (!session.HostSupportsMultiDisplay)
                        {
                            Console.WriteLine("*** This host does not stream several displays at once; asking anyway");
                        }

                        await session.SubscribeDisplaysAsync(set, focus, ct);
                        break;
                    }
                    case "res":
                    {
                        Resolution? wanted = null;
                        if (!arg.Equals("original", StringComparison.OrdinalIgnoreCase))
                        {
                            string[] scaleSplit = arg.Split('@');
                            string[] wh = scaleSplit[0].Split('x');
                            wanted = new Resolution { Width = int.Parse(wh[0]), Height = int.Parse(wh[1]), Scale = scaleSplit.Length > 1 ? double.Parse(scaleSplit[1], CultureInfo.InvariantCulture) : 0 };
                        }

                        await session.SetResolutionAsync(_display, wanted, ct);
                        break;
                    }
                    case "follow":
                        await FollowAsync(arg.Trim(), ct);
                        break;
                    case "resburst":
                    {
                        // :resburst WxH WxH ... -- requests back to back, the way a window being resized asks: the host
                        // should make the first and the last, not every one.
                        string[] sizes = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        foreach (string size in sizes)
                        {
                            (int w, int h) = Size(size);
                            await session.SetResolutionAsync(_display, new Resolution { Width = w, Height = h }, ct);
                        }

                        Console.WriteLine($"*** Sent {sizes.Length} resolution requests for display {_display} back to back");
                        break;
                    }
                    case "ls":
                    {
                        DeskPair.Protocol.Messages.FileDirectory listing =
                            await session.Files.ListDirectoryAsync(arg.Length == 0 ? "/" : arg, false, ct);
                        foreach (DeskPair.Protocol.Messages.FileEntry e in listing.Entries)
                        {
                            Console.WriteLine($"  {(e.Type == DeskPair.Protocol.Messages.FileType.FtDir ? "d" : "-")} {e.Size,12} {e.Name}");
                        }

                        break;
                    }

                    case "get":
                    {
                        string[] p = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                        int id = await session.Files.StartDownloadAsync(p[0], p[1], false, ct);
                        string outcome = await AwaitOutcomeAsync(id, ct);
                        Console.WriteLine($"*** get {p[0]} -> {p[1]}: {outcome}");
                        break;
                    }

                    case "put":
                    {
                        string[] p = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                        int id = await session.Files.StartUploadAsync(p[0], p[1], false, ct);
                        string outcome = await AwaitOutcomeAsync(id, ct);
                        Console.WriteLine($"*** put {p[0]} -> {p[1]}: {outcome}");
                        break;
                    }

                    case "getbg":
                    {
                        // Start a download but do not wait, so a later 'cancel <id>' can interrupt it mid-flight.
                        string[] p = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                        int id = await session.Files.StartDownloadAsync(p[0], p[1], false, ct);
                        Console.WriteLine($"*** download job {id} started {p[0]} -> {p[1]}");
                        break;
                    }

                    case "cancel":
                        await session.Files.CancelAsync(int.Parse(arg), ct);
                        Console.WriteLine($"*** cancel requested for job {arg}");
                        break;

                    case "clip":
                        clipboard.Push(arg);
                        Console.WriteLine($"*** clipboard to host: {arg}");
                        break;
                    case "paste":
                        Console.WriteLine($"*** pasted {await clipboard.PasteAsync(ct)} file(s)");
                        break;
                    case "copy":
                        // Stands in for the local user selecting these and pressing Ctrl+C.
                        clipboard.CopiedPaths = arg.Split(';', StringSplitOptions.RemoveEmptyEntries);
                        clipboard.Push($"copied {clipboard.CopiedPaths.Count} file(s)");
                        Console.WriteLine($"*** offering {clipboard.CopiedPaths.Count} file(s) to the host");
                        break;
                    case "sleep":
                        await Task.Delay(int.Parse(arg), ct);
                        break;
                    case "term":
                    {
                        int id = ++_terminals;
                        (int cols, int rows) = arg.Length > 0 ? Size(arg) : (80, 24);
                        await session.OpenTerminalAsync(id, cols, rows, ct);
                        Console.WriteLine($"*** term {id}: open requested ({cols}x{rows})");
                        break;
                    }

                    case "tsend":
                    {
                        (int id, string text) = IdAndRest(arg);
                        await session.SendTerminalInputAsync(id, Unescape(text), ct);
                        break;
                    }

                    case "tresize":
                    {
                        (int id, string size) = IdAndRest(arg);
                        (int cols, int rows) = Size(size);
                        await session.ResizeTerminalAsync(id, cols, rows, ct);
                        break;
                    }

                    case "tsig":
                    {
                        (int id, string which) = IdAndRest(arg);
                        await session.SignalTerminalAsync(id, which.ToLowerInvariant() switch
                        {
                            "term" => TerminalSignal.Types.Kind.Terminate,
                            "kill" => TerminalSignal.Types.Kind.Kill,
                            "hup" => TerminalSignal.Types.Kind.Hangup,
                            _ => TerminalSignal.Types.Kind.Interrupt,
                        }, ct);
                        break;
                    }

                    case "tclose":
                        await session.CloseTerminalAsync(int.Parse(arg), ct);
                        break;
                    case "expect":
                    {
                        (int id, string pattern) = IdAndRest(arg);
                        var regex = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.Singleline);
                        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
                        while (true)
                        {
                            System.Text.RegularExpressions.Match m = regex.Match(callbacks.TerminalText(id));
                            if (m.Success)
                            {
                                Console.WriteLine($"*** expect {id}: matched {Escape(m.Value)}");
                                break;
                            }

                            if (DateTime.UtcNow > deadline)
                            {
                                Console.WriteLine($"*** expect {id}: TIMEOUT waiting for /{pattern}/; output so far: {Escape(callbacks.TerminalText(id))}");
                                break;
                            }

                            await Task.Delay(50, ct);
                        }

                        break;
                    }

                    case "dump":
                        Console.WriteLine($"*** term {arg}: {Escape(callbacks.TerminalText(int.Parse(arg)))}");
                        break;
                    case "save":
                    {
                        // The next decoded frame goes to this file, so one connection can record what the
                        // host looked like at each step of a script (before locking, locked, unlocked). A
                        // display number after the path picks that display's next frame.
                        string[] parts = arg.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                        callbacks.SaveNext(parts[0], parts.Length > 1 ? int.Parse(parts[1]) : -1);
                        break;
                    }
                    default:
                        Console.Error.WriteLine($"*** unknown command: {verb}");
                        break;
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"*** command '{line}' failed: {e.Message}");
            }
        }

        // Waits for a transfer job to finish and reports its real terminal state, so a refused or cancelled
        // transfer is not printed as a success. The terminal progress snapshot is raised before the job's
        // completion is signalled, so subscribing before the wait catches it.
        private async Task<string> AwaitOutcomeAsync(int id, CancellationToken ct)
        {
            var seen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnProgress(DeskPair.Core.FileTransfer.TransferJobSnapshot s)
            {
                if (s.Id == id && s.State is DeskPair.Core.FileTransfer.TransferState.Done
                        or DeskPair.Core.FileTransfer.TransferState.Failed
                        or DeskPair.Core.FileTransfer.TransferState.Cancelled)
                {
                    seen.TrySetResult(s.Error is { Length: > 0 } ? $"{s.State} ({s.Error})" : s.State.ToString());
                }
            }

            session.Files.Progress += OnProgress;
            try
            {
                await session.Files.WaitForJobAsync(id, ct);
            }
            catch (Exception e) when (e is DeskPair.Core.FileTransfer.FileTransferFailedException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                return $"failed ({e.Message})";
            }
            finally
            {
                session.Files.Progress -= OnProgress;
            }

            return seen.Task.IsCompletedSuccessfully ? seen.Task.Result : "completed";
        }

        private static (int, int) TwoInts(string s)
        {
            string[] p = s.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
            return (int.Parse(p[0]), int.Parse(p[1]));
        }

        private static (int, int) Swap((int a, int b) t) => (t.b, t.a);

        private static (string, string) SplitLast(string s)
        {
            int i = s.LastIndexOf(' ');
            return i < 0 ? (s, string.Empty) : (s[..i], s[(i + 1)..].ToLowerInvariant());
        }

        // PC set-1 scancodes for the letter and digit keys, matching KeyMapper.Scancodes on the controller.
        private static uint Scan(string name) => name.Trim().ToLowerInvariant() switch
        {
            "a" => 0x1E, "b" => 0x30, "c" => 0x2E, "d" => 0x20, "e" => 0x12, "f" => 0x21, "g" => 0x22,
            "h" => 0x23, "i" => 0x17, "j" => 0x24, "k" => 0x25, "l" => 0x26, "m" => 0x32, "n" => 0x31,
            "o" => 0x18, "p" => 0x19, "q" => 0x10, "r" => 0x13, "s" => 0x1F, "t" => 0x14, "u" => 0x16,
            "v" => 0x2F, "w" => 0x11, "x" => 0x2D, "y" => 0x15, "z" => 0x2C,
            "0" => 0x0B, "1" => 0x02, "2" => 0x03, "3" => 0x04, "4" => 0x05,
            "5" => 0x06, "6" => 0x07, "7" => 0x08, "8" => 0x09, "9" => 0x0A,
            _ => throw new ArgumentException($"no scancode for '{name}'"),
        };

        private static int Button(string a) => a.Trim().ToLowerInvariant() switch
        {
            "r" or "right" => 2,
            "m" or "middle" => 4,
            _ => 1, // left
        };

        private static ControlKey Ck(string name) => name.Trim().ToLowerInvariant() switch
        {
            "return" or "enter" => ControlKey.CkReturn,
            "tab" => ControlKey.CkTab,
            "escape" or "esc" => ControlKey.CkEscape,
            "backspace" or "bksp" => ControlKey.CkBackspace,
            "delete" or "del" => ControlKey.CkDelete,
            "left" => ControlKey.CkLeftArrow,
            "right" => ControlKey.CkRightArrow,
            "up" => ControlKey.CkUpArrow,
            "down" => ControlKey.CkDownArrow,
            "home" => ControlKey.CkHome,
            "end" => ControlKey.CkEnd,
            "pageup" or "pgup" => ControlKey.CkPageUp,
            "pagedown" or "pgdn" => ControlKey.CkPageDown,
            "space" => ControlKey.CkSpace,
            "ctrl" or "control" => ControlKey.CkControl,
            "alt" or "option" => ControlKey.CkAlt,
            "shift" => ControlKey.CkShift,
            "meta" or "cmd" or "win" => ControlKey.CkMeta,
            "f1" => ControlKey.CkF1,
            "f2" => ControlKey.CkF2,
            "f3" => ControlKey.CkF3,
            "f4" => ControlKey.CkF4,
            "f5" => ControlKey.CkF5,
            "f6" => ControlKey.CkF6,
            "f7" => ControlKey.CkF7,
            "f8" => ControlKey.CkF8,
            "f9" => ControlKey.CkF9,
            "f10" => ControlKey.CkF10,
            "f11" => ControlKey.CkF11,
            "f12" => ControlKey.CkF12,
            _ => throw new ArgumentException($"unknown key '{name}'"),
        };
    }

    /// <summary>
    /// A clipboard the session drives so tests can observe both directions: what the host puts on the
    /// clipboard is printed (host -> controller), and <see cref="Push"/> feeds a controller-side change to the
    /// host (controller -> host).
    /// </summary>
    internal sealed class ScriptClipboard : IClipboard, IFilePromiseClipboard
    {
        private readonly Channel<IReadOnlyList<ClipboardItem>> _changes = Channel.CreateUnbounded<IReadOnlyList<ClipboardItem>>();
        private IReadOnlyList<ClipboardItem> _last = [];

        public ChannelReader<IReadOnlyList<ClipboardItem>> Changes => _changes.Reader;

        public ValueTask<IReadOnlyList<ClipboardItem>> ReadAsync(CancellationToken ct) => ValueTask.FromResult(_last);

        public ValueTask WriteAsync(IReadOnlyList<ClipboardItem> items, CancellationToken ct)
        {
            _last = items;
            ClipboardItem? text = items.FirstOrDefault(i => i.Format == ClipboardItemFormat.Text);
            if (text is not null)
            {
                Console.WriteLine($"*** clipboard from host: {Encoding.UTF8.GetString(text.Payload.Span)}");
            }

            return ValueTask.CompletedTask;
        }

        public void Push(string text) =>
            _changes.Writer.TryWrite([new ClipboardItem(ClipboardItemFormat.Text, Encoding.UTF8.GetBytes(text))]);

        // ---- file promises ----
        //
        // A real clipboard here would be the operating system's; this stands in for it so the whole path can
        // be driven from a script on a machine with no desktop at all.

        public bool CanPromiseFiles => true;

        /// <summary>What the host is offering, if anything.</summary>
        public FilePromiseListing? Promised { get; private set; }

        public IFilePromiseSource? PromiseSource { get; private set; }

        /// <summary>Paths a `copy` command said the local user had copied, for the other direction.</summary>
        public IReadOnlyList<string> CopiedPaths { get; set; } = [];

        public ValueTask<IReadOnlyList<string>> ReadCopiedFilePathsAsync(CancellationToken ct) =>
            ValueTask.FromResult(CopiedPaths);

        public ValueTask WriteWithPromiseAsync(
            IReadOnlyList<ClipboardItem> items,
            FilePromiseListing listing,
            IFilePromiseSource source,
            CancellationToken ct)
        {
            Promised = listing;
            PromiseSource = source;
            Console.WriteLine($"*** files promised by host ({listing.Entries.Count} item(s), root '{listing.RemoteRoot}'):");
            foreach (FilePromiseEntry entry in listing.Entries)
            {
                Console.WriteLine($"      [{entry.Id}] {entry.RelativePath}  {(entry.IsDirectory ? "<dir>" : entry.Size + " bytes")}");
            }

            return WriteAsync(items, ct);
        }

        /// <summary>Pastes: fetches everything promised, which is what a file manager does on Ctrl+V.</summary>
        public async Task<int> PasteAsync(CancellationToken ct)
        {
            if (Promised is null || PromiseSource is null)
            {
                Console.WriteLine("*** nothing is promised");
                return 0;
            }

            IReadOnlyList<FilePromiseEntry> files = await PromiseSource.ResolveAsync(ct);
            int fetched = 0;
            foreach (FilePromiseEntry entry in files)
            {
                string destination = PromiseSource.StagedPathFor(entry.Id);
                await PromiseSource.FetchAsync(entry.Id, destination, null, ct);
                var info = new FileInfo(destination);
                Console.WriteLine($"*** pasted {entry.RelativePath} -> {destination} ({(info.Exists ? info.Length : -1)} bytes)");
                fetched++;
            }

            return fetched;
        }

        public ValueTask DisposeAsync()
        {
            _changes.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
