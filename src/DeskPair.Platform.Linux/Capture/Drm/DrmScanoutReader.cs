using System.Runtime.InteropServices;
using DeskPair.Platform.Linux.Native;
using DrmNative = DeskPair.Platform.Linux.Native.Drm;
using Microsoft.Extensions.Logging;

namespace DeskPair.Platform.Linux.Capture.Drm;

/// <summary>
/// The root half of seeing the screen: what the CRTC is scanning out, as a dma-buf.
///
/// It does exactly what needs root and nothing more. Open the card, ask which framebuffer the primary
/// plane of the active CRTC is showing, turn its GEM handle into a descriptor, and hand that descriptor
/// over. No pixel is read here, no GPU library is loaded here -- <c>libdrm.so.2</c> is a wrapper over
/// ioctls and that is the whole of what this process links. The reading, and Mesa, belong to the engine
/// on the other end of the socketpair.
///
/// Descriptors are cached per framebuffer id, because a double-buffered compositor flips between two
/// buffers for the life of a mode and asking for the same buffer sixty times a second would mint sixty
/// GEM handles a second. A change to anything that describes the picture -- CRTC, connector, mode,
/// format, modifier, crop, rotation -- bumps <see cref="Generation"/> and drops the cache, and the engine
/// answers a new generation by rebuilding its capturer, the way it already does for a resize under X11.
/// </summary>
public sealed unsafe class DrmScanoutReader : IDisposable
{
    private readonly ILogger _log;
    private readonly int _fd;
    private readonly Dictionary<uint, (ulong Inode, int Fd)> _exported = new();

    // The ids in _exported, most recently shown first; the ones past HeldExports are let go.
    private readonly List<uint> _exportOrder = new(HeldExports + 1);
    private int _reuses;
    private DrmFrameInfo _shape;
    private uint _generation;
    private ulong _polls;
    private bool _wasOff;

    public DrmScanoutReader(string cardPath, ILogger log)
    {
        _log = log;
        _fd = LibC.open(cardPath, LibC.O_RDWR | LibC.O_CLOEXEC);
        if (_fd < 0)
        {
            throw new IOException($"Could not open {cardPath}: errno {Marshal.GetLastPInvokeError()}");
        }

        nint version = DrmNative.drmGetVersion(_fd);
        if (version != 0)
        {
            var v = (DrmNative.Version*)version;
            Driver = Marshal.PtrToStringUTF8(v->Name, v->NameLength);
            DrmNative.drmFreeVersion(version);
        }

        // The first process to open a card after boot is made its DRM master by the kernel, and this
        // daemon starts before the display manager. A master that is not the compositor is a compositor
        // that cannot start: on the verification machine gnome-shell logged "Failed to open gpu
        // /dev/dri/card1: EBUSY", gdm gave up on the greeter, and the machine booted to a black screen
        // that this daemon then faithfully served. Master is never needed here -- GETFB2's handles come
        // with CAP_SYS_ADMIN, which root has -- so whatever the kernel handed out is given straight back.
        // Dropping it when it was never held fails harmlessly.
        if (DrmNative.drmDropMaster(_fd) == 0)
        {
            log.LogInformation("Opened {Card} before any compositor and was made DRM master; dropped it so the compositor can take it", cardPath);
        }

        // Without these the primary plane is not enumerable at all, and neither needs master to set.
        _ = DrmNative.drmSetClientCap(_fd, DrmNative.ClientCapUniversalPlanes, 1);
        _ = DrmNative.drmSetClientCap(_fd, DrmNative.ClientCapAtomic, 1);
        CardPath = cardPath;
    }

    public string CardPath { get; }

    public string Driver { get; } = "unknown";

    public uint Generation => _generation;

