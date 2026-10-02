using System.Buffers.Binary;
using System.Threading.Channels;
using DeskPair.Platform.Abstractions.Audio;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Abstractions.Clipboard;
using DeskPair.Platform.Abstractions.Codec;
using DeskPair.Platform.Abstractions.Cursor;
using DeskPair.Platform.Abstractions.Input;

namespace DeskPair.Core.Testing;

/// <summary>A single fake display.</summary>
public sealed class FakeDisplayEnumerator : IDisplayEnumerator
{
    public FakeDisplayEnumerator(int width = 640, int height = 360, int count = 1)
    {
        Displays = Enumerable.Range(0, count)
            .Select(i => new DisplayDescriptor(i, $"FAKE{i}", i * width, 0, width, height, 1.0, FrameRotation.None, i == 0, 0))
            .ToList();
    }

    public List<DisplayDescriptor> Displays { get; }

    /// <summary>
    /// Held by the fakes that change <see cref="Displays"/> -- a mode set, a display added or taken away -- and while it
    /// is read: the host does those from more than one thread, the last viewer leaving and the host stopping at once, say,
    /// and a list changed from two threads hands out empty descriptors.
    /// </summary>
    public object Sync { get; } = new();

    public IReadOnlyList<DisplayDescriptor> GetDisplays()
    {
        lock (Sync)
        {
            return [.. Displays];
        }
    }

    public event EventHandler? DisplaysChanged;

    /// <summary>What a real enumerator raises when a monitor is plugged in, pulled, or changes mode behind the host's back.</summary>
    public void Raise() => DisplaysChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>A secure-desktop monitor a test drives by hand: every poll answers whatever <see cref="Kind"/> is set to.</summary>
public sealed class FakeSecureDesktopMonitor : ISecureDesktopMonitor
{
    private int _kind = (int)SecureDesktopKind.None;
    private int _onSecure = -1; // -1 follows Kind; 0/1 is explicit.

    /// <summary>What the next poll answers. Set from any thread; the host polls it from a timer thread.</summary>
    public SecureDesktopKind Kind
    {
        get => (SecureDesktopKind)Volatile.Read(ref _kind);
        set => Volatile.Write(ref _kind, (int)value);
    }

    /// <summary>
    /// Whether the input desktop is a secure one. Left unset it follows <see cref="Kind"/> (a UAC/lock kind is on a
    /// secure desktop); set it to model a SYSTEM engine, where the kind is None but the desktop is still secure.
    /// </summary>
    public bool? OnSecureDesktop
    {
        get { int v = Volatile.Read(ref _onSecure); return v < 0 ? null : v != 0; }
        set => Volatile.Write(ref _onSecure, value is null ? -1 : value.Value ? 1 : 0);
    }

    public SecureDesktopState Poll()
    {
        SecureDesktopKind kind = Kind;
        return new SecureDesktopState(kind, OnSecureDesktop ?? kind != SecureDesktopKind.None);
    }
}

/// <summary>An elevator a test drives by hand: <see cref="ElevateAsync"/> succeeds or fails by <see cref="Succeed"/>.</summary>
public sealed class FakeSessionElevator : ISessionElevator
{
    /// <summary>Whether raising the helper reports success (as if the person completed the real UAC).</summary>
    public bool Succeed { get; set; } = true;

    public int Raised { get; private set; }
    public int Lowered { get; private set; }
    public bool IsElevated { get; private set; }

    /// <summary>The last request's permanent flag and peer id, so a test can assert what was asked.</summary>
    public bool LastPermanent { get; private set; }
    public string? LastPeerId { get; private set; }

    public event Action? Ended;

    public Task<bool> ElevateAsync(bool permanent, string? peerId, CancellationToken ct)
    {
        Raised++;
        LastPermanent = permanent;
        LastPeerId = peerId;
        IsElevated = Succeed;
        return Task.FromResult(Succeed);
    }

    public Task LowerAsync()
    {
        Lowered++;
        IsElevated = false;
        return Task.CompletedTask;
    }

    /// <summary>Test hook: pretend the helper died on its own.</summary>
    public void RaiseEnded() => Ended?.Invoke();
}

/// <summary>
/// A display that can be switched between a few sizes. Setting a mode rewrites the enumerator's descriptor,
/// so the capture loop and <c>DescribeDisplays</c> see the new size the way they would on a real machine.
/// </summary>
public sealed class FakeDisplayModes : IDisplayModeSwitcher
{
    private readonly FakeDisplayEnumerator _displays;

