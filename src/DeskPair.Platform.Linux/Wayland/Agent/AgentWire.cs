using System.Buffers.Binary;
using System.Text;

namespace DeskPair.Platform.Linux.Wayland.Agent;

/// <summary>What the agent says first: whose session it is in, and on which desktop.</summary>
/// <param name="Uid">The account the agent runs as, which is the session's.</param>
/// <param name="Pid">The agent's process, for the log.</param>
/// <param name="Desktop">XDG_CURRENT_DESKTOP, or empty.</param>
/// <param name="Version">The agent's build, which should be the engine's own.</param>
internal sealed record AgentHello(uint Uid, int Pid, string Desktop, string Version);

/// <summary>A portal session the agent opened, as the engine needs to know it.</summary>
/// <param name="Devices">The input devices granted: <c>1</c> keyboard, <c>2</c> pointer.</param>
/// <param name="RemoteControl">A remote desktop session rather than one that can only watch.</param>
/// <param name="Clipboard">Whether the portal agreed to carry the clipboard.</param>
/// <param name="StartTook">How long starting took, which says whether somebody was asked.</param>
/// <param name="Token">The permission to remember for next time, or null when the portal gave none.</param>
/// <param name="Streams">The shared monitors, in the portal's order.</param>
internal sealed record AgentOpened(uint Devices, bool RemoteControl, bool Clipboard, TimeSpan StartTook, string? Token, IReadOnlyList<PortalStream> Streams);

/// <summary>
/// The messages between the unattended engine and the session agent that holds the portal for it in the signed-in
/// user's session: one message per SOCK_SEQPACKET datagram, an eight-byte header -- version, kind, flags, and the id
/// that pairs an answer with its request (0 for what answers nothing) -- then the kind's fields, little-endian, a
/// string as a two-byte length and UTF-8.
///
/// Hand-written for the reasons <see cref="Capture.Drm.DrmWire"/> is: platform code cannot reach the protobuf types,
/// and a descriptor rides along with some of these. And read as if it came from anybody: the agent runs as another
/// account, so every length and count is checked before anything is trusted.
/// </summary>
internal static class AgentWire
{
    public const byte Version = 1;
    public const int HeaderSize = 8;

    /// <summary>Room for the largest message there is: an <see cref="KindOpened"/> with every stream and string at its limit (some 34 KiB).</summary>
    public const int MaxMessage = 64 * 1024;

    /// <summary>More monitors than anybody has; a list longer than this is not believed.</summary>
    public const int MaxStreams = 16;

    /// <summary>The longest string in bytes; longer ones are cut when written and refused when read.</summary>
    public const int MaxString = 1024;

    /// <summary>More monitors than anybody has; a layout longer than this is not believed.</summary>
    public const int MaxMonitors = 16;

    /// <summary>The farthest from the desktop's origin a monitor's edge is believed to be, and the largest it is believed to be.</summary>
    public const int MaxLayoutExtent = 1 << 16;

    // ---- engine -> agent ----

    /// <summary>Start a portal session: a token to restore, and whether to offer it at all.</summary>
    public const byte KindOpen = 2;

    /// <summary>A new PipeWire connection for the open session; answered with <see cref="KindRemote"/> and the descriptor.</summary>
    public const byte KindOpenRemote = 5;

    /// <summary>One input event for the open session; answers nothing.</summary>
    public const byte KindInput = 7;

    /// <summary>Close the open session, or call off the one being opened; answers nothing.</summary>
    public const byte KindClose = 8;

    // ---- agent -> engine ----

    /// <summary>The agent's first message.</summary>
    public const byte KindHello = 1;

    /// <summary>The session is open: what it shares.</summary>
    public const byte KindOpened = 3;

    /// <summary>A request failed: the portal's reason and the words for it.</summary>
    public const byte KindFailed = 4;

    /// <summary>A PipeWire connection, attached.</summary>
    public const byte KindRemote = 6;

    /// <summary>The portal ended the session without being asked to; answers nothing.</summary>
    public const byte KindClosed = 9;

    /// <summary>How the compositor lays out its monitors, when first read and whenever it changes; answers nothing.</summary>
    public const byte KindLayout = 10;

    /// <summary>On <see cref="KindOpen"/>: offer the remembered permission. Off, the person is asked whatever was remembered.</summary>
    public const byte FlagOfferRestoreToken = 1;

    public static bool TryReadHeader(ReadOnlySpan<byte> message, out byte kind, out byte flags, out uint id)
    {
        kind = 0;
        flags = 0;
        id = 0;
        if (message.Length < HeaderSize || message[0] != Version)
        {
            return false;
        }

        kind = message[1];
        flags = message[2];
        id = BinaryPrimitives.ReadUInt32LittleEndian(message[4..]);
        return true;
    }

    public static int WriteHello(Span<byte> buffer, AgentHello hello)
    {
        var w = new Writer(buffer, KindHello, 0, 0);
        w.U32(hello.Uid);
        w.I32(hello.Pid);
        w.Str(hello.Desktop);
        w.Str(hello.Version);
        return w.Length;
    }