    /// <summary>
    /// What is on the primary plane right now, plus a dma-buf descriptor for it when the caller does not
    /// already hold one (as listed in <paramref name="knownFbIds"/>). Null when no CRTC is scanning out:
    /// an idle login screen turns the display off entirely and there is then nothing to read, which is
    /// not the same thing as a black picture and is reported as such.
    /// </summary>
    public DrmFrameInfo? Read(ReadOnlySpan<uint> knownFbIds, out int fdToSend, out bool crtcOff)
    {
        fdToSend = -1;
        crtcOff = false;
        _polls++;

        (DrmNative.ModePlane plane, uint planeType, uint srcX, uint srcY, uint srcW, uint srcH, uint rotation)? primary = FindPrimaryPlane();
        if (primary is null)
        {
            crtcOff = true;
            NoteOff();
            return null;
        }

        (DrmNative.ModePlane plane, _, uint sx, uint sy, uint sw, uint sh, uint rot) = primary.Value;
        nint fbPtr = DrmNative.drmModeGetFB2(_fd, plane.FbId);
        if (fbPtr == 0)
        {
            crtcOff = true;
            NoteOff();
            return null;
        }

        try
        {
            var fb = (DrmNative.ModeFB2*)fbPtr;
            byte planes = 0;
            for (int i = 0; i < 4; i++)
            {
                if (fb->Handles[i] != 0)
                {
                    planes++;
                }
            }

            ScanoutConnector connector = ConnectorFor(plane.CrtcId);
            var info = new DrmFrameInfo
            {
                FbId = fb->FbId,
                CrtcId = plane.CrtcId,
                ConnectorId = connector.Id,
                ConnectorType = connector.Type,
                ConnectorTypeId = connector.TypeId,
                Fourcc = fb->PixelFormat,
                Modifier = fb->Modifier,
                PlaneCount = planes,
                Width = fb->Width,
                Height = fb->Height,
                SrcX = sx,
                SrcY = sy,
                SrcWidth = sw == 0 ? fb->Width : sw,
                SrcHeight = sh == 0 ? fb->Height : sh,
                Rotation = rot,
                Pitch = fb->Pitches[0],
                Offset = fb->Offsets[0],
                Sequence = _polls,
                TimestampNs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000,
            };

            if (ShapeChanged(info))
            {
                _generation++;
                DropExports();
                _reuses = 0;
                _shape = info;
                _log.LogInformation(
                    "Scanout generation {Generation}: {Description}, {Width}x{Height} crop {SrcW}x{SrcH}+{SrcX}+{SrcY} rotation {Rotation}",
                    _generation,
                    DrmFormatSupport.Describe(ConnectorName(info.ConnectorId), Driver, info.Fourcc, info.Modifier, planes),
                    info.Width, info.Height, info.SrcWidth, info.SrcHeight, info.SrcX, info.SrcY, info.Rotation);
            }

            info = info with { Generation = _generation };
            _wasOff = false;

            bool known = false;
            foreach (uint id in knownFbIds)
            {
                if (id == info.FbId)
                {
                    known = true;
                    break;
                }
            }

            fdToSend = Export(fb->Handles[0], info.FbId, known);

            // Every GETFB2 minted these handles afresh; they are ours to close whether or not we exported.
            for (int i = 0; i < 4; i++)
            {
                if (fb->Handles[i] != 0)
                {
                    _ = DrmNative.GemClose(_fd, fb->Handles[i]);
                }
            }

            return info;
        }
        finally
        {
            DrmNative.drmModeFreeFB2(fbPtr);
        }
    }

    private void NoteOff()
    {
        if (!_wasOff)
        {
            _wasOff = true;
            _log.LogInformation("No scanout: the display is off or nothing is driving it. Waiting rather than sending a black picture.");
        }
    }

    private bool ShapeChanged(in DrmFrameInfo info) =>
        info.CrtcId != _shape.CrtcId || info.ConnectorId != _shape.ConnectorId ||
        info.Fourcc != _shape.Fourcc || info.Modifier != _shape.Modifier ||
        info.Width != _shape.Width || info.Height != _shape.Height ||
        info.SrcX != _shape.SrcX || info.SrcY != _shape.SrcY ||
        info.SrcWidth != _shape.SrcWidth || info.SrcHeight != _shape.SrcHeight ||
        info.Rotation != _shape.Rotation || info.Pitch != _shape.Pitch || info.PlaneCount != _shape.PlaneCount;

    /// <summary>
    /// The descriptor to send for this framebuffer, or -1 when the engine already holds the right one.
    ///
    /// "The right one" is decided by the buffer, not by the id. Framebuffer ids are recycled by the kernel:
    /// when the compositor frees the lock screen's two buffers and allocates two for the desktop, the new
    /// ones commonly get the old numbers -- and an engine that trusted the number kept its map of the old
    /// buffer, which this daemon's own descriptor was keeping alive, and served the lock screen on every
    /// other frame of the desktop. Watched on the second VM after a local unlock. So every poll exports
    /// the buffer and asks its inode, which is the same for the same buffer and different for a new one;
    /// an id whose inode has moved is sent again, and the engine replaces its map.
    /// </summary>
    private int Export(uint handle, uint fbId, bool engineKnowsId)
    {
        if (DrmNative.drmPrimeHandleToFD(_fd, handle, LibC.O_CLOEXEC, out int fd) != 0 || fd < 0)
        {
            throw new IOException($"drmPrimeHandleToFD failed for fb {fbId}: errno {Marshal.GetLastPInvokeError()}");
        }

        ulong inode = LibC.InodeOf(fd);
        if (_exported.TryGetValue(fbId, out (ulong Inode, int Fd) held) && held.Inode == inode && inode != 0)
        {
            // The same buffer as before. The engine has it if it says so; if it does not (a restarted
            // engine), the held descriptor is sent again.
            _ = UnixSocketMsg.close(fd);
            Held(fbId);
            return engineKnowsId ? -1 : held.Fd;
        }

        if (_exported.TryGetValue(fbId, out held))
        {
            // Said once per generation at a level somebody reads: a compositor tearing down a lock screen
            // reallocates several buffers within a hundred milliseconds, and each one is a line here.
            _log.Log(_reuses++ == 0 ? LogLevel.Information : LogLevel.Debug,
                "Framebuffer id {FbId} now names a different buffer (inode {Old} -> {New}); sending it again", fbId, held.Inode, inode);
            _ = UnixSocketMsg.close(held.Fd);
        }

        _exported[fbId] = (inode, fd);
        Held(fbId);
        return fd;
    }

