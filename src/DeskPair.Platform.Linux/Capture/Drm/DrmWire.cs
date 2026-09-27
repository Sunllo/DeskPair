using System.Buffers.Binary;
using System.Text;

namespace DeskPair.Platform.Linux.Capture.Drm;

/// <summary>
/// What crosses the socketpair between the root daemon and the engine, byte for byte.
///
/// Fixed-size little-endian records, no strings on the frame path, so that neither side needs a
/// serializer and a version mismatch is a number in the first byte rather than a parse error. Each
/// message is one SEQPACKET datagram; a descriptor, when there is one, rides on the same datagram.
/// </summary>
public static class DrmWire
{
    public const byte Version = 1;

    public const byte KindPoll = 1;
    public const byte KindFrame = 2;
    public const byte KindNoScanout = 3;
    public const byte KindError = 4;

    /// <summary>The engine asks the daemon to lock whoever is on the seat; the answer is <see cref="KindLocked"/> or an error.</summary>
    public const byte KindLock = 5;
    public const byte KindLocked = 6;

    /// <summary>
    /// The engine asks the daemon for a shell: which identity (highest or the signed-in user) and the
    /// size, and nothing else -- no program, no argument, no path. Everything the daemon runs as root is
    /// chosen by the daemon. The answer is <see cref="KindTerminal"/> or an error.
    /// </summary>
    public const byte KindOpenTerminal = 7;

    /// <summary>A shell was started: its handle and account, and the pty master attached (none when only described).</summary>
    public const byte KindTerminal = 8;

    /// <summary>The engine asks about or signals a shell the daemon started, by handle; 0 asks, anything else sends that signal.</summary>
    public const byte KindTerminalSignal = 9;

    /// <summary>Whether that shell has ended, and how.</summary>
    public const byte KindTerminalState = 10;

    /// <summary>
    /// The engine asks whether a session agent is waiting for it: the daemon starts one as the signed-in user for a
    /// Wayland desktop, and the engine, which was running before anybody signed in, comes for its socket. The answer
    /// is <see cref="KindAgent"/>.
    /// </summary>
    public const byte KindAgentPoll = 11;

    /// <summary>A session agent's socket, attached (<see cref="FlagFdAttached"/>), with whose session it is in; or, with nothing attached, that none waits.</summary>
    public const byte KindAgent = 12;

    /// <summary>The longest logind session id an agent answer carries; ids are short ("2", "c1"), and a longer one is not believed.</summary>
    public const int MaxSessionId = 64;

    /// <summary>On <see cref="KindOpenTerminal"/>: say whose shell it would be, start nothing.</summary>
    public const byte FlagDescribeOnly = 1;

    /// <summary>
    /// On <see cref="KindError"/>: the daemon is not allowed to give this (its own configuration says no),
    /// as distinct from having tried and failed. The engine then offers a shell as its own account.
    /// </summary>
    public const byte FlagRefused = 1;

    /// <summary>A dma-buf descriptor is attached to this datagram.</summary>
    public const byte FlagFdAttached = 1;

    /// <summary>The CRTC is off: the display is asleep, or nothing is driving it.</summary>
    public const byte FlagCrtcOff = 2;

    /// <summary>
    /// There is no display hardware at all: a server with no card, or none the daemon could open. Distinct
    /// from <see cref="FlagCrtcOff"/> because that one is worth nudging the pointer for and this one never is.
    /// </summary>
    public const byte FlagNoHardware = 4;

    public const int PollSize = 4 + 4 * KnownFbSlots;
    public const int FrameSize = 96;
    public const int KnownFbSlots = 4;

    /// <summary>The engine's request: which framebuffers it already holds a descriptor for.</summary>
    public static int WritePoll(Span<byte> buffer, ReadOnlySpan<uint> knownFbIds)
    {
        buffer[0] = Version;
        buffer[1] = KindPoll;
        buffer[2] = 0;
        buffer[3] = 0;
        for (int i = 0; i < KnownFbSlots; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer[(4 + i * 4)..], i < knownFbIds.Length ? knownFbIds[i] : 0);
        }

