using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Abstractions.Cursor;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace DeskPair.Platform.Linux.Wayland;

/// <summary>
/// The monitors a portal session shares, as displays the engine can list and capture.
///
/// The portal describes each monitor in the compositor's logical coordinates, and the encoder needs pixels; the
/// two differ whenever the desktop is scaled. So each stream is opened once at the start to learn its pixel size,
/// and a capturer that later sees a different size (the monitor changed mode) reports it back here, the list is
/// updated and <see cref="DisplaysChanged"/> raised, which the media module already answers by rebuilding.
///
/// Every capturer gets its own PipeWire connection: a connection is one socket speaking one client's protocol, so
/// two streams cannot share it, and the portal hands out as many as it is asked for.
///
/// Positions are pixels too, because the engine's virtual screen is: a viewer's click arrives in pixels of the
/// picture and gets the display's X and Y added. Portal positions are logical, and adding a logical offset to a
/// pixel coordinate puts a scaled monitor's right half on top of its neighbour. So the logical layout is laid out
/// at the largest scale of any monitor: every monitor's pixel rectangle then sits inside its own logical rectangle
/// scaled up, no two overlap, and <see cref="PortalInputInjector"/> can say which monitor a point is on and where
/// on it, in the logical units the portal wants back.
/// </summary>
public sealed class PortalCapture : IDisplayEnumerator, IScreenCapturerFactory
{
    /// <summary>How long a stream may take to show its first picture while its size is being learned.</summary>
    private const int ProbeTimeoutMs = 3000;

    private readonly IPortalSession _session;
    private readonly ILoggerFactory _logs;
    private readonly ILogger _log;
    private readonly object _lock = new();
    private readonly (int Width, int Height)[] _pixels;
    private readonly List<PortalScreenCapturer> _live = [];
    private List<DisplayDescriptor> _displays;

    private PortalCapture(IPortalSession session, ILoggerFactory logs, (int Width, int Height)[] pixels)
    {
        _session = session;
        _logs = logs;
        _log = logs.CreateLogger<PortalCapture>();
        _pixels = pixels;
        _displays = Describe(session.Streams, pixels);
    }

    public event EventHandler? DisplaysChanged;

    /// <summary>Lists the session's monitors with their pixel sizes, opening each stream once to learn them.</summary>
    /// <exception cref="IOException">The Wayland shim cannot be loaded, or a stream cannot be opened.</exception>
    internal static async Task<PortalCapture> OpenAsync(IPortalSession session, ILoggerFactory logs, CancellationToken ct = default)
    {
        ILogger log = logs.CreateLogger<PortalCapture>();
        var pixels = new List<(int Width, int Height)>();
        foreach (PortalStream stream in session.Streams)
        {
            (int Width, int Height)? size = await ProbeAsync(session, stream, ct).ConfigureAwait(false);
            if (size is null)
            {
                log.LogWarning("Node {Node} showed no picture in {Ms} ms; assuming its logical size {Width}x{Height}",
                    stream.NodeId, ProbeTimeoutMs, stream.Width, stream.Height);
            }

            pixels.Add(size ?? (stream.Width, stream.Height));
        }

        var capture = new PortalCapture(session, logs, [.. pixels]);
        IReadOnlyList<DisplayDescriptor> displays = capture.GetDisplays();
        log.LogInformation("Portal displays: {Displays}", string.Join("; ", displays.Select(d => $"{d.Name} {d.Width}x{d.Height} at {d.X},{d.Y} scale {d.Scale:0.##}")));
        if (displays.Count < session.Streams.Count)
        {
            log.LogWarning(
                "The portal shares {Streams} screens but only {Displays} different ones: it remembered the choice as monitors it " +
                "cannot tell apart (GNOME knows a monitor by maker, model and serial number, and identical monitors without serial " +
                "numbers are the same to it) and brought back the same one more than once. The others are not shared.",
                session.Streams.Count, displays.Count);
        }

        return capture;
    }

    public IReadOnlyList<DisplayDescriptor> GetDisplays()
    {
        lock (_lock)
        {
            return _displays;
        }
    }

    public IScreenCapturer Create(DisplayDescriptor display, bool preferGpu)
    {
        PortalStream stream = _session.Streams.FirstOrDefault(s => s.NodeId == (uint)display.AdapterLuid)
            ?? throw new ArgumentException($"{display.Name} is not one of this portal session's screens", nameof(display));

        // Factories are synchronous; the portal answers this in milliseconds and the engine has no context to deadlock.
        using SafeFileHandle remote = _session.OpenPipeWireRemoteAsync().GetAwaiter().GetResult();
        IPipeWireStream pipeWire = ShimPipeWireStream.Open(remote, stream.NodeId);
        var capturer = new PortalScreenCapturer(pipeWire, display, _logs.CreateLogger<PortalScreenCapturer>(), OnSizeChanged, OnCapturerDisposed);
        lock (_lock)
        {
            _live.Add(capturer);
        }

        return capturer;
    }

