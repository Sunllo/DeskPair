// The Linux daemon's pieces, exercised on a real machine before the daemon exists.
//
//   sudo DeskPair.Tools.LinuxHarness drm-grab --out /tmp/grab [--card /dev/dri/card1] [--drop-uid 1000] [--seconds 5]
//
// The parent stays root and does only what the daemon will do: open the card, serve the socketpair.
// It starts itself again as the child, which gives up root and reads the screen through the product's
// own IScreenCapturer -- DrmCaptureChannel, DrmDisplayEnumerator, DrmScreenCapturer -- exactly as the
// engine will. Every decoded frame prints one line of numbers, because "it looks right" is not evidence:
//
//   frame #12 1280x800 sha256=ab12cd34 nonBlack=98.7% diffFromPrev=0.0000 wait=16ms copy=1.2ms
//
// A stale buffer is diffFromPrev staying 0.0000 across a change on screen; a wrongly read format is
// nonBlack at 0.0; the wrong screen is two states with one sha256. The first and last frames are written
// raw (.bgra + .txt) for a human to convert and look at.
//
//   DeskPair.Tools.LinuxHarness portal [--store DIR] [--hold 5]
//
// The Wayland portal session, run as the signed-in user with their session bus (DBUS_SESSION_BUS_ADDRESS): asks for
// the monitors, keyboard and pointer, prints what came back and how long Start took, opens the PipeWire remote and
// holds the session for a while. The restore token is kept under --store, so a second run should start without the
// dialog the first one showed -- Start in a few milliseconds rather than however long the person took.
//
//   DeskPair.Tools.LinuxHarness portal-grab --out PREFIX [--store DIR] [--seconds 5] [--copy] [--expect-color RRGGBB]
//
// The same session, then the monitors through the product's own PortalCapture and PortalScreenCapturer (the shim
// found beside the executable or through SUNLLO_WAYLANDSHIM_PATH), with one line of numbers per frame as drm-grab
// prints them. --copy forces the copying path instead of handing PipeWire's buffers on. --expect-color fails the run
// (exit 8) unless that is the picture's most common colour: tools/wayland-ci paints a headless sway one colour, so a
// picture of anything else is not the desktop.
//
//   DeskPair.Tools.LinuxHarness pipewire-grab --node ID --out PREFIX [--width 1280 --height 800] [--seconds 5] [--copy] [--expect-color RRGGBB]
//
// A PipeWire video node read with no portal in the way: the harness connects to the PipeWire daemon's own socket --
// what OpenPipeWireRemote hands out is the same kind of connection, restricted to the shared nodes -- and reads the
// node through the product's shim and PortalScreenCapturer, printing what portal-grab prints. tools/wayland-ci runs it
// against a GStreamer test picture in a container, which needs no desktop and nobody to press "Share".
//
//   DeskPair.Tools.LinuxHarness portal-input --out PREFIX [--store DIR] [--x 500 --y 400]
//
// Input through the portal, watched through the portal: clicks at (x, y) -- a terminal is expected there -- types
// text as keysyms (with characters no US key has), types `ls` as positional evdev codes, fills the terminal and
// scrolls it back with the wheel. The picture after each step is saved raw, as portal-grab saves it.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Linux.Capture;
using DeskPair.Platform.Linux.Capture.Drm;
using DeskPair.Platform.Linux.Native;
using DeskPair.Platform.Linux.Wayland;
using DeskPair.Platform.Abstractions.Security;
using Microsoft.Extensions.Logging;

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: drm-grab --out PREFIX [--card /dev/dri/card1] [--drop-uid 1000] [--seconds 5] | portal [--store DIR] [--hold 5]");
    return 1;
}

using ILoggerFactory logs = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Information));
string Arg(string name, string fallback) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }

