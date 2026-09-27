using System.Runtime.InteropServices;
using DeskPair.Platform.Linux.Native;
using DeskPair.Platform.Linux.Terminal;

namespace DeskPair.Platform.Linux.Capture.Drm;

/// <summary>What one poll of the daemon came back with.</summary>
public enum DrmPollKind
{
    Frame,
    NoScanout,
    Error,
    Closed,
}

/// <summary>
/// A framebuffer the engine holds a descriptor for, mapped into its own memory. Read only by a
/// <see cref="DrmFrameReader"/>, inside the poll that named it: the next poll, whichever thread makes it, may unmap it.
/// </summary>
public sealed unsafe class DrmMappedBuffer : IDisposable
{
    public DrmMappedBuffer(int fd, nuint length)
    {
        Fd = fd;
        Length = length;
        Base = (byte*)Mman.mmap(null, length, Mman.ProtRead, Mman.MapShared, fd, 0);
        if (Base == Mman.MapFailed)
        {
            int errno = Marshal.GetLastPInvokeError();
            _ = UnixSocketMsg.close(fd);
            throw new IOException($"mmap of the scanout dma-buf failed: errno {errno}");
        }
    }

    public int Fd { get; }

    public byte* Base { get; }

    public nuint Length { get; }

    public void Dispose()
    {
        _ = Mman.munmap(Base, Length);
        _ = UnixSocketMsg.close(Fd);
    }
}

/// <summary>
/// Reads the picture out of the buffer a poll named, while that buffer is certain to stay mapped; true when it made
/// something to hand on.
/// </summary>
public delegate bool DrmFrameReader(in DrmFrameInfo frame, DrmMappedBuffer buffer);

public readonly record struct DrmPollResult(
    DrmPollKind Kind,
    DrmFrameInfo Frame,
    uint Generation,
    bool CrtcOff,
    string? Error)
{
    /// <summary>
    /// With <see cref="DrmPollKind.NoScanout"/>: the daemon has no display hardware to read, so waking the
    /// display is pointless and the honest display list is empty.
    /// </summary>
    public bool NoHardware { get; init; }

    /// <summary>What the reader given to the poll returned; false when there was none or it did not run.</summary>
    public bool Read { get; init; }
}

/// <summary>
/// The engine's end of the socketpair: asks what the screen is, keeps the descriptors it is handed,
/// and maps each one once.
///
/// A compositor flips between two or three buffers for the life of a mode, so after the first few polls
/// there is nothing to receive and nothing to map -- each poll is a hundred-byte round trip. The maps kept
/// are the <see cref="DrmWire.KnownFbSlots"/> buffers shown most recently, so every one of them is named in
/// the next poll and none is sent again; one not shown for a while is unmapped when a newer one arrives.
/// Unbounded, the maps beyond the ones a poll can name were sent and replaced over and over. A new
/// generation (a mode change, a hotplug, a rotation) drops every map; the capturer reports that as the
/// desktop having switched, and is rebuilt.
///
/// A map is read only inside <see cref="Poll(DrmFrameReader?)"/>. The enumerator and the capturer share
/// this channel from different threads, and any poll may unmap a buffer: read after the gate was let go,
/// a copy ran into memory the other thread had just unmapped, and the engine died of SIGSEGV. A GNOME
/// desktop on two monitors did it within a minute, since every click asks the enumerator for the display.
/// </summary>
public sealed class DrmCaptureChannel : IDisposable
{
    private readonly int _socket;
    private readonly Dictionary<uint, DrmMappedBuffer> _maps = new();
    private readonly List<uint> _recent = new(DrmWire.KnownFbSlots + 1);
    private readonly byte[] _request = new byte[DrmWire.PollSize];
    private readonly byte[] _reply = new byte[512];
    private readonly object _gate = new();
    private uint _generation;
    private long _connector;
    private bool _closed;

    public DrmCaptureChannel(int socket)
    {
        _socket = socket;
    }