    public FakeDisplayModes(FakeDisplayEnumerator displays, params DisplayMode[] modes)
    {
        _displays = displays;
        Modes = modes.Length > 0 ? [.. modes] : [new DisplayMode(640, 360), new DisplayMode(320, 180)];
    }

    public List<DisplayMode> Modes { get; }

    /// <summary>When set, every switch fails with this reason.</summary>
    public string? Refuse { get; set; }

    /// <summary>How long a switch takes, the way a monitor takes a second or two to resynchronise.</summary>
    public TimeSpan Delay { get; set; }

    public List<(string Display, DisplayMode Mode)> Calls { get; } = [];

    private readonly Dictionary<string, List<DisplayMode>> _taught = new(StringComparer.Ordinal);

    /// <summary>Sizes taught to displays and not yet forgotten.</summary>
    public int TaughtCount => _taught.Values.Sum(t => t.Count);

    public IReadOnlyList<DisplayMode> GetModes(DisplayDescriptor display) =>
        _taught.TryGetValue(display.Name, out List<DisplayMode>? taught) ? [.. Modes, .. taught] : Modes;

    /// <summary>Adds a size to one display's list, as a mode sink would.</summary>
    public void Teach(string display, DisplayMode mode)
    {
        if (!_taught.TryGetValue(display, out List<DisplayMode>? taught))
        {
            taught = [];
            _taught[display] = taught;
        }

        taught.Add(mode);
    }

    /// <summary>Takes one taught size off one display's list.</summary>
    public void Forget(string display, DisplayMode mode)
    {
        if (_taught.TryGetValue(display, out List<DisplayMode>? taught))
        {
            taught.RemoveAll(m => m.Width == mode.Width && m.Height == mode.Height);
        }
    }

    public void ForgetTaught() => _taught.Clear();

    public bool TrySetMode(DisplayDescriptor display, DisplayMode mode, out string? failure)
    {
        Calls.Add((display.Name, mode));
        if (Delay > TimeSpan.Zero)
        {
            Thread.Sleep(Delay);
        }

        if (Refuse is { } why)
        {
            failure = why;
            return false;
        }

        lock (_displays.Sync)
        {
            int i = _displays.Displays.FindIndex(d => d.Name == display.Name);
            if (i < 0)
            {
                failure = "no such display";
                return false;
            }

            DisplayDescriptor d = _displays.Displays[i];
            _displays.Displays[i] = d with { Width = mode.Width, Height = mode.Height, Scale = mode.Scale == 0 ? d.Scale : mode.Scale };
        }

        failure = null;
        return true;
    }
}

/// <summary>
/// A private session screen over a <see cref="FakeDisplayEnumerator"/>: opening takes every display off the list and
/// puts one of the asked size in their place, closing puts them back, and <see cref="TakeBack"/> does what somebody at
/// the computer does with Win+P -- the displays come back on beside it.
/// </summary>
public sealed class FakeSessionScreen(FakeDisplayEnumerator displays, FakeDisplayModes modes) : ISessionScreen
{
    public const string Name = "SESSION";

    private List<DisplayDescriptor>? _off;

    /// <summary>When set, it reports itself unavailable with this reason.</summary>
    public string? UnavailableReason { get; set; }

    /// <summary>The sizes it was last opened with, besides the one it came up at.</summary>
    public IReadOnlyList<DisplayMode> LastSizes { get; private set; } = [];

    /// <summary>How many times it was opened.</summary>
    public int Opened { get; private set; }

    /// <summary>
    /// How long opening and closing take after the displays have changed: Windows takes a moment to settle the new
    /// arrangement, and a stream capturing a display that just went fails meanwhile -- before the host follows the change.
    /// </summary>
    public TimeSpan Settling { get; set; } = TimeSpan.Zero;

    public bool IsOpen => _off is not null;

    public bool IsSessionScreen(DisplayDescriptor display) => IsOpen && display.Name == Name;

    public async Task<DisplayActionResult> OpenAsync(DisplayMode size, IReadOnlyList<DisplayMode> sizes, CancellationToken ct)
    {
        if (UnavailableReason is { } why)
        {
            return DisplayActionResult.Refused(why);
        }

        lock (displays.Sync)
        {
            _off = [.. displays.Displays];
            LastSizes = sizes;
            Opened++;
            displays.Displays.Clear();
            displays.Displays.Add(new DisplayDescriptor(0, Name, 0, 0, size.Width, size.Height, 1.0, FrameRotation.None, true, 0));
        }

        foreach (DisplayMode other in sizes)
        {
            modes.Teach(Name, other);
        }

        displays.Raise();
        await Task.Delay(Settling, ct).ConfigureAwait(false);
        return DisplayActionResult.Done;
    }