switch (args[0])
{
    case "drm-grab":
        return Parent();
    case "drm-child":
        return await ChildAsync();
    case "uinput-click":
        return UinputClick();
    case "portal":
        return await PortalAsync();
    case "pipewire-grab":
        return await PipeWireGrabAsync();
    case "portal-grab":
        return await PortalGrabAsync();
    case "portal-input":
        return await PortalInputAsync();
    case "portal-cursor":
        return await PortalCursorAsync();
    case "portal-ask":
        return await PortalAskAsync();
    default:
        Console.Error.WriteLine($"unknown verb {args[0]}");
        return 1;
}

int Parent()
{
    string card = Arg("--card", "/dev/dri/card1");
    ILogger log = logs.CreateLogger("daemon");
    (int mine, int theirs) = UnixSocketMsg.Pair();
    UnixSocketMsg.Inherit(theirs);

    // Framework-dependent runs are `dotnet <dll>`; a single-file publish is the executable itself.
    string exe = Environment.ProcessPath!;
    string self = Path.Combine(AppContext.BaseDirectory, "DeskPair.Tools.LinuxHarness.dll");
    string tail = string.Join(' ', args.Skip(1).Select(a => a.Contains(' ') ? '"' + a + '"' : a));
    string arguments = Path.GetFileName(exe) == "dotnet" ? $"\"{self}\" drm-child --fd {theirs} {tail}" : $"drm-child --fd {theirs} {tail}";
    log.LogInformation("Starting the child: {Exe} {Args}", exe, arguments);

    using var reader = new DrmScanoutReader(card, log);
    log.LogInformation("Card {Card}, driver {Driver}", card, reader.Driver);
    using Process child = Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = false })!;
    _ = UnixSocketMsg.close(theirs);

    DrmCaptureServer.Run(mine, reader, log, CancellationToken.None);
    child.WaitForExit(10_000);
    log.LogInformation("Child exited {Code}", child.ExitCode);
    return child.ExitCode;
}

async Task<int> ChildAsync()
{
    int fd = int.Parse(Arg("--fd", "-1"));
    string outPrefix = Arg("--out", "/tmp/grab");
    int dropUid = int.Parse(Arg("--drop-uid", "0"));
    int seconds = int.Parse(Arg("--seconds", "5"));
    ILogger log = logs.CreateLogger("engine");

    if (dropUid > 0)
    {
        if (Native.setresgid((uint)dropUid, (uint)dropUid, (uint)dropUid) != 0 || Native.setresuid((uint)dropUid, (uint)dropUid, (uint)dropUid) != 0)
        {
            log.LogError("Could not drop to uid {Uid}: errno {Errno}", dropUid, Marshal.GetLastPInvokeError());
            return 2;
        }
    }

    log.LogInformation("Child running as euid {Euid} on fd {Fd}", Native.geteuid(), fd);

    using var channel = new DrmCaptureChannel(fd);
    var displays = new DrmDisplayEnumerator(channel, "vmwgfx");
    IReadOnlyList<DisplayDescriptor> list = displays.GetDisplays();
    if (list.Count == 0)
    {
        log.LogError("No display: the daemon reports no scanout (is the screen off? wake it with input first)");
        return 3;
    }

    log.LogInformation("Display {Name} {Width}x{Height}", list[0].Name, list[0].Width, list[0].Height);
    IScreenCapturer capturer;
    try
    {
        capturer = new DrmScreenCapturerFactory(channel, logs).Create(list[0], preferGpu: false);
    }
    catch (DrmCaptureUnsupportedException e)
    {
        log.LogError("{Message}", e.Message);
        return 4;
    }

    var stopwatch = Stopwatch.StartNew();
    byte[]? previous = null;
    int frames = 0;
    byte[]? first = null;
    byte[]? last = null;
    (int W, int H) size = (0, 0);
    while (stopwatch.Elapsed < TimeSpan.FromSeconds(seconds))
    {
        CaptureResult r = await capturer.AcquireFrameAsync(TimeSpan.FromMilliseconds(500), CancellationToken.None);
        if (r.Status == CaptureStatus.Error)
        {
            log.LogError("Capture error: {Error}", r.Error?.Message);
            return 5;
        }

        if (r.Status == CaptureStatus.DesktopSwitched)
        {
            log.LogInformation("Desktop switched (new generation); a real engine would rebuild the capturer here");
            return 6;
        }

        if (r.Status != CaptureStatus.Frame)
        {
            continue;
        }

        frames++;
        byte[] pixels = r.Frame.Cpu.ToArray();
        size = (r.Frame.Width, r.Frame.Height);
        first ??= pixels;
        last = pixels;
        (double WaitMs, double ReadbackMs) timing = capturer.LastFrameTiming;
        Console.WriteLine(
            $"frame #{frames} {r.Frame.Width}x{r.Frame.Height} sha256={Convert.ToHexString(SHA256.HashData(pixels))[..8].ToLowerInvariant()} " +
            $"nonBlack={NonBlack(pixels):P1} diffFromPrev={(previous is null ? 0 : Diff(previous, pixels)):F4} wait={timing.WaitMs:F0}ms copy={timing.ReadbackMs:F1}ms");
        previous = pixels;
    }

    await capturer.DisposeAsync();
    if (first is not null)
    {
        Save(outPrefix + "-first", first, size);
        Save(outPrefix + "-last", last!, size);
    }

    log.LogInformation("{Frames} frames in {Seconds}s", frames, seconds);
    return frames > 0 ? 0 : 7;
}