    /// <summary>The daemon's current generation as last heard; the capturer compares against the one it was built for.</summary>
    public uint Generation => _generation;

    /// <summary>
    /// The connector the last picture came from, as the kernel numbers it (type 15, 1 is Virtual-1); null before the
    /// first picture. Read without the gate -- the input thread asks it for every click, and must not wait out a
    /// frame's copy -- so it is kept in one word.
    /// </summary>
    public (uint Type, uint TypeId)? Connector
    {
        get
        {
            long packed = Volatile.Read(ref _connector);
            return packed == 0 ? null : ((uint)(packed >>> 32), (uint)packed);
        }
    }

    /// <summary>
    /// One request and its answer, atomically: the enumerator and the capturer share this channel, and a
    /// poll from each on two threads would read each other's replies.
    /// </summary>
    public DrmPollResult Poll() => Poll(null);

    /// <summary>
    /// One request and its answer, and with <paramref name="read"/> the picture read out of the buffer the answer
    /// names before any other poll can unmap it.
    /// </summary>
    public DrmPollResult Poll(DrmFrameReader? read)
    {
        lock (_gate)
        {
            DrmPollResult result = PollLocked(out DrmMappedBuffer? map);
            return read is not null && map is not null ? result with { Read = read(result.Frame, map) } : result;
        }
    }

    /// <summary>The descriptors mapped now, which is at most <see cref="DrmWire.KnownFbSlots"/>.</summary>
    internal int MapCount
    {
        get
        {
            lock (_gate)
            {
                return _maps.Count;
            }
        }
    }

    private DrmPollResult PollLocked(out DrmMappedBuffer? map)
    {
        map = null;
        if (_closed)
        {
            return new DrmPollResult(DrmPollKind.Closed, default, _generation, false, "the capture channel is closed");
        }

        Span<uint> known = stackalloc uint[DrmWire.KnownFbSlots];
        int i = 0;
        foreach (uint id in _recent)
        {
            if (i < known.Length)
            {
                known[i++] = id;
            }
        }

        int n = DrmWire.WritePoll(_request, known[..i]);
        Span<int> fds = stackalloc int[UnixSocketMsg.MaxFds];
        int length;
        int fdCount;
        try
        {
            UnixSocketMsg.Send(_socket, _request.AsSpan(0, n), []);
            length = UnixSocketMsg.Receive(_socket, _reply, fds, out fdCount);
        }
        catch (IOException e)
        {
            _closed = true;
            return new DrmPollResult(DrmPollKind.Closed, default, _generation, false, e.Message);
        }

        if (length == 0)
        {
            _closed = true;
            return new DrmPollResult(DrmPollKind.Closed, default, _generation, false, "the daemon closed the capture channel");
        }

        ReadOnlySpan<byte> reply = _reply.AsSpan(0, length);
        switch (DrmWire.KindOf(reply))
        {
            case DrmWire.KindFrame when DrmWire.TryReadFrame(reply, out DrmFrameInfo frame, out bool fdAttached):
                if (frame.Generation != _generation)
                {
                    DropMaps();
                    _generation = frame.Generation;
                }

                if (fdAttached && fdCount > 0)
                {
                    // A descriptor for an id already mapped means the id now names a different buffer
                    // (the daemon checked the inode); the old map is of a buffer nothing shows any more.
                    if (_maps.Remove(frame.FbId, out DrmMappedBuffer? stale))
                    {
                        stale.Dispose();
                        _recent.Remove(frame.FbId);
                    }

                    for (int k = 1; k < fdCount; k++)
                    {
                        _ = UnixSocketMsg.close(fds[k]);
                    }

                    nuint size = (nuint)frame.Offset + (nuint)frame.Pitch * frame.Height;
                    _maps[frame.FbId] = new DrmMappedBuffer(fds[0], size);
                }

                if (!_maps.TryGetValue(frame.FbId, out map))
                {
                    return new DrmPollResult(DrmPollKind.Error, frame, _generation, false,
                        $"the daemon named framebuffer {frame.FbId} without ever sending its descriptor");
                }

                Shown(frame.FbId);
                if (frame.ConnectorTypeId != 0)
                {
                    Volatile.Write(ref _connector, ((long)frame.ConnectorType << 32) | frame.ConnectorTypeId);
                }

                return new DrmPollResult(DrmPollKind.Frame, frame, _generation, false, null);

            case DrmWire.KindNoScanout when DrmWire.TryReadNoScanout(reply, out uint generation, out bool off, out bool noHardware):
                if (generation != _generation)
                {
                    DropMaps();
                    _generation = generation;
                }

                return new DrmPollResult(DrmPollKind.NoScanout, default, _generation, off, null) { NoHardware = noHardware };

            case DrmWire.KindError when DrmWire.TryReadError(reply, out string message):
                return new DrmPollResult(DrmPollKind.Error, default, _generation, false, message);

            default:
                for (int k = 0; k < fdCount; k++)
                {
                    _ = UnixSocketMsg.close(fds[k]);
                }

                return new DrmPollResult(DrmPollKind.Error, default, _generation, false,
                    $"an unreadable {length}-byte message of kind {DrmWire.KindOf(reply)} from the daemon");
        }
    }