    public bool IsAlone()
    {
        lock (displays.Sync)
        {
            return IsOpen && displays.Displays.Count == 1;
        }
    }

    public async Task CloseAsync(CancellationToken ct)
    {
        if (_off is { } off)
        {
            lock (displays.Sync)
            {
                _off = null;
                displays.Displays.Clear();
                displays.Displays.AddRange(off);
            }

            displays.Raise();
            await Task.Delay(Settling, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Somebody at the computer turns the other displays back on, beside the session screen.</summary>
    public void TakeBack()
    {
        if (_off is not { } off)
        {
            return;
        }

        lock (displays.Sync)
        {
            int x = displays.Displays.Max(d => d.X + d.Width);
            foreach (DisplayDescriptor d in off)
            {
                displays.Displays.Add(d with { Index = displays.Displays.Count, X = x, IsPrimary = false });
                x += d.Width;
            }
        }

        displays.Raise();
    }
}

/// <summary>
/// Plugs displays into a <see cref="FakeDisplayEnumerator"/> the way a virtual display driver would: a descriptor
/// appears at the end of the list and the enumerator raises its change, and a removal renumbers what is left as a
/// real enumerator does. Sizes are taught to the displays it added, through the <see cref="FakeDisplayModes"/>.
/// </summary>
public sealed class FakeVirtualDisplays(FakeDisplayEnumerator displays, FakeDisplayModes modes) : IVirtualDisplayProvider, IArbitraryModeSink
{
    private const string Prefix = "VIRTUAL";
    private int _next;

    /// <summary>When set, the provider reports itself unavailable with this reason.</summary>
    public string? UnavailableReason { get; set; }

    /// <summary>
    /// Plugs every display in at 640x360 whatever size was asked for, the way Windows brings a monitor back at the
    /// size it last had: the host then has to set the size itself.
    /// </summary>
    public bool IgnoresRequestedSize { get; set; }

    public int Count
    {
        get
        {
            lock (displays.Sync)
            {
                return displays.Displays.Count(IsVirtual);
            }
        }
    }

    /// <summary>Sizes can be taught to every display, not only the ones added here: a Linux desktop's outputs.</summary>
    public bool TeachAll { get; set; }

    public TeachableSizes Limits { get; set; } = new(64, 64, 8192, 8192, 1);

    /// <summary>Sizes taken back one at a time, as a display left them.</summary>
    public List<(string Display, DisplayMode Mode)> Forgotten { get; } = [];

    public bool IsVirtual(DisplayDescriptor display) => display.Name.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>The sizes the last display was asked for with, besides the one it came up at.</summary>
    public IReadOnlyList<DisplayMode> LastSizes { get; private set; } = [];

    public Task<DisplayActionResult> AddAsync(DisplayMode? mode, IReadOnlyList<DisplayMode> sizes, CancellationToken ct)
    {
        LastSizes = sizes;
        lock (displays.Sync)
        {
            List<DisplayDescriptor> list = displays.Displays;
            (int width, int height) = mode is { } m && !IgnoresRequestedSize ? (m.Width, m.Height) : (640, 360);
            int x = list.Count == 0 ? 0 : list.Max(d => d.X + d.Width);
            list.Add(new DisplayDescriptor(list.Count, $"{Prefix}{_next++}", x, 0, width, height, 1.0, FrameRotation.None, list.Count == 0, 0));
        }

        displays.Raise();
        return Task.FromResult(DisplayActionResult.Done);
    }

    public Task<DisplayActionResult> RemoveAsync(DisplayDescriptor display, CancellationToken ct)
    {
        lock (displays.Sync)
        {
            int i = displays.Displays.FindIndex(d => d.Name == display.Name);
            if (i < 0)
            {
                return Task.FromResult(DisplayActionResult.Refused("not there"));
            }

            displays.Displays.RemoveAt(i);
            Renumber();
        }

        displays.Raise();
        return Task.FromResult(DisplayActionResult.Done);
    }

    public Task RemoveAllAsync(CancellationToken ct)
    {
        bool removed;
        lock (displays.Sync)
        {
            removed = displays.Displays.RemoveAll(IsVirtual) > 0;
            if (removed)
            {
                Renumber();
            }
        }

        if (removed)
        {
            displays.Raise();
        }

        return Task.CompletedTask;
    }

    public bool CanTeach(DisplayDescriptor display) => TeachAll || IsVirtual(display);

    public Task<DisplayActionResult> TeachAsync(DisplayDescriptor display, DisplayMode mode, CancellationToken ct)
    {
        modes.Teach(display.Name, mode);
        return Task.FromResult(DisplayActionResult.Done);
    }

    public Task ForgetAsync(DisplayDescriptor display, DisplayMode mode, CancellationToken ct)
    {
        Forgotten.Add((display.Name, mode));
        modes.Forget(display.Name, mode);
        return Task.CompletedTask;
    }

    public Task ForgetTaughtModesAsync(CancellationToken ct)
    {
        modes.ForgetTaught();
        return Task.CompletedTask;
    }

    /// <summary>Under the enumerator's lock; the caller raises the change once it is out of it.</summary>
    private void Renumber()
    {
        for (int i = 0; i < displays.Displays.Count; i++)
        {
            displays.Displays[i] = displays.Displays[i] with { Index = i };
        }
    }
}

/// <summary>Produces a moving gradient in BGRA; every Nth acquire reports "unchanged" to exercise the static path.</summary>
/// <summary>
/// Produces one BGRA frame per call. <paramref name="frame"/> counts from 1; return false to report
/// "nothing changed" (the capturer then answers with a timeout instead of a frame). Do not touch
/// <paramref name="bgra"/> when returning false: the previous frame must stay intact.
/// </summary>
public delegate bool FakeFrameGenerator(int frame, byte[] bgra, int stride, int width, int height);

public sealed class FakeScreenCapturerFactory : IScreenCapturerFactory
{
    public int UnchangedEvery { get; set; } = 0;

    /// <summary>Custom picture source; null keeps the default moving gradient.</summary>
    public FakeFrameGenerator? Generator { get; set; }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _created = new(StringComparer.Ordinal);

    /// <summary>How many capturers were made for a display: one per stream start, so a restarted stream counts again.</summary>
    public int CreatedFor(string display) => _created.GetValueOrDefault(display);

    /// <summary>
    /// When set, a capturer whose display has left this enumerator throws, the way a real one does when Windows turns its
    /// display off (GDI's CreateDC fails for it): the stream crashes, and comes back only if the host restarts it.
    /// </summary>
    public FakeDisplayEnumerator? FailWhenGone { get; set; }

    public IScreenCapturer Create(DisplayDescriptor display, bool preferGpu)
    {
        _created.AddOrUpdate(display.Name, 1, (_, n) => n + 1);
        return new FakeScreenCapturer(display, UnchangedEvery, Generator, FailWhenGone);
    }

    private sealed class FakeScreenCapturer(DisplayDescriptor display, int unchangedEvery, FakeFrameGenerator? generator, FakeDisplayEnumerator? failWhenGone) : IScreenCapturer
    {
        private readonly byte[] _buffer = new byte[display.Width * display.Height * 4];
        private int _frame;

        public DisplayDescriptor Display => display;
        public bool SupportsGpuTexture => false;
        public GpuApi GpuApi => GpuApi.None;

        public ValueTask<CaptureResult> AcquireFrameAsync(TimeSpan timeout, CancellationToken ct)
        {
            if (failWhenGone is not null && !failWhenGone.GetDisplays().Any(d => d.Name == display.Name))
            {
                throw new InvalidOperationException($"{display.Name} is not on the desktop any more.");
            }

            _frame++;
            if (unchangedEvery > 0 && _frame % unchangedEvery == 0)
            {
                return ValueTask.FromResult(CaptureResult.TimedOut);
            }

            int stride = display.Width * 4;
            if (generator is not null)
            {
                if (!generator(_frame, _buffer, stride, display.Width, display.Height))
                {
                    return ValueTask.FromResult(CaptureResult.TimedOut);
                }

                return ValueTask.FromResult(new CaptureResult(CaptureStatus.Frame, new CaptureFrame
                {
                    Width = display.Width,
                    Height = display.Height,
                    Format = PixelFormat.Bgra32,
                    Cpu = _buffer,
                    Stride = stride,
                }));
            }

            for (int y = 0; y < display.Height; y++)
            {
                for (int x = 0; x < display.Width; x++)
                {
                    int o = y * stride + x * 4;
                    _buffer[o] = (byte)(x + _frame);
                    _buffer[o + 1] = (byte)(y + _frame);
                    _buffer[o + 2] = (byte)_frame;
                    _buffer[o + 3] = 255;
                }
            }

            return ValueTask.FromResult(new CaptureResult(CaptureStatus.Frame, new CaptureFrame
            {
                Width = display.Width,
                Height = display.Height,
                Format = PixelFormat.Bgra32,
                Cpu = _buffer,
                Stride = stride,
            }));
        }

        public void ForceFallbackPath()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>
/// "Codec" that ships a downscaled BGRA thumbnail (every 8th pixel) as the packet; the decoder scales it
/// back up with nearest-neighbour, so the round trip is deliberately lossy like a real video codec.
/// Keyframes carry the whole thumbnail; delta frames carry the same but flagged as delta, so decoders can
/// be tested for keyframe gating without a real video codec.
/// </summary>
public sealed class FakeVideoEncoderFactory : IVideoEncoderFactory
{
    /// <summary>
    /// Bandwidth tests: every packet is exactly the size the current bitrate allows at 60 fps (a 1x1 "thumbnail"
    /// plus padding), so the stream behaves like a rate-controlled encoder.
    /// </summary>
    public bool PadToBitrate { get; init; }

    /// <summary>What this fake claims to encode. A knob, so a test can set up a negotiation without hardware.</summary>
    public IReadOnlyList<VideoCodec> Codecs { get; init; } = [VideoCodec.H264];

    /// <summary>
    /// Names this fake pretends to be, best first, so a test can watch the host fall from one encoder to the
    /// next. <see cref="Breaks"/> decides which of them misbehave and how.
    /// </summary>
    public IReadOnlyList<string> Names { get; init; } = ["Fake encoder"];

    /// <summary>How a named encoder misbehaves. A name absent from this map works normally.</summary>
    public IReadOnlyDictionary<string, EncoderFault> Breaks { get; init; } = new Dictionary<string, EncoderFault>();

    /// <summary>
    /// Codecs this fake lists but cannot start an encoder for, the way Media Foundation's software H.264 encoder lists
    /// itself on a Windows with no graphics driver and then refuses its output format.
    /// </summary>
    public IReadOnlyList<VideoCodec> Unstartable { get; init; } = [];

    /// <summary>The name this factory last handed out, which is the one a session is running on.</summary>
    public string? LastCreated { get; private set; }

    /// <summary>
    /// Keyframes made because somebody asked for one -- a viewer, the loss handling, the first frame -- across every
    /// encoder this factory built. The ones an encoder makes on its own every 60 frames are not counted: how many of
    /// those fall in a stretch of time is the machine's frame rate, not anything a test is asking about.
    /// </summary>
    public int KeyFramesRequested => Volatile.Read(ref _keyFramesRequested);

    private int _keyFramesRequested;

    public IReadOnlyList<EncoderDescriptor> Describe() => Codecs
        .SelectMany(c => Names.Select(n => new EncoderDescriptor { Codec = c, Backend = CodecBackend.Fake, Name = n, IsHardware = false }))
        .ToList();

    public SupportedCodecs Probe() => Describe().ToFlags();

    public IVideoEncoder Create(VideoEncoderConfig config)
    {
        if (Unstartable.Contains(config.Codec))
        {
            throw new NotSupportedException($"No fake {config.Codec} encoder accepted the configuration.");
        }

        string? name = Names.FirstOrDefault(n => !config.Exclude.Contains(n));
        if (name is null)
        {
            throw new NotSupportedException($"Every fake {config.Codec} encoder has been excluded.");
        }

        LastCreated = name;
        return new FakeVideoEncoder(this, config, PadToBitrate, name, Breaks.GetValueOrDefault(name, EncoderFault.None));
    }

    private sealed class FakeVideoEncoder(FakeVideoEncoderFactory owner, VideoEncoderConfig config, bool padToBitrate, string name, EncoderFault fault) : IVideoEncoder
    {
        private bool _forceKey = true;
        private int _count;
        private EncodedPacket? _waiting;

        public VideoCodec Codec => config.Codec;

        public EncoderDescriptor Descriptor => new()
        {
            Codec = config.Codec,
            Backend = CodecBackend.Fake,
            Name = name,
            IsHardware = false,
        };

        public bool IsHardware => false;
        public bool IsLatencyFree => fault != EncoderFault.OneFrameLate;
        public PixelFormat RequiredInputFormat => PixelFormat.Bgra32;
        public int Bitrate { get; private set; } = config.BitrateKbps;
        public int KeyFramesProduced { get; private set; }

        public void SetBitrate(int kbps) => Bitrate = kbps;

        public void RequestKeyFrame() => _forceKey = true;

        public bool TryEncode(ReadOnlySpan<byte> input, int stride, long ptsTicks, out EncodedPacket packet)
        {
            if (fault == EncoderFault.Throws)
            {
                throw new InvalidOperationException($"{name} is pretending its driver died.");
            }

            if (fault == EncoderFault.SilentlyFreezes)
            {
                // What an AMD encoder has been seen to do: keep saying yes with the same short packet while
                // the picture stops moving. Nothing reports an error, so only the output gives it away.
                packet = new EncodedPacket { Data = new byte[40], IsKeyFrame = false, PtsTicks = ptsTicks };
                return true;
            }

            bool asked = _forceKey;
            bool key = asked || _count % 60 == 0;
            _forceKey = false;
            _count++;
            if (asked)
            {
                Interlocked.Increment(ref owner._keyFramesRequested);
            }

            int w = padToBitrate ? 1 : config.Width / 8;
            int h = padToBitrate ? 1 : config.Height / 8;
            int size = 16 + w * h * 4;
            if (padToBitrate)
            {
                size = Math.Max(size, Bitrate * 1000 / 8 / 60);
            }

            byte[] data = new byte[size];
            BinaryPrimitives.WriteInt32LittleEndian(data, w);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), h);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), config.Width);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(12), config.Height);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    input.Slice(y * 8 * stride + x * 8 * 4, 4).CopyTo(data.AsSpan(16 + (y * w + x) * 4));
                }
            }