    public static bool TryReadHello(ReadOnlySpan<byte> message, out AgentHello hello)
    {
        var r = new Reader(message);
        uint uid = r.U32();
        int pid = r.I32();
        string desktop = r.Str() ?? string.Empty;
        string version = r.Str() ?? string.Empty;
        hello = new AgentHello(uid, pid, desktop, version);
        return r.Done;
    }

    public static int WriteOpen(Span<byte> buffer, uint id, string? token, bool offerRestoreToken)
    {
        var w = new Writer(buffer, KindOpen, offerRestoreToken ? FlagOfferRestoreToken : (byte)0, id);
        w.Str(token);
        return w.Length;
    }

    public static bool TryReadOpen(ReadOnlySpan<byte> message, out string? token, out bool offerRestoreToken)
    {
        var r = new Reader(message);
        token = r.Str();
        offerRestoreToken = message.Length > 2 && (message[2] & FlagOfferRestoreToken) != 0;
        return r.Done;
    }

    public static int WriteOpened(Span<byte> buffer, uint id, AgentOpened opened)
    {
        var w = new Writer(buffer, KindOpened, 0, id);
        w.U32(opened.Devices);
        w.U8((byte)((opened.RemoteControl ? 1 : 0) | (opened.Clipboard ? 2 : 0)));
        w.U32((uint)Math.Clamp(opened.StartTook.TotalMilliseconds, 0, uint.MaxValue));
        w.Str(opened.Token);
        int count = Math.Min(opened.Streams.Count, MaxStreams);
        w.U8((byte)count);
        for (int i = 0; i < count; i++)
        {
            PortalStream s = opened.Streams[i];
            w.U32(s.NodeId);
            w.I32(s.X);
            w.I32(s.Y);
            w.I32(s.Width);
            w.I32(s.Height);
            w.U8(s.HasPosition ? (byte)1 : (byte)0);
            w.U32(s.SourceType);
            w.Str(s.Id);
            w.Str(s.MappingId);
        }

        return w.Length;
    }

    public static bool TryReadOpened(ReadOnlySpan<byte> message, out AgentOpened opened)
    {
        var r = new Reader(message);
        uint devices = r.U32();
        byte bits = r.U8();
        uint tookMs = r.U32();
        string? token = r.Str();
        int count = r.U8();
        var streams = new List<PortalStream>(Math.Min(count, MaxStreams));
        if (count > MaxStreams)
        {
            r.Fail();
        }

        for (int i = 0; i < count && r.Ok; i++)
        {
            uint node = r.U32();
            int x = r.I32();
            int y = r.I32();
            int width = r.I32();
            int height = r.I32();
            bool positioned = r.U8() != 0;
            uint source = r.U32();
            string? streamId = r.Str();
            string? mapping = r.Str();
            if (width <= 0 || height <= 0)
            {
                r.Fail();
            }

            streams.Add(new PortalStream(node, streamId, x, y, width, height, positioned, source, mapping));
        }

        opened = new AgentOpened(devices, (bits & 1) != 0, (bits & 2) != 0, TimeSpan.FromMilliseconds(tookMs), token, streams);
        return r.Done;
    }

    public static int WriteFailed(Span<byte> buffer, uint id, PortalFailure reason, string message)
    {
        var w = new Writer(buffer, KindFailed, 0, id);
        w.U8((byte)reason);
        w.Str(message);
        return w.Length;
    }

    public static bool TryReadFailed(ReadOnlySpan<byte> message, out PortalFailure reason, out string text)
    {
        var r = new Reader(message);
        byte code = r.U8();
        text = r.Str() ?? string.Empty;
        reason = Enum.IsDefined((PortalFailure)code) ? (PortalFailure)code : PortalFailure.Failed;
        return r.Done;
    }

    public static int WriteOpenRemote(Span<byte> buffer, uint id) => new Writer(buffer, KindOpenRemote, 0, id).Length;

    public static int WriteRemote(Span<byte> buffer, uint id) => new Writer(buffer, KindRemote, 0, id).Length;

    public static int WriteInput(Span<byte> buffer, in PortalSession.InputEvent e)
    {
        var w = new Writer(buffer, KindInput, 0, 0);
        w.U8((byte)e.Kind);
        w.U32(e.A);
        w.I32(e.B);
        w.F64(e.X);
        w.F64(e.Y);
        return w.Length;
    }

    public static bool TryReadInput(ReadOnlySpan<byte> message, out PortalSession.InputEvent e)
    {
        var r = new Reader(message);
        byte kind = r.U8();
        uint a = r.U32();
        int b = r.I32();
        double x = r.F64();
        double y = r.F64();
        e = new PortalSession.InputEvent((PortalSession.InputKind)kind, a, b, x, y);
        return r.Done && Enum.IsDefined((PortalSession.InputKind)kind) && double.IsFinite(x) && double.IsFinite(y);
    }

    public static int WriteClose(Span<byte> buffer) => new Writer(buffer, KindClose, 0, 0).Length;

    public static int WriteClosed(Span<byte> buffer, string reason)
    {
        var w = new Writer(buffer, KindClosed, 0, 0);
        w.Str(reason);
        return w.Length;
    }