        return PollSize;
    }

    public static bool TryReadPoll(ReadOnlySpan<byte> buffer, Span<uint> knownFbIds)
    {
        if (buffer.Length < PollSize || buffer[0] != Version || buffer[1] != KindPoll)
        {
            return false;
        }

        for (int i = 0; i < KnownFbSlots && i < knownFbIds.Length; i++)
        {
            knownFbIds[i] = BinaryPrimitives.ReadUInt32LittleEndian(buffer[(4 + i * 4)..]);
        }

        return true;
    }

    public static int WriteFrame(Span<byte> buffer, in DrmFrameInfo frame, bool fdAttached)
    {
        buffer[..FrameSize].Clear();
        buffer[0] = Version;
        buffer[1] = KindFrame;
        buffer[2] = (byte)(fdAttached ? FlagFdAttached : 0);
        buffer[3] = frame.PlaneCount;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[4..], frame.Generation);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[8..], frame.FbId);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[12..], frame.CrtcId);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[16..], frame.ConnectorId);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[20..], frame.Fourcc);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[24..], frame.Modifier);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[32..], frame.Width);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[36..], frame.Height);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[40..], frame.SrcX);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[44..], frame.SrcY);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[48..], frame.SrcWidth);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[52..], frame.SrcHeight);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[56..], frame.Rotation);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[60..], frame.Pitch);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[64..], frame.Offset);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[72..], frame.Sequence);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[80..], frame.TimestampNs);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[88..], frame.ConnectorType);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[92..], frame.ConnectorTypeId);
        return FrameSize;
    }

    public static bool TryReadFrame(ReadOnlySpan<byte> buffer, out DrmFrameInfo frame, out bool fdAttached)
    {
        frame = default;
        fdAttached = false;
        if (buffer.Length < FrameSize || buffer[0] != Version || buffer[1] != KindFrame)
        {
            return false;
        }

        fdAttached = (buffer[2] & FlagFdAttached) != 0;
        frame = new DrmFrameInfo
        {
            PlaneCount = buffer[3],
            Generation = BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..]),
            FbId = BinaryPrimitives.ReadUInt32LittleEndian(buffer[8..]),
            CrtcId = BinaryPrimitives.ReadUInt32LittleEndian(buffer[12..]),
            ConnectorId = BinaryPrimitives.ReadUInt32LittleEndian(buffer[16..]),
            Fourcc = BinaryPrimitives.ReadUInt32LittleEndian(buffer[20..]),
            Modifier = BinaryPrimitives.ReadUInt64LittleEndian(buffer[24..]),
            Width = BinaryPrimitives.ReadUInt32LittleEndian(buffer[32..]),
            Height = BinaryPrimitives.ReadUInt32LittleEndian(buffer[36..]),
            SrcX = BinaryPrimitives.ReadUInt32LittleEndian(buffer[40..]),
            SrcY = BinaryPrimitives.ReadUInt32LittleEndian(buffer[44..]),
            SrcWidth = BinaryPrimitives.ReadUInt32LittleEndian(buffer[48..]),
            SrcHeight = BinaryPrimitives.ReadUInt32LittleEndian(buffer[52..]),
            Rotation = BinaryPrimitives.ReadUInt32LittleEndian(buffer[56..]),
            Pitch = BinaryPrimitives.ReadUInt32LittleEndian(buffer[60..]),
            Offset = BinaryPrimitives.ReadUInt32LittleEndian(buffer[64..]),
            Sequence = BinaryPrimitives.ReadUInt64LittleEndian(buffer[72..]),
            TimestampNs = BinaryPrimitives.ReadInt64LittleEndian(buffer[80..]),
            ConnectorType = BinaryPrimitives.ReadUInt32LittleEndian(buffer[88..]),
            ConnectorTypeId = BinaryPrimitives.ReadUInt32LittleEndian(buffer[92..]),
        };
        return true;
    }

    /// <summary>"There is nothing on the screen to read right now": the CRTC is off or has no framebuffer.</summary>
    public static int WriteNoScanout(Span<byte> buffer, uint generation, bool crtcOff) => WriteNoScanout(buffer, generation, crtcOff, noHardware: false);

    /// <summary>As above, or with <paramref name="noHardware"/> "and there never will be: this machine has no display".</summary>
    public static int WriteNoScanout(Span<byte> buffer, uint generation, bool crtcOff, bool noHardware)
    {
        buffer[..8].Clear();
        buffer[0] = Version;
        buffer[1] = KindNoScanout;
        buffer[2] = (byte)((crtcOff ? FlagCrtcOff : 0) | (noHardware ? FlagNoHardware : 0));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[4..], generation);
        return 8;
    }

    public static bool TryReadNoScanout(ReadOnlySpan<byte> buffer, out uint generation, out bool crtcOff) =>
        TryReadNoScanout(buffer, out generation, out crtcOff, out _);

    public static bool TryReadNoScanout(ReadOnlySpan<byte> buffer, out uint generation, out bool crtcOff, out bool noHardware)
    {
        generation = 0;
        crtcOff = false;
        noHardware = false;
        if (buffer.Length < 8 || buffer[0] != Version || buffer[1] != KindNoScanout)
        {
            return false;
        }

        crtcOff = (buffer[2] & FlagCrtcOff) != 0;
        noHardware = (buffer[2] & FlagNoHardware) != 0;
        generation = BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..]);
        return true;
    }

    /// <summary>A named failure, in the daemon's own words, so the engine's log and the viewer's notice agree.</summary>
    public static int WriteError(Span<byte> buffer, string message)
    {
        buffer[0] = Version;
        buffer[1] = KindError;
        buffer[2] = 0;
        buffer[3] = 0;

        // Cut to fit rather than throw: a long message must not be what breaks the channel.
        int room = buffer.Length - 4;
        while (Encoding.UTF8.GetByteCount(message) > room && message.Length > 0)
        {
            message = message[..^1];
        }

        int written = Encoding.UTF8.GetBytes(message.AsSpan(), buffer[4..]);
        return 4 + written;
    }

    public static bool TryReadError(ReadOnlySpan<byte> buffer, out string message)
    {
        message = string.Empty;
        if (buffer.Length < 4 || buffer[0] != Version || buffer[1] != KindError)
        {
            return false;
        }

        message = Encoding.UTF8.GetString(buffer[4..]);
        return true;
    }

    /// <summary>
    /// "Lock the seat." The one request that is not about pixels. Locking a session is a logind call that
    /// polkit answers with "interactive authentication required" for the engine's own account, and root is
    /// the one account it does not ask; so the engine asks the daemon, over the channel it already has.
    /// </summary>
    public static int WriteLock(Span<byte> buffer)
    {
        buffer[0] = Version;
        buffer[1] = KindLock;
        buffer[2] = 0;
        buffer[3] = 0;
        return 4;
    }

    public static bool TryReadLock(ReadOnlySpan<byte> buffer) => buffer.Length >= 4 && buffer[0] == Version && buffer[1] == KindLock;

    public static int WriteLocked(Span<byte> buffer)
    {
        buffer[0] = Version;
        buffer[1] = KindLocked;
        buffer[2] = 0;
        buffer[3] = 0;
        return 4;
    }

    public static bool TryReadLocked(ReadOnlySpan<byte> buffer) => buffer.Length >= 4 && buffer[0] == Version && buffer[1] == KindLocked;

    public static int WriteOpenTerminal(Span<byte> buffer, byte runAs, int columns, int rows, bool describeOnly)
    {
        buffer[..8].Clear();
        buffer[0] = Version;
        buffer[1] = KindOpenTerminal;
        buffer[2] = describeOnly ? FlagDescribeOnly : (byte)0;
        buffer[3] = runAs;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[4..], (ushort)Math.Clamp(columns, 1, 1000));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[6..], (ushort)Math.Clamp(rows, 1, 1000));
        return 8;
    }

    /// <summary>Reads a shell request; anything out of range is refused here rather than trusted further in.</summary>
    public static bool TryReadOpenTerminal(ReadOnlySpan<byte> buffer, out byte runAs, out int columns, out int rows, out bool describeOnly)
    {
        runAs = 0;
        columns = rows = 0;
        describeOnly = false;
        if (buffer.Length != 8 || buffer[0] != Version || buffer[1] != KindOpenTerminal || buffer[3] > 1)
        {
            return false;
        }

        describeOnly = (buffer[2] & FlagDescribeOnly) != 0;
        runAs = buffer[3];
        columns = BinaryPrimitives.ReadUInt16LittleEndian(buffer[4..]);
        rows = BinaryPrimitives.ReadUInt16LittleEndian(buffer[6..]);
        return columns is >= 1 and <= 1000 && rows is >= 1 and <= 1000;
    }

    public static int WriteTerminal(Span<byte> buffer, int handle, string identity, bool fdAttached)
    {
        buffer[0] = Version;
        buffer[1] = KindTerminal;
        buffer[2] = fdAttached ? FlagFdAttached : (byte)0;
        buffer[3] = 0;
        BinaryPrimitives.WriteInt32LittleEndian(buffer[4..], handle);
        int written = Encoding.UTF8.GetBytes(identity.AsSpan(), buffer[8..]);
        return 8 + written;
    }

    public static bool TryReadTerminal(ReadOnlySpan<byte> buffer, out int handle, out string identity, out bool fdAttached)
    {
        handle = 0;
        identity = string.Empty;
        fdAttached = false;
        if (buffer.Length < 8 || buffer[0] != Version || buffer[1] != KindTerminal)
        {
            return false;
        }

        fdAttached = (buffer[2] & FlagFdAttached) != 0;
        handle = BinaryPrimitives.ReadInt32LittleEndian(buffer[4..]);
        identity = Encoding.UTF8.GetString(buffer[8..]);
        return true;
    }

    public static int WriteTerminalSignal(Span<byte> buffer, int handle, int signal)
    {
        buffer[..8].Clear();
        buffer[0] = Version;
        buffer[1] = KindTerminalSignal;
        buffer[2] = (byte)signal;
        BinaryPrimitives.WriteInt32LittleEndian(buffer[4..], handle);
        return 8;
    }

    /// <summary>Only the signals a terminal needs: 0 (ask), SIGHUP, SIGKILL, SIGTERM. Anything else is not a request this channel carries.</summary>
    public static bool TryReadTerminalSignal(ReadOnlySpan<byte> buffer, out int handle, out int signal)
    {
        handle = 0;
        signal = 0;
        if (buffer.Length != 8 || buffer[0] != Version || buffer[1] != KindTerminalSignal || buffer[2] is not (0 or 1 or 9 or 15))
        {
            return false;
        }

        signal = buffer[2];
        handle = BinaryPrimitives.ReadInt32LittleEndian(buffer[4..]);
        return true;
    }

    public static int WriteTerminalState(Span<byte> buffer, int handle, bool exited, int code)
    {
        buffer[..12].Clear();
        buffer[0] = Version;
        buffer[1] = KindTerminalState;
        buffer[2] = exited ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32LittleEndian(buffer[4..], handle);
        BinaryPrimitives.WriteInt32LittleEndian(buffer[8..], code);
        return 12;
    }

    public static bool TryReadTerminalState(ReadOnlySpan<byte> buffer, out int handle, out bool exited, out int code)
    {
        handle = code = 0;
        exited = false;
        if (buffer.Length < 12 || buffer[0] != Version || buffer[1] != KindTerminalState)
        {
            return false;
        }

        exited = buffer[2] != 0;
        handle = BinaryPrimitives.ReadInt32LittleEndian(buffer[4..]);
        code = BinaryPrimitives.ReadInt32LittleEndian(buffer[8..]);
        return true;
    }

    /// <summary>A refusal by the daemon's own policy: an error the engine answers by offering its own account's shell instead.</summary>
    public static int WriteRefusal(Span<byte> buffer, string message)
    {
        int n = WriteError(buffer, message);
        buffer[2] = FlagRefused;
        return n;
    }

    public static bool IsRefusal(ReadOnlySpan<byte> buffer) => buffer.Length >= 4 && buffer[0] == Version && buffer[1] == KindError && (buffer[2] & FlagRefused) != 0;

    public static int WriteAgentPoll(Span<byte> buffer)
    {
        buffer[0] = Version;
        buffer[1] = KindAgentPoll;
        buffer[2] = 0;
        buffer[3] = 0;
        return 4;
    }

    public static bool TryReadAgentPoll(ReadOnlySpan<byte> buffer) => buffer.Length == 4 && buffer[0] == Version && buffer[1] == KindAgentPoll;

    public static int WriteAgent(Span<byte> buffer, uint uid, string session, bool fdAttached)
    {
        buffer[..8].Clear();
        buffer[0] = Version;
        buffer[1] = KindAgent;
        buffer[2] = fdAttached ? FlagFdAttached : (byte)0;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[4..], uid);
        if (Encoding.UTF8.GetByteCount(session) > MaxSessionId)
        {
            session = string.Empty;
        }

        return 8 + Encoding.UTF8.GetBytes(session.AsSpan(), buffer[8..]);
    }

    public static bool TryReadAgent(ReadOnlySpan<byte> buffer, out uint uid, out string session, out bool fdAttached)
    {
        uid = 0;
        session = string.Empty;
        fdAttached = false;
        if (buffer.Length < 8 || buffer.Length > 8 + MaxSessionId || buffer[0] != Version || buffer[1] != KindAgent)
        {
            return false;
        }

        fdAttached = (buffer[2] & FlagFdAttached) != 0;
        uid = BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..]);
        session = Encoding.UTF8.GetString(buffer[8..]);
        return true;
    }

    public static byte KindOf(ReadOnlySpan<byte> buffer) => buffer.Length >= 2 && buffer[0] == Version ? buffer[1] : (byte)0;
}