    /// <summary>
    /// How many framebuffers' descriptors are held: twice what the engine keeps mapped. Each one keeps its buffer
    /// alive, and a compositor frees its lock screen's buffers on unlock and allocates new ones, so without a limit
    /// the daemon kept every buffer it had ever shown until the next mode change.
    /// </summary>
    private const int HeldExports = 2 * DrmWire.KnownFbSlots;

    /// <summary>Puts a framebuffer first among the recently shown, and lets go of the one that falls off the end.</summary>
    private void Held(uint fbId)
    {
        _exportOrder.Remove(fbId);
        _exportOrder.Insert(0, fbId);
        while (_exportOrder.Count > HeldExports)
        {
            uint oldest = _exportOrder[^1];
            _exportOrder.RemoveAt(_exportOrder.Count - 1);
            if (_exported.Remove(oldest, out (ulong Inode, int Fd) gone))
            {
                _ = UnixSocketMsg.close(gone.Fd);
            }
        }
    }

    private void DropExports()
    {
        foreach ((_, int fd) in _exported.Values)
        {
            _ = UnixSocketMsg.close(fd);
        }

        _exported.Clear();
        _exportOrder.Clear();
    }

    /// <summary>Whether a descriptor for this framebuffer has been exported (and so sent) already.</summary>
    public bool HasExported(uint fbId) => _exported.ContainsKey(fbId);

    private (DrmNative.ModePlane, uint, uint, uint, uint, uint, uint)? FindPrimaryPlane()
    {
        nint resPtr = DrmNative.drmModeGetPlaneResources(_fd);
        if (resPtr == 0)
        {
            return null;
        }

        try
        {
            var res = (DrmNative.ModePlaneRes*)resPtr;
            var ids = (uint*)res->Planes;
            for (uint i = 0; i < res->CountPlanes; i++)
            {
                nint planePtr = DrmNative.drmModeGetPlane(_fd, ids[i]);
                if (planePtr == 0)
                {
                    continue;
                }

                try
                {
                    DrmNative.ModePlane plane = *(DrmNative.ModePlane*)planePtr;
                    if (plane.CrtcId == 0 || plane.FbId == 0)
                    {
                        continue;
                    }

                    Dictionary<string, ulong> props = Properties(plane.PlaneId, DrmNative.ObjectPlane);
                    if (!props.TryGetValue("type", out ulong type) || type != DrmNative.PlaneTypePrimary)
                    {
                        continue;
                    }

                    // SRC_* are 16.16 fixed point: the crop in whole pixels is the top half.
                    return (
                        plane,
                        (uint)type,
                        (uint)(props.GetValueOrDefault("SRC_X") >> 16),
                        (uint)(props.GetValueOrDefault("SRC_Y") >> 16),
                        (uint)(props.GetValueOrDefault("SRC_W") >> 16),
                        (uint)(props.GetValueOrDefault("SRC_H") >> 16),
                        (uint)props.GetValueOrDefault("rotation"));
                }
                finally
                {
                    DrmNative.drmModeFreePlane(planePtr);
                }
            }

            return null;
        }
        finally
        {
            DrmNative.drmModeFreePlaneResources(resPtr);
        }
    }

    private Dictionary<string, ulong> Properties(uint objectId, uint objectType)
    {
        var result = new Dictionary<string, ulong>(StringComparer.Ordinal);
        nint propsPtr = DrmNative.drmModeObjectGetProperties(_fd, objectId, objectType);
        if (propsPtr == 0)
        {
            return result;
        }

        try
        {
            var props = (DrmNative.ModeObjectProperties*)propsPtr;
            var ids = (uint*)props->Props;
            var values = (ulong*)props->PropValues;
            for (uint i = 0; i < props->CountProps; i++)
            {
                nint propPtr = DrmNative.drmModeGetProperty(_fd, ids[i]);
                if (propPtr == 0)
                {
                    continue;
                }

                try
                {
                    var prop = (DrmNative.ModeProperty*)propPtr;
                    result[Marshal.PtrToStringUTF8((nint)prop->Name) ?? string.Empty] = values[i];
                }
                finally
                {
                    DrmNative.drmModeFreeProperty(propPtr);
                }
            }
        }
        finally
        {
            DrmNative.drmModeFreeObjectProperties(propsPtr);
        }

        return result;
    }