async Task<int> PortalAsync()
{
    string store = Arg("--store", Path.Combine(Path.GetTempPath(), "deskpair-portal"));
    int hold = int.Parse(Arg("--hold", "5"));
    ILogger log = logs.CreateLogger("portal");
    var tokens = new PortalTokens(new DirectorySecrets(store), Native.geteuid(), log);
    log.LogInformation("Saved token before: {Token}", await tokens.LoadAsync() is { } before ? before[..Math.Min(8, before.Length)] + "..." : "none");

    PortalSession session;
    try
    {
        session = await PortalSession.OpenAsync(tokens, log);
    }
    catch (PortalException e)
    {
        log.LogError("Portal: {Reason}: {Message}", e.Reason, e.Message);
        return 10 + (int)e.Reason;
    }

    await using (session)
    {
        Console.WriteLine($"START took={session.StartTook.TotalMilliseconds:F0}ms offeredToken={session.OfferedRestoreToken} devices={session.Devices} clipboard={session.ClipboardEnabled}");
        foreach (PortalStream stream in session.Streams)
        {
            Console.WriteLine($"STREAM node={stream.NodeId} id={stream.Id ?? "-"} pos={(stream.HasPosition ? $"{stream.X},{stream.Y}" : "-")} size={stream.Width}x{stream.Height} type={stream.SourceType} mapping={stream.MappingId ?? "-"}");
        }

        int remotes = int.Parse(Arg("--remotes", "1"));
        for (int i = 0; i < remotes; i++)
        {
            var took = Stopwatch.StartNew();
            using Microsoft.Win32.SafeHandles.SafeFileHandle remote = await session.OpenPipeWireRemoteAsync();
            int fd = (int)remote.DangerousGetHandle();
            string target = new FileInfo($"/proc/self/fd/{fd}").LinkTarget ?? "?";
            Console.WriteLine($"PIPEWIRE fd={fd} -> {target} in {took.ElapsedMilliseconds} ms");
        }

        Task closed = session.Closed;
        Task which = await Task.WhenAny(closed, Task.Delay(TimeSpan.FromSeconds(hold)));
        Console.WriteLine(which == closed ? "CLOSED by the portal" : $"HELD {hold}s");
    }

    log.LogInformation("Saved token after: {Token}", await tokens.LoadAsync() is { } after ? after[..Math.Min(8, after.Length)] + "..." : "none");
    return 0;
}