    /// <summary>
    /// The pointer on whichever shared monitor it is over, from that monitor's stream; null when it is over none of
    /// them, or nobody is watching (the streams are only open while somebody is).
    /// </summary>
    internal (CursorImage Shape, int X, int Y)? Cursor()
    {
        PortalScreenCapturer[] live;
        lock (_lock)
        {
            live = [.. _live];
        }

        foreach (PortalScreenCapturer capturer in live)
        {
            if (capturer.Cursor() is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private void OnCapturerDisposed(PortalScreenCapturer capturer)
    {
        lock (_lock)
        {
            _live.Remove(capturer);
        }
    }

    /// <summary>
    /// The display list for the portal's streams: pixel sizes as measured, the portal's layout at the largest scale,
    /// the monitor at the origin first, and the PipeWire node as the native identity.
    ///
    /// A stream in the same place and of the same size as one before it is left out. GNOME brings a remembered choice
    /// back by each monitor's maker, model and serial number, and two identical monitors without serial numbers --
    /// a virtual machine's always are -- both come back as the first: two streams of one monitor, both at its place.
    /// The second shows nothing the first does not, and two displays of one name would cross every viewer that finds
    /// its windows' displays by name.
    /// </summary>
    internal static List<DisplayDescriptor> Describe(IReadOnlyList<PortalStream> streams, IReadOnlyList<(int Width, int Height)> pixels)
    {
        double[] scales = [.. streams.Select((s, i) => s.Width > 0 && pixels[i].Width > 0 ? (double)pixels[i].Width / s.Width : 1.0)];
        double layout = scales.DefaultIfEmpty(1.0).Max();

        // The portal does not say which monitor is primary; the one at the origin is where GNOME and KDE put it.
        int primary = 0;
        for (int i = 0; i < streams.Count; i++)
        {
            if (streams[i].HasPosition && streams[i].X == 0 && streams[i].Y == 0)
            {
                primary = i;
                break;
            }
        }

        // KDE 5.27 says nothing about where its monitors are. Left to right in the portal's order they at least do
        // not overlap, and that is all the input needs: a point goes to one stream in that stream's own coordinates,
        // so only which monitor it is on has to be right, not where the monitors really sit.
        bool positioned = streams.All(s => s.HasPosition);
        var place = new (int X, int Y)[streams.Count];
        for (int i = 0, next = 0; i < streams.Count; i++)
        {
            place[i] = positioned ? ((int)Math.Round(streams[i].X * layout), (int)Math.Round(streams[i].Y * layout)) : (next, 0);
            next += pixels[i].Width;
        }

        var kept = new List<int>(streams.Count);
        for (int i = 0; i < streams.Count; i++)
        {
            PortalStream s = streams[i];
            if (!positioned || !kept.Exists(k => streams[k].X == s.X && streams[k].Y == s.Y && streams[k].Width == s.Width && streams[k].Height == s.Height))
            {
                kept.Add(i);
            }
        }

        return DisplayOrdering.PrimaryFirst(kept.Select(i => new DisplayDescriptor(
            Index: i,
            Name: NameOf(streams[i], i, positioned),
            X: place[i].X,
            Y: place[i].Y,
            Width: pixels[i].Width,
            Height: pixels[i].Height,
            Scale: scales[i],
            Rotation: FrameRotation.None,
            IsPrimary: i == primary,
            AdapterLuid: streams[i].NodeId)));
    }

    /// <summary>
    /// A name that survives the next session: the node id and the portal's stream id are new every time, and a
    /// viewer's display windows are matched to displays by name, so the monitor is named by where it sits -- or,
    /// where the portal does not say (KDE 5.27), by its place in the portal's order, which is the same next time.
    /// </summary>
    internal static string NameOf(PortalStream stream, int index, bool positioned) =>
        positioned ? $"wayland@{stream.X},{stream.Y}" : $"wayland#{index + 1}";

    private static Task<(int Width, int Height)?> ProbeAsync(IPortalSession session, PortalStream stream, CancellationToken ct) =>
        Task.Run(
            async () =>
            {
                using SafeFileHandle remote = await session.OpenPipeWireRemoteAsync(ct).ConfigureAwait(false);
                using ShimPipeWireStream pipeWire = ShimPipeWireStream.Open(remote, stream.NodeId);
                long deadline = Environment.TickCount64 + ProbeTimeoutMs;
                while (Environment.TickCount64 < deadline)
                {
                    ct.ThrowIfCancellationRequested();
                    int result = pipeWire.Wait(100);
                    if (result < 0)
                    {
                        throw new IOException($"PipeWire node {stream.NodeId}: {pipeWire.Failure ?? "the stream ended"}");
                    }

                    if (result > 0 && pipeWire.TryLock(out PipeWireFrame frame))
                    {
                        pipeWire.Release();
                        return ((int Width, int Height)?)(frame.Width, frame.Height);
                    }
                }

                return null;
            },
            ct);

    private void OnSizeChanged(DisplayDescriptor display, int width, int height)
    {
        lock (_lock)
        {
            // A new scale can change the layout of every monitor, not just this one's size.
            int i = _session.Streams.ToList().FindIndex(s => s.NodeId == (uint)display.AdapterLuid);
            if (i >= 0)
            {
                _pixels[i] = (width, height);
                _displays = Describe(_session.Streams, _pixels);
            }
        }

        _log.LogInformation("{Display} is now {Width}x{Height}", display.Name, width, height);
        DisplaysChanged?.Invoke(this, EventArgs.Empty);
    }
}