    public static bool TryReadClosed(ReadOnlySpan<byte> message, out string reason)
    {
        var r = new Reader(message);
        reason = r.Str() ?? string.Empty;
        return r.Done;
    }

    public static int WriteLayout(Span<byte> buffer, DesktopLayout layout)
    {
        var w = new Writer(buffer, KindLayout, 0, 0);
        int count = Math.Min(layout.Monitors.Count, MaxMonitors);
        w.U8((byte)count);
        for (int i = 0; i < count; i++)
        {
            LayoutMonitor m = layout.Monitors[i];
            w.Str(m.Connector);
            w.I32(m.X);
            w.I32(m.Y);
            w.I32(m.Width);
            w.I32(m.Height);
        }

        return w.Length;
    }

    /// <summary>A layout, refused whole if any monitor in it is unnamed, empty, or farther out than any desktop reaches.</summary>
    public static bool TryReadLayout(ReadOnlySpan<byte> message, out DesktopLayout layout)
    {
        var r = new Reader(message);
        int count = r.U8();
        if (count > MaxMonitors)
        {
            r.Fail();
        }

        var monitors = new List<LayoutMonitor>(Math.Min(count, MaxMonitors));
        for (int i = 0; i < count && r.Ok; i++)
        {
            string? connector = r.Str();
            int x = r.I32();
            int y = r.I32();
            int width = r.I32();
            int height = r.I32();
            if (connector is null || x is < -MaxLayoutExtent or > MaxLayoutExtent || y is < -MaxLayoutExtent or > MaxLayoutExtent ||
                width is <= 0 or > MaxLayoutExtent || height is <= 0 or > MaxLayoutExtent)
            {
                r.Fail();
            }

            monitors.Add(new LayoutMonitor(connector ?? string.Empty, x, y, width, height));
        }

        layout = new DesktopLayout(monitors);
        return r.Done;
    }

    /// <summary>Writes one message; a field that does not fit throws, since every message here has a known largest size.</summary>
    private ref struct Writer
    {
        private readonly Span<byte> _buffer;
        private int _at;

        public Writer(Span<byte> buffer, byte kind, byte flags, uint id)
        {
            _buffer = buffer;
            buffer[..HeaderSize].Clear();
            buffer[0] = Version;
            buffer[1] = kind;
            buffer[2] = flags;
            BinaryPrimitives.WriteUInt32LittleEndian(buffer[4..], id);
            _at = HeaderSize;
        }

        public readonly int Length => _at;

        public void U8(byte value) => _buffer[_at++] = value;

        public void U32(uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(_buffer[_at..], value);
            _at += 4;
        }

        public void I32(int value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(_buffer[_at..], value);
            _at += 4;
        }

        public void F64(double value)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(_buffer[_at..], value);
            _at += 8;
        }

        /// <summary>A string, cut at a character boundary if it is longer than <see cref="MaxString"/> bytes; null writes as empty.</summary>
        public void Str(string? value)
        {
            value ??= string.Empty;
            if (value.Length > MaxString)
            {
                value = value[..MaxString];
            }

            int length = Encoding.UTF8.GetByteCount(value);
            while (length > MaxString)
            {
                value = value[..^1];
                length = Encoding.UTF8.GetByteCount(value);
            }

            BinaryPrimitives.WriteUInt16LittleEndian(_buffer[_at..], (ushort)length);
            _at += 2;
            _at += Encoding.UTF8.GetBytes(value, _buffer[_at..]);
        }
    }

    /// <summary>Reads one message's fields; the first one that is not there fails the whole message rather than reading past it.</summary>
    private ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _message;
        private int _at;

        public Reader(ReadOnlySpan<byte> message)
        {
            _message = message;
            Ok = message.Length >= HeaderSize && message[0] == Version;
            _at = HeaderSize;
        }

        public bool Ok { get; private set; }

        /// <summary>Everything read, and nothing left over: a message longer than its fields is not one this side wrote.</summary>
        public readonly bool Done => Ok && _at == _message.Length;

        public void Fail() => Ok = false;

        public byte U8() => Take(1) ? _message[_at - 1] : (byte)0;

        public uint U32() => Take(4) ? BinaryPrimitives.ReadUInt32LittleEndian(_message[(_at - 4)..]) : 0;

        public int I32() => Take(4) ? BinaryPrimitives.ReadInt32LittleEndian(_message[(_at - 4)..]) : 0;

        public double F64() => Take(8) ? BinaryPrimitives.ReadDoubleLittleEndian(_message[(_at - 8)..]) : 0;

        /// <summary>A string; empty reads as null.</summary>
        public string? Str()
        {
            if (!Take(2))
            {
                return null;
            }

            int length = BinaryPrimitives.ReadUInt16LittleEndian(_message[(_at - 2)..]);
            if (length > MaxString || !Take(length))
            {
                Ok = false;
                return null;
            }

            return length == 0 ? null : Encoding.UTF8.GetString(_message.Slice(_at - length, length));
        }

        private bool Take(int count)
        {
            if (!Ok || _message.Length - _at < count)
            {
                Ok = false;
                return false;
            }

            _at += count;
            return true;
        }
    }
}