async Task<int> PortalAskAsync()
{
    // What the settings page's "Allow..." does: ask now, whatever is remembered, and remember the answer.
    var store = new DirectorySecrets(Arg("--store", Path.Combine(Path.GetTempPath(), "deskpair-portal")));
    ILogger log = logs.CreateLogger("portal");
    Console.WriteLine($"STATE before: {await PortalPermission.StateAsync(store, log)}");
    var took = Stopwatch.StartNew();
    (PortalAskOutcome outcome, string? detail) = await PortalPermission.AskAsync(store, logs);
    Console.WriteLine($"ASKED: {outcome} after {took.ElapsedMilliseconds} ms{(detail is null ? string.Empty : ": " + detail)}");
    Console.WriteLine($"STATE after: {await PortalPermission.StateAsync(store, log)}");
    return 0;
}

async Task<int> PortalCursorAsync()
{
    // The pointer through the engine's own path (PortalHost as the ICursorProvider), while it is moved around.
    await using var host = new PortalHost(new DirectorySecrets(Arg("--store", Path.Combine(Path.GetTempPath(), "deskpair-portal"))), logs);
    string? why = await host.OpenAsync(notice => Console.WriteLine($"NOTICE {notice}"), CancellationToken.None);
    if (why is not null)
    {
        Console.WriteLine($"OPEN failed: {why}");
        return 2;
    }

    DisplayDescriptor display = host.GetDisplays()[0];
    var screen = new DeskPair.Platform.Abstractions.Input.VirtualScreenRect(0, 0, display.Width, display.Height);
    IScreenCapturer capturer = host.Create(display, preferGpu: false);
    if (args.Contains("--watch"))
    {
        // Moved by something else (a person, uinput): only watch.
        var watching = Stopwatch.StartNew();
        while (watching.Elapsed < TimeSpan.FromSeconds(int.Parse(Arg("--seconds", "15"))))
        {
            _ = await capturer.AcquireFrameAsync(TimeSpan.FromMilliseconds(250), CancellationToken.None);
            ulong seen = host.GetCurrentCursorId();
            Console.WriteLine($"WATCH {watching.ElapsedMilliseconds,6} ms: position {host.GetCursorPosition()?.ToString() ?? "none"}, shape {seen:x}");
        }

        await capturer.DisposeAsync();
        return 0;
    }

    int[][] points = [[100, 100], [500, 400], [1100, 700], [640, 60], [300, 300]];
    foreach (int[] point in points)
    {
        host.InjectMouse(new DeskPair.Platform.Abstractions.Input.MouseInput(DeskPair.Platform.Abstractions.Input.MouseAction.Move, DeskPair.Platform.Abstractions.Input.MouseButtons.None, point[0], point[1], 0), screen);
        var until = Stopwatch.StartNew();
        while (until.ElapsedMilliseconds < 700)
        {
            _ = await capturer.AcquireFrameAsync(TimeSpan.FromMilliseconds(100), CancellationToken.None);
        }

        ulong id = host.GetCurrentCursorId();
        var shape = host.GetCursorImage(id);
        Console.WriteLine($"CURSOR after move to {point[0]},{point[1]}: position {host.GetCursorPosition()?.ToString() ?? "none"}, shape {id:x} {shape?.Width}x{shape?.Height} hot {shape?.HotX},{shape?.HotY}");
    }

    await capturer.DisposeAsync();
    await host.CloseAsync();
    return 0;
}