            if (key)
            {
                KeyFramesProduced++;
            }

            packet = new EncodedPacket { Data = data, IsKeyFrame = key, PtsTicks = ptsTicks };
            if (fault == EncoderFault.OneFrameLate)
            {
                // This frame stays inside; what comes out is the one before it, if there was one.
                (EncodedPacket? earlier, _waiting) = (_waiting, packet);
                packet = earlier.GetValueOrDefault();
                return earlier is not null;
            }

            return true;
        }

        public bool TryCollect(out EncodedPacket packet)
        {
            packet = _waiting.GetValueOrDefault();
            bool had = _waiting is not null;
            _waiting = null;
            return had;
        }

        public bool TryEncode(in GpuSurfaceHandle texture, long ptsTicks, out EncodedPacket packet)
        {
            packet = default;
            return false;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>How a fake encoder misbehaves, so failover can be tested without a vendor driver to break.</summary>
public enum EncoderFault
{
    None,

    /// <summary>Throws from every encode, the way a driver that has gone away does.</summary>
    Throws,

    /// <summary>Returns success with the same short packet forever, the way a corrupt one does.</summary>
    SilentlyFreezes,

    /// <summary>
    /// Hands each frame out only when asked again, the way an asynchronous hardware encoder does when a frame
    /// takes longer than the caller waits for it. Not a fault as such: nothing is wrong with the packets.
    /// </summary>
    OneFrameLate,
}

public sealed class FakeVideoDecoderFactory : IVideoDecoderFactory
{
    /// <summary>What this fake claims to decode; the encoder fake has the matching knob.</summary>
    public IReadOnlyList<VideoCodec> Codecs { get; init; } = [VideoCodec.H264];

    public SupportedCodecs Probe()
    {
        SupportedCodecs all = SupportedCodecs.None;
        foreach (VideoCodec codec in Codecs)
        {
            all |= SupportedCodecsExtensions.Bit(codec, hardware: false);
        }

        return all;
    }

    public IVideoDecoder Create(VideoCodec codec, GpuApi preferredOutput, long adapterLuid) => new FakeVideoDecoder(codec);

    private sealed class FakeVideoDecoder(VideoCodec codec) : IVideoDecoder
    {
        private byte[] _pixels = [];
        private bool _haveKey;

        public VideoCodec Codec => codec;
        public bool OutputsGpuSurface => false;

        public bool TryDecode(ReadOnlySpan<byte> packet, bool isKeyFrame, out DecodedFrame frame)
        {
            frame = default;
            if (!isKeyFrame && !_haveKey)
            {
                return false; // cannot start on a delta frame
            }

            int tw = BinaryPrimitives.ReadInt32LittleEndian(packet);
            int th = BinaryPrimitives.ReadInt32LittleEndian(packet[4..]);
            int w = BinaryPrimitives.ReadInt32LittleEndian(packet[8..]);
            int h = BinaryPrimitives.ReadInt32LittleEndian(packet[12..]);

            // The fake decoder only understands frames the fake encoder produced. Connected to a real host
            // (a real VP9/H.264 encoder), the packet is not this synthetic format: the dimensions read out as
            // garbage. Fail the frame cleanly instead of letting w*h*4 overflow, so a live E2E run that uses
            // the fake decoder to count frames does not spew decode exceptions.
            if (tw <= 0 || th <= 0 || w <= 0 || h <= 0 || (long)w * h > 64L * 1024 * 1024 ||
                packet.Length < 16 + (long)tw * th * 4)
            {
                return false;
            }

            _haveKey = true;
            if (_pixels.Length != w * h * 4)
            {
                _pixels = new byte[w * h * 4];
            }

            ReadOnlySpan<byte> thumb = packet[16..];
            for (int y = 0; y < h; y++)
            {
                int ty = Math.Min(th - 1, y / 8);
                for (int x = 0; x < w; x++)
                {
                    int tx = Math.Min(tw - 1, x / 8);
                    thumb.Slice((ty * tw + tx) * 4, 4).CopyTo(_pixels.AsSpan((y * w + x) * 4));
                }
            }

            frame = new DecodedFrame { Width = w, Height = h, Format = PixelFormat.Bgra32, Cpu = _pixels, Stride = w * 4 };
            return true;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>Records every injected event.</summary>
public sealed class FakeInputInjector : IInputInjector
{
    private readonly object _lock = new();
    public List<MouseInput> Mouse { get; } = [];
    public List<KeyInput> Keys { get; } = [];
    public int ReleaseAllCalls { get; private set; }
    public int CtrlAltDelCalls { get; private set; }

    public void EnsureInputDesktop()
    {
    }

    public void InjectMouse(in MouseInput input, in VirtualScreenRect virtualScreen)
    {
        lock (_lock)
        {
            Mouse.Add(input);
        }
    }

    public void InjectKey(in KeyInput input)
    {
        lock (_lock)
        {
            Keys.Add(input);
        }
    }

    public LockKeyStates GetLockKeyStates() => default;

    public void SetLockKeyStates(LockKeyStates states)
    {
    }

    public void ReleaseAll()
    {
        lock (_lock)
        {
            ReleaseAllCalls++;
        }
    }

    public void SendCtrlAltDel()
    {
        lock (_lock)
        {
            CtrlAltDelCalls++;
        }
    }

    /// <summary>Counts lock requests so tests can assert "lock when the session ends" actually fired.</summary>
    public int LockCalls { get; private set; }

    public void LockWorkstation() => LockCalls++;

    public int MouseCount
    {
        get
        {
            lock (_lock)
            {
                return Mouse.Count;
            }
        }
    }

    public int KeyCount
    {
        get
        {
            lock (_lock)
            {
                return Keys.Count;
            }
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Cursor that walks diagonally and swaps between two shapes.</summary>
public sealed class FakeCursorProvider : ICursorProvider
{
    private int _tick;

    public ulong GetCurrentCursorId() => (ulong)(1 + (_tick / 30) % 2);

    public CursorImage? GetCursorImage(ulong id) => new(id, 1, 1, 16, 16, new byte[16 * 16 * 4]);

    public (int X, int Y)? GetCursorPosition()
    {
        _tick++;
        return (_tick % 640, _tick % 360);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Emits a 440 Hz tone at 48 kHz stereo in 10 ms packets, paced in real time.</summary>
public sealed class FakeAudioCapture : IAudioCapture
{
    private readonly Channel<ReadOnlyMemory<float>> _frames = Channel.CreateBounded<ReadOnlyMemory<float>>(new BoundedChannelOptions(50) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public AudioStreamFormat Format { get; } = new(48000, 2);

    public ChannelReader<ReadOnlyMemory<float>> Frames => _frames.Reader;

    public ValueTask StartAsync(CancellationToken ct)
    {
        _loop = Task.Run(async () =>
        {
            int n = 0;
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
            try
            {
                while (await timer.WaitForNextTickAsync(_cts.Token))
                {
                    float[] pcm = new float[480 * 2];
                    for (int i = 0; i < 480; i++, n++)
                    {
                        float v = (float)Math.Sin(2 * Math.PI * 440 * n / 48000.0) * 0.2f;
                        pcm[i * 2] = v;
                        pcm[i * 2 + 1] = v;
                    }

                    _frames.Writer.TryWrite(pcm);
                }
            }
            catch (OperationCanceledException)
            {
            }

            _frames.Writer.TryComplete();
        });
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync()
    {
        _cts.Cancel();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        if (_loop is not null)
        {
            await _loop;
        }

        _cts.Dispose();
    }
}

/// <summary>
/// Clipboard whose content is set programmatically; local sets raise change notifications, remote writes do
/// not. It implements <see cref="IFilePromiseClipboard"/> as well, so the routing that decides whether a
/// promise can be kept is testable without any operating system.
/// </summary>
public sealed class FakeClipboard : IClipboard, IFilePromiseClipboard
{
    private readonly Channel<IReadOnlyList<ClipboardItem>> _changes = Channel.CreateUnbounded<IReadOnlyList<ClipboardItem>>();
    private IReadOnlyList<ClipboardItem> _content = [];

    public ChannelReader<IReadOnlyList<ClipboardItem>> Changes => _changes.Reader;

    public IReadOnlyList<ClipboardItem> Content => _content;

    public int Writes { get; private set; }

    public string? Text => _content.FirstOrDefault(c => c.Format == ClipboardItemFormat.Text) is { } t ? System.Text.Encoding.UTF8.GetString(t.Payload.Span) : null;

    /// <summary>Emulates the local user copying something.</summary>
    public void SetText(string text)
    {
        _content = [new ClipboardItem(ClipboardItemFormat.Text, System.Text.Encoding.UTF8.GetBytes(text))];
        _changes.Writer.TryWrite(_content);
    }

    public ValueTask<IReadOnlyList<ClipboardItem>> ReadAsync(CancellationToken ct) => ValueTask.FromResult(_content);

    public ValueTask WriteAsync(IReadOnlyList<ClipboardItem> items, CancellationToken ct)
    {
        _content = items;
        Writes++;
        return ValueTask.CompletedTask;
    }

    /// <summary>Set false to stand in for a platform that cannot promise, or a session with no display.</summary>
    public bool CanPromiseFiles { get; set; } = true;

    /// <summary>Emulates the local user having copied files in a file manager.</summary>
    public IReadOnlyList<string> CopiedFilePaths { get; set; } = [];

    public ValueTask<IReadOnlyList<string>> ReadCopiedFilePathsAsync(CancellationToken ct) =>
        ValueTask.FromResult(CopiedFilePaths);

    /// <summary>The promise this clipboard is currently offering, if any.</summary>
    public FilePromiseListing? Promised { get; private set; }

    public IFilePromiseSource? PromiseSource { get; private set; }

    public int PromiseWrites { get; private set; }

    public ValueTask WriteWithPromiseAsync(
        IReadOnlyList<ClipboardItem> items,
        FilePromiseListing listing,
        IFilePromiseSource source,
        CancellationToken ct)
    {
        _content = items;
        Promised = listing;
        PromiseSource = source;
        PromiseWrites++;
        Writes++;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _changes.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Displays that appear only while somebody watches, the shape of a Wayland portal session: opening adds them to the
/// enumerator, closing takes them away. It can be made to ask first (and wait for <see cref="Consent"/>), to refuse,
/// and to be stopped from the machine's own side.
/// </summary>
public sealed class FakeDisplaySession(FakeDisplayEnumerator displays, int count = 1) : IDisplaySession
{
    private readonly object _lock = new();
    private int _opens;
    private int _closes;

    /// <summary>How many times opening was asked for.</summary>
    public int Opens => Volatile.Read(ref _opens);

    /// <summary>How many times closing was asked for.</summary>
    public int Closes => Volatile.Read(ref _closes);

    /// <summary>When set, opening says this to the viewers and then waits for <see cref="Consent"/>.</summary>
    public string? AskFirst { get; set; }

    /// <summary>Completed to answer the question <see cref="AskFirst"/> put.</summary>
    public TaskCompletionSource Consent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>When set, opening fails with this reason.</summary>
    public string? Refusal { get; set; }

    public bool IsOpen { get; private set; }

    public event Action<string>? Closed;

    public event Action? Reopenable;

    public async Task<string?> OpenAsync(Action<string> progress, CancellationToken ct)
    {
        Interlocked.Increment(ref _opens);
        if (AskFirst is { } asking)
        {
            progress(asking);
            await Consent.Task.WaitAsync(ct).ConfigureAwait(false);
        }

        if (Refusal is { } refusal)
        {
            return refusal;
        }

        lock (_lock)
        {
            if (!IsOpen)
            {
                for (int i = 0; i < count; i++)
                {
                    displays.Displays.Add(new DisplayDescriptor(i, $"SHARED{i}", i * 640, 0, 640, 360, 1.0, FrameRotation.None, i == 0, 0));
                }

                IsOpen = true;
            }
        }

        return null;
    }

    public ValueTask CloseAsync()
    {
        Interlocked.Increment(ref _closes);
        lock (_lock)
        {
            displays.Displays.Clear();
            IsOpen = false;
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>The person at the machine stopped sharing.</summary>
    public void StopFromTheMachine(string reason)
    {
        lock (_lock)
        {
            displays.Displays.Clear();
            IsOpen = false;
        }

        displays.Raise();
        Closed?.Invoke(reason);
    }

    /// <summary>The machine's screen is back -- unlocked, after a lock ended the sharing -- and can be shared again.</summary>
    public void ScreenBack() => Reopenable?.Invoke();
}