/// <summary>One scanout framebuffer, as GETFB2 and the plane properties describe it.</summary>
public readonly record struct DrmFrameInfo
{
    /// <summary>Bumped by the daemon whenever the CRTC, connector, mode, format or modifier changes.</summary>
    public uint Generation { get; init; }

    public uint FbId { get; init; }

    public uint CrtcId { get; init; }

    public uint ConnectorId { get; init; }

    /// <summary>
    /// The connector's type as the kernel numbers it (15 is Virtual, 11 HDMI-A): with <see cref="ConnectorTypeId"/>, the
    /// name a compositor knows the monitor by, which the engine needs to find this picture in the compositor's layout.
    /// </summary>
    public uint ConnectorType { get; init; }

    /// <summary>Which connector of its type this is, from 1 (the 1 of Virtual-1); 0 when the daemon did not say.</summary>
    public uint ConnectorTypeId { get; init; }

    public uint Fourcc { get; init; }

    public ulong Modifier { get; init; }

    public byte PlaneCount { get; init; }

    public uint Width { get; init; }

    public uint Height { get; init; }

    /// <summary>The part of the framebuffer this CRTC shows, in pixels (the plane's SRC_* properties, already shifted).</summary>
    public uint SrcX { get; init; }

    public uint SrcY { get; init; }

    public uint SrcWidth { get; init; }

    public uint SrcHeight { get; init; }

    /// <summary>The plane's rotation property bits, 0 when the driver has none.</summary>
    public uint Rotation { get; init; }

    public uint Pitch { get; init; }

    public uint Offset { get; init; }

    /// <summary>The CRTC's vblank count, when the driver reports one; otherwise a counter of polls.</summary>
    public ulong Sequence { get; init; }

    public long TimestampNs { get; init; }
}