    /// <summary>
    /// Asks the daemon for a shell (or, with <paramref name="describeOnly"/>, whose shell it would be).
    /// Shares the gate with <see cref="Poll()"/>; starting one takes the daemon a moment, during which a
    /// capture poll waits.
    /// </summary>
    public DaemonTerminalReply OpenTerminal(byte runAs, int columns, int rows, bool describeOnly)
    {
        lock (_gate)
        {
            Span<byte> request = stackalloc byte[8];
            int n = DrmWire.WriteOpenTerminal(request, runAs, columns, rows, describeOnly);
            if (Exchange(request[..n], out int length, out int fd) is { } problem)
            {
                return DaemonTerminalReply.Failed(problem);
            }

            ReadOnlySpan<byte> reply = _reply.AsSpan(0, length);
            if (DrmWire.TryReadTerminal(reply, out int handle, out string identity, out bool attached))
            {
                string[] parts = identity.Split('\n', 2);
                if (!describeOnly && (!attached || fd < 0))
                {
                    return DaemonTerminalReply.Failed("the daemon started a shell without sending its terminal");
                }

                return DaemonTerminalReply.Granted(handle, parts[0], parts.Length > 1 ? parts[1] : string.Empty, fd);
            }

            if (fd >= 0)
            {
                _ = UnixSocketMsg.close(fd);
            }

            DrmWire.TryReadError(reply, out string message);
            return DrmWire.IsRefusal(reply) ? DaemonTerminalReply.Refused(message) : DaemonTerminalReply.Failed(message);
        }
    }

    /// <summary>
    /// A session agent's socket, when the daemon has started one since this engine last asked: the signed-in user's
    /// Wayland desktop, served through the portal by an agent running as them. The caller owns the descriptor. Null
    /// when none waits, or the channel failed. Shares the gate with <see cref="Poll()"/>.
    /// </summary>
    public (int Socket, uint Uid, string Session)? TakeAgent()
    {
        lock (_gate)
        {
            Span<byte> request = stackalloc byte[4];
            int n = DrmWire.WriteAgentPoll(request);
            if (Exchange(request[..n], out int length, out int fd) is not null)
            {
                return null;
            }

            if (DrmWire.TryReadAgent(_reply.AsSpan(0, length), out uint uid, out string session, out bool attached) && attached && fd >= 0)
            {
                return (fd, uid, session);
            }

            if (fd >= 0)
            {
                _ = UnixSocketMsg.close(fd);
            }

            return null;
        }
    }