    /// <summary>How long which connector a CRTC drives is believed before it is asked again: a monitor can be swapped on the same CRTC.</summary>
    private const long ConnectorsBelievedMs = 5000;

    private readonly Dictionary<uint, ScanoutConnector> _connectorByCrtc = new();
    private long _connectorsAsked;

    /// <summary>A connector as the kernel names it: its object id, and its type and number ("Virtual" 1 is Virtual-1).</summary>
    private readonly record struct ScanoutConnector(uint Id, uint Type, uint TypeId)
    {
        public string Name => $"{DrmNative.ConnectorTypeName(Type)}-{TypeId}";
    }

    /// <summary>
    /// The connector this CRTC drives, so the display can be named as xrandr names it and the engine can tell which
    /// of the compositor's monitors the picture is. Found through each connected connector's encoder, which says
    /// the CRTC driving it: with two monitors, "the first connected connector" was the other monitor's as often as
    /// not. A driver that reports no encoder for any gets the first connected connector, as before.
    /// </summary>
    private ScanoutConnector ConnectorFor(uint crtcId)
    {
        long now = Environment.TickCount64;
        if (now - _connectorsAsked > ConnectorsBelievedMs)
        {
            _connectorByCrtc.Clear();
            _connectorsAsked = now;
        }

        if (_connectorByCrtc.TryGetValue(crtcId, out ScanoutConnector known))
        {
            return known;
        }

        nint resPtr = DrmNative.drmModeGetResources(_fd);
        if (resPtr == 0)
        {
            return default;
        }

        ScanoutConnector? first = null;
        try
        {
            var res = (DrmNative.ModeRes*)resPtr;
            var connectors = (uint*)res->Connectors;
            for (int i = 0; i < res->CountConnectors; i++)
            {
                nint connPtr = DrmNative.drmModeGetConnectorCurrent(_fd, connectors[i]);
                if (connPtr == 0)
                {
                    continue;
                }

                try
                {
                    var conn = (DrmNative.ModeConnector*)connPtr;
                    if (conn->Connection != DrmNative.Connected || conn->EncoderId == 0)
                    {
                        continue;
                    }

                    var candidate = new ScanoutConnector(conn->ConnectorId, conn->ConnectorType, conn->ConnectorTypeId);
                    first ??= candidate;
                    if (CrtcOfEncoder(conn->EncoderId) == crtcId)
                    {
                        _connectorByCrtc[crtcId] = candidate;
                        return candidate;
                    }
                }
                finally
                {
                    DrmNative.drmModeFreeConnector(connPtr);
                }
            }
        }
        finally
        {
            DrmNative.drmModeFreeResources(resPtr);
        }

        ScanoutConnector fallback = first ?? default;
        _connectorByCrtc[crtcId] = fallback;
        return fallback;
    }

    private uint CrtcOfEncoder(uint encoderId)
    {
        nint encoder = DrmNative.drmModeGetEncoder(_fd, encoderId);
        if (encoder == 0)
        {
            return 0;
        }

        uint crtc = ((DrmNative.ModeEncoder*)encoder)->CrtcId;
        DrmNative.drmModeFreeEncoder(encoder);
        return crtc;
    }

    /// <summary>Whether any connector on this card has a display plugged in: how the daemon picks a card.</summary>
    public bool HasConnectedDisplay()
    {
        nint resPtr = DrmNative.drmModeGetResources(_fd);
        if (resPtr == 0)
        {
            return false;
        }

        try
        {
            var res = (DrmNative.ModeRes*)resPtr;
            var connectors = (uint*)res->Connectors;
            for (int i = 0; i < res->CountConnectors; i++)
            {
                nint connPtr = DrmNative.drmModeGetConnector(_fd, connectors[i]);
                if (connPtr == 0)
                {
                    continue;
                }

                bool connected = ((DrmNative.ModeConnector*)connPtr)->Connection == DrmNative.Connected;
                DrmNative.drmModeFreeConnector(connPtr);
                if (connected)
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            DrmNative.drmModeFreeResources(resPtr);
        }
    }

    public string ConnectorName(uint connectorId)
    {
        foreach (ScanoutConnector entry in _connectorByCrtc.Values)
        {
            if (entry.Id == connectorId && entry.Id != 0)
            {
                return entry.Name;
            }
        }

        return connectorId == 0 ? "unknown" : $"connector-{connectorId}";
    }

    public void Dispose()
    {
        DropExports();
        _ = UnixSocketMsg.close(_fd);
    }
}