async Task<int> PortalInputAsync()
{
    string store = Arg("--store", Path.Combine(Path.GetTempPath(), "deskpair-portal"));
    string outPrefix = Arg("--out", "/tmp/portal-input");
    int x = int.Parse(Arg("--x", "500"));
    int y = int.Parse(Arg("--y", "400"));
    ILogger log = logs.CreateLogger("portal");
    var tokens = new PortalTokens(new DirectorySecrets(store), Native.geteuid(), log);

    PortalSession session;
    try
    {
        session = await PortalSession.OpenAsync(tokens, log);
    }
    catch (PortalException e)
    {
        log.LogError("Portal: {Reason}: {Message}", e.Reason, e.Message);
        return 10 + (int)e.Reason;
    }

    await using (session)
    {
        PortalCapture capture = await PortalCapture.OpenAsync(session, logs);
        IReadOnlyList<DisplayDescriptor> displays = capture.GetDisplays();
        var screen = new DeskPair.Platform.Abstractions.Input.VirtualScreenRect(0, 0, displays[0].Width, displays[0].Height);
        IScreenCapturer capturer = capture.Create(displays[0], preferGpu: false);
        await using var injector = new PortalInputInjector(session, capture, logs.CreateLogger("input"));
        Console.WriteLine($"INPUT capabilities {injector.Capabilities}; display {displays[0].Name} {displays[0].Width}x{displays[0].Height} scale {displays[0].Scale}");

        byte[]? latest = null;
        (int W, int H) size = (0, 0);
        async Task Settle(int ms)
        {
            // Keep taking pictures for a while; the last one is the screen after the step.
            var until = Stopwatch.StartNew();
            while (until.ElapsedMilliseconds < ms)
            {
                CaptureResult r = await capturer.AcquireFrameAsync(TimeSpan.FromMilliseconds(100), CancellationToken.None);
                if (r.Status == CaptureStatus.Frame)
                {
                    latest = new byte[r.Frame.Width * 4 * r.Frame.Height];
                    for (int row = 0; row < r.Frame.Height; row++)
                    {
                        r.Frame.Cpu.Span.Slice(row * r.Frame.Stride, r.Frame.Width * 4).CopyTo(latest.AsSpan(row * r.Frame.Width * 4));
                    }

                    size = (r.Frame.Width, r.Frame.Height);
                }
            }
        }

        void Tap(DeskPair.Platform.Abstractions.Input.KeyInput key)
        {
            injector.InjectKey(key with { Down = true });
            injector.InjectKey(key with { Down = false });
        }

        var enter = new DeskPair.Platform.Abstractions.Input.KeyInput(DeskPair.Platform.Abstractions.Input.KeyInputMode.Translate, true, 0, DeskPair.Platform.Abstractions.Input.ControlKey.Return, null);
        await Settle(500);

        injector.InjectMouse(new DeskPair.Platform.Abstractions.Input.MouseInput(DeskPair.Platform.Abstractions.Input.MouseAction.Move, DeskPair.Platform.Abstractions.Input.MouseButtons.None, x, y, 0), screen);
        injector.InjectMouse(new DeskPair.Platform.Abstractions.Input.MouseInput(DeskPair.Platform.Abstractions.Input.MouseAction.Down, DeskPair.Platform.Abstractions.Input.MouseButtons.Left, x, y, 0), screen);
        injector.InjectMouse(new DeskPair.Platform.Abstractions.Input.MouseInput(DeskPair.Platform.Abstractions.Input.MouseAction.Up, DeskPair.Platform.Abstractions.Input.MouseButtons.Left, x, y, 0), screen);
        await Settle(500);

        // Text as keysyms, then `ls` as positional evdev codes (l = 38, s = 31, Enter = 28).
        injector.InjectKey(new DeskPair.Platform.Abstractions.Input.KeyInput(DeskPair.Platform.Abstractions.Input.KeyInputMode.Translate, true, 0, DeskPair.Platform.Abstractions.Input.ControlKey.None, "echo 'portal typed: The Quick Brown Fox 1234567890 @#$%^&*()_+{}|:<>?~ done'"));
        Tap(enter);
        foreach (uint code in new uint[] { 38, 31, 28 })
        {
            Tap(new DeskPair.Platform.Abstractions.Input.KeyInput(DeskPair.Platform.Abstractions.Input.KeyInputMode.Map, true, code, DeskPair.Platform.Abstractions.Input.ControlKey.None, null));
        }

        await Settle(1500);
        if (latest is not null)
        {
            Save(outPrefix + "-typed", latest, size);
        }

        injector.InjectKey(new DeskPair.Platform.Abstractions.Input.KeyInput(DeskPair.Platform.Abstractions.Input.KeyInputMode.Translate, true, 0, DeskPair.Platform.Abstractions.Input.ControlKey.None, "seq 1 80\n"));
        await Settle(1000);
        injector.InjectMouse(new DeskPair.Platform.Abstractions.Input.MouseInput(DeskPair.Platform.Abstractions.Input.MouseAction.Move, DeskPair.Platform.Abstractions.Input.MouseButtons.None, x, y, 0), screen);
        injector.InjectMouse(new DeskPair.Platform.Abstractions.Input.MouseInput(DeskPair.Platform.Abstractions.Input.MouseAction.Wheel, DeskPair.Platform.Abstractions.Input.MouseButtons.None, 0, 0, 5), screen);
        await Settle(1500);
        if (latest is not null)
        {
            Save(outPrefix + "-scrolled", latest, size);
        }

        // Back to the bottom and a clean prompt for whoever looks next.
        injector.InjectMouse(new DeskPair.Platform.Abstractions.Input.MouseInput(DeskPair.Platform.Abstractions.Input.MouseAction.Wheel, DeskPair.Platform.Abstractions.Input.MouseButtons.None, 0, 0, -30), screen);
        injector.InjectKey(new DeskPair.Platform.Abstractions.Input.KeyInput(DeskPair.Platform.Abstractions.Input.KeyInputMode.Translate, true, 0, DeskPair.Platform.Abstractions.Input.ControlKey.None, "clear\n"));
        await Settle(800);
        await capturer.DisposeAsync();
        Console.WriteLine("INPUT done");
        return 0;
    }
}