    /// <summary>Signals a shell the daemon started, or with 0 only asks; null when the channel itself failed.</summary>
    public (bool Exited, int Code)? TerminalSignal(int handle, int signal)
    {
        lock (_gate)
        {
            Span<byte> request = stackalloc byte[8];
            int n = DrmWire.WriteTerminalSignal(request, handle, signal);
            if (Exchange(request[..n], out int length, out int fd) is not null)
            {
                return null;
            }

            if (fd >= 0)
            {
                _ = UnixSocketMsg.close(fd);
            }

            return DrmWire.TryReadTerminalState(_reply.AsSpan(0, length), out _, out bool exited, out int code) ? (exited, code) : null;
        }
    }

    /// <summary>One request and its reply, with at most one descriptor. Null, or why the channel failed. Call under the gate.</summary>
    private string? Exchange(ReadOnlySpan<byte> request, out int length, out int fd)
    {
        length = 0;
        fd = -1;
        if (_closed)
        {
            return "the capture channel is closed";
        }

        Span<int> fds = stackalloc int[UnixSocketMsg.MaxFds];
        int fdCount;
        try
        {
            UnixSocketMsg.Send(_socket, request, []);
            length = UnixSocketMsg.Receive(_socket, _reply, fds, out fdCount);
        }
        catch (IOException e)
        {
            _closed = true;
            return e.Message;
        }

        for (int k = 1; k < fdCount; k++)
        {
            _ = UnixSocketMsg.close(fds[k]);
        }

        fd = fdCount > 0 ? fds[0] : -1;
        if (length == 0)
        {
            _closed = true;
            return "the daemon closed the capture channel";
        }

        return null;
    }

    /// <summary>
    /// Asks the daemon to lock the seat. Null when it did; otherwise the daemon's reason, in words for the
    /// log. Shares the gate with <see cref="Poll()"/>: one request in flight at a time on this socket.
    /// </summary>
    public string? Lock()
    {
        lock (_gate)
        {
            if (_closed)
            {
                return "the capture channel is closed";
            }

            Span<byte> request = stackalloc byte[4];
            int n = DrmWire.WriteLock(request);
            Span<int> fds = stackalloc int[UnixSocketMsg.MaxFds];
            int length;
            int fdCount;
            try
            {
                UnixSocketMsg.Send(_socket, request[..n], []);
                length = UnixSocketMsg.Receive(_socket, _reply, fds, out fdCount);
            }
            catch (IOException e)
            {
                _closed = true;
                return e.Message;
            }

            for (int k = 0; k < fdCount; k++)
            {
                _ = UnixSocketMsg.close(fds[k]); // a lock reply carries none; anything here is a mistake
            }

            if (length == 0)
            {
                _closed = true;
                return "the daemon closed the capture channel";
            }

            ReadOnlySpan<byte> reply = _reply.AsSpan(0, length);
            if (DrmWire.TryReadLocked(reply))
            {
                return null;
            }

            return DrmWire.TryReadError(reply, out string message)
                ? message
                : $"an unreadable {length}-byte message of kind {DrmWire.KindOf(reply)} from the daemon";
        }
    }

    /// <summary>Puts a buffer first among the recently shown, and unmaps the one that falls off the end. Call under the gate.</summary>
    private void Shown(uint fbId)
    {
        _recent.Remove(fbId);
        _recent.Insert(0, fbId);
        while (_recent.Count > DrmWire.KnownFbSlots)
        {
            uint oldest = _recent[^1];
            _recent.RemoveAt(_recent.Count - 1);
            if (_maps.Remove(oldest, out DrmMappedBuffer? map))
            {
                map.Dispose();
            }
        }
    }

    private void DropMaps()
    {
        foreach (DrmMappedBuffer map in _maps.Values)
        {
            map.Dispose();
        }

        _maps.Clear();
        _recent.Clear();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            DropMaps();
            _closed = true;
            _ = UnixSocketMsg.close(_socket);
        }
    }
}