async Task<int> PortalGrabAsync()
{
    string store = Arg("--store", Path.Combine(Path.GetTempPath(), "deskpair-portal"));
    string outPrefix = Arg("--out", "/tmp/portal-grab");
    int seconds = int.Parse(Arg("--seconds", "5"));
    ILogger log = logs.CreateLogger("portal");
    var tokens = new PortalTokens(new DirectorySecrets(store), Native.geteuid(), log);

    PortalSession session;
    try
    {
        session = await PortalSession.OpenAsync(tokens, log);
    }
    catch (PortalException e)
    {
        log.LogError("Portal: {Reason}: {Message}", e.Reason, e.Message);
        return 10 + (int)e.Reason;
    }

    await using (session)
    {
        Console.WriteLine($"remote control: {session.RemoteControl}, devices {session.Devices}, restore token offered: {session.OfferedRestoreToken}");
        PortalCapture capture = await PortalCapture.OpenAsync(session, logs);
        DisplayDescriptor display = capture.GetDisplays()[0];
        return await GrabAsync(capture.Create(display, preferGpu: false), outPrefix, seconds, log);
    }
}

async Task<int> PipeWireGrabAsync()
{
    uint node = uint.Parse(Arg("--node", "0"), System.Globalization.CultureInfo.InvariantCulture);
    int width = int.Parse(Arg("--width", "1280"), System.Globalization.CultureInfo.InvariantCulture);
    int height = int.Parse(Arg("--height", "800"), System.Globalization.CultureInfo.InvariantCulture);
    string outPrefix = Arg("--out", "/tmp/pipewire-grab");
    int seconds = int.Parse(Arg("--seconds", "5"), System.Globalization.CultureInfo.InvariantCulture);
    ILogger log = logs.CreateLogger("pipewire");

    string runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? $"/run/user/{Native.geteuid()}";
    string path = Path.Combine(runtime, Environment.GetEnvironmentVariable("PIPEWIRE_REMOTE") ?? "pipewire-0");
    IPipeWireStream stream;
    using (var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified))
    {
        socket.Connect(new System.Net.Sockets.UnixDomainSocketEndPoint(path));
        // The shim takes a copy of the descriptor, so this one can close with the socket.
        stream = ShimPipeWireStream.Open(socket.SafeHandle, node);
    }

    log.LogInformation("Reading node {Node} through {Path}", node, path);
    var display = new DisplayDescriptor(0, $"pipewire@{node}", 0, 0, width, height, 1.0, FrameRotation.None, true, node);
    return await GrabAsync(new PortalScreenCapturer(stream, display, logs.CreateLogger<PortalScreenCapturer>()), outPrefix, seconds, log);
}

async Task<int> GrabAsync(IScreenCapturer capturer, string outPrefix, int seconds, ILogger log)
{
    if (args.Contains("--copy"))
    {
        capturer.ForceFallbackPath();
    }

    var stopwatch = Stopwatch.StartNew();
    byte[]? previous = null;
    byte[]? first = null;
    byte[]? last = null;
    int frames = 0;
    int timeouts = 0;
    (int W, int H) size = (0, 0);
    while (stopwatch.Elapsed < TimeSpan.FromSeconds(seconds))
    {
        CaptureResult r = await capturer.AcquireFrameAsync(TimeSpan.FromMilliseconds(500), CancellationToken.None);
        if (r.Status == CaptureStatus.Timeout)
        {
            timeouts++;
            continue;
        }

        if (r.Status != CaptureStatus.Frame)
        {
            log.LogError("Capture {Status}: {Error}", r.Status, r.Error?.Message);
            break;
        }

        frames++;
        // Rows copied out tight, so the numbers compare across strides and the saved file is plain.
        byte[] pixels = new byte[r.Frame.Width * 4 * r.Frame.Height];
        for (int y = 0; y < r.Frame.Height; y++)
        {
            r.Frame.Cpu.Span.Slice(y * r.Frame.Stride, r.Frame.Width * 4).CopyTo(pixels.AsSpan(y * r.Frame.Width * 4));
        }

        size = (r.Frame.Width, r.Frame.Height);
        first ??= pixels;
        last = pixels;
        (double WaitMs, double ReadbackMs) timing = capturer.LastFrameTiming;
        Console.WriteLine(
            $"frame #{frames} {r.Frame.Width}x{r.Frame.Height} {r.Frame.Format} stride={r.Frame.Stride} sha256={Convert.ToHexString(SHA256.HashData(pixels))[..8].ToLowerInvariant()} " +
            $"nonBlack={NonBlack(pixels):P1} diffFromPrev={(previous is null ? 0 : Diff(previous, pixels)):F4} wait={timing.WaitMs:F0}ms copy={timing.ReadbackMs:F2}ms");
        previous = pixels;
    }

    await capturer.DisposeAsync();
    if (first is not null)
    {
        Save(outPrefix + "-first", first, size);
        Save(outPrefix + "-last", last!, size);
    }

    log.LogInformation("{Frames} frames and {Timeouts} quiet half-seconds in {Seconds}s", frames, timeouts, seconds);
    if (frames == 0)
    {
        return 7;
    }

    string? expected = Arg("--expect-color", string.Empty) is { Length: 6 } hex ? hex.ToLowerInvariant() : null;
    if (expected is not null)
    {
        string seen = DominantColour(last!);
        Console.WriteLine($"most common colour #{seen}, expected #{expected}");
        if (seen != expected)
        {
            return 8;
        }
    }

    return 0;
}

// The product's own virtual devices and injector, clicking where the login screen's user tile was seen,
// then typing one character -- with a scanout grab before and after so the change is a number.
//   sudo ... uinput-click --x 640 --y 350 --text a
int UinputClick()
{
    int x = int.Parse(Arg("--x", "640"));
    int y = int.Parse(Arg("--y", "350"));
    string text = Arg("--text", "a");
    ILogger log = logs.CreateLogger("uinput");
    using DeskPair.Platform.Linux.Input.UinputDevice device = DeskPair.Platform.Linux.Input.UinputDevice.Create(log);
    Thread.Sleep(2000); // udev and the compositor have to see the devices first
    var injector = new DeskPair.Platform.Linux.Input.UinputInputInjector(device.Keyboard, device.Pointer, device.Relative, log);
    var screen = new DeskPair.Platform.Abstractions.Input.VirtualScreenRect(0, 0, 1280, 800);
    injector.Nudge();
    Thread.Sleep(1500);
    injector.InjectMouse(new DeskPair.Platform.Abstractions.Input.MouseInput(DeskPair.Platform.Abstractions.Input.MouseAction.Move, DeskPair.Platform.Abstractions.Input.MouseButtons.None, x, y, 0), screen);
    Thread.Sleep(300);
    injector.InjectMouse(new DeskPair.Platform.Abstractions.Input.MouseInput(DeskPair.Platform.Abstractions.Input.MouseAction.Down, DeskPair.Platform.Abstractions.Input.MouseButtons.Left, x, y, 0), screen);
    Thread.Sleep(80);
    injector.InjectMouse(new DeskPair.Platform.Abstractions.Input.MouseInput(DeskPair.Platform.Abstractions.Input.MouseAction.Up, DeskPair.Platform.Abstractions.Input.MouseButtons.Left, x, y, 0), screen);
    Thread.Sleep(1500);
    injector.InjectKey(new DeskPair.Platform.Abstractions.Input.KeyInput(DeskPair.Platform.Abstractions.Input.KeyInputMode.Translate, true, 0, DeskPair.Platform.Abstractions.Input.ControlKey.None, text));
    Thread.Sleep(1500);
    if (Arg("--then", "") == "escape")
    {
        // Undo, for a test that wants the screen back as it was -- after it has been measured.
        injector.InjectKey(new DeskPair.Platform.Abstractions.Input.KeyInput(DeskPair.Platform.Abstractions.Input.KeyInputMode.Map, true, 0, DeskPair.Platform.Abstractions.Input.ControlKey.Escape, null));
        injector.InjectKey(new DeskPair.Platform.Abstractions.Input.KeyInput(DeskPair.Platform.Abstractions.Input.KeyInputMode.Map, false, 0, DeskPair.Platform.Abstractions.Input.ControlKey.Escape, null));
    }

    log.LogInformation("Clicked ({X},{Y}), typed {Text}", x, y, text);
    return 0;
}

// The most common colour of a BGRx picture, as RRGGBB, from every 16th pixel.
static string DominantColour(byte[] px)
{
    var counts = new Dictionary<int, int>();
    for (int i = 0; i + 3 < px.Length; i += 64)
    {
        int rgb = (px[i + 2] << 16) | (px[i + 1] << 8) | px[i];
        counts[rgb] = counts.GetValueOrDefault(rgb) + 1;
    }

    int top = counts.MaxBy(pair => pair.Value).Key;
    return top.ToString("x6", System.Globalization.CultureInfo.InvariantCulture);
}

static double NonBlack(byte[] px)
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

static double Diff(byte[] a, byte[] b)
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

static void Save(string prefix, byte[] pixels, (int W, int H) size)
{
    File.WriteAllBytes(prefix + ".bgra", pixels);
    File.WriteAllText(prefix + ".txt", $"{size.W} {size.H} BGRX");
}

// One file per key, owner-only: the shape of the engine's FileSecretStore, which lives in Core and so is out of reach here.
sealed class DirectorySecrets : ISecretStore
{
    private readonly string _directory;

    public DirectorySecrets(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    public async ValueTask<byte[]?> GetAsync(string key, CancellationToken ct = default) =>
        File.Exists(PathFor(key)) ? await File.ReadAllBytesAsync(PathFor(key), ct) : null;

    public async ValueTask SetAsync(string key, ReadOnlyMemory<byte> value, CancellationToken ct = default)
    {
        await File.WriteAllBytesAsync(PathFor(key), value.ToArray(), ct);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(PathFor(key), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public ValueTask RemoveAsync(string key, CancellationToken ct = default)
    {
        File.Delete(PathFor(key));
        return ValueTask.CompletedTask;
    }

    private string PathFor(string key) => Path.Combine(_directory, key + ".bin");
}

static partial class Native
{
    [DllImport("libc", SetLastError = true)] public static extern int setresuid(uint r, uint e, uint s);
    [DllImport("libc", SetLastError = true)] public static extern int setresgid(uint r, uint e, uint s);
    [DllImport("libc")] public static extern uint geteuid();
}
