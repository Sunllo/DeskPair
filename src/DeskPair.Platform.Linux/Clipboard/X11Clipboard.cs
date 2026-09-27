using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using DeskPair.Platform.Abstractions.Clipboard;
using DeskPair.Platform.Linux.Native;

namespace DeskPair.Platform.Linux.Clipboard;

/// <summary>
/// The CLIPBOARD selection, text only. X11's clipboard is a live negotiation, not a store: to read you ask
/// the current owner to convert its content and it answers with an event; to "hold" the clipboard you become
/// the owner and answer everyone else's requests. A dedicated thread owns a display connection and runs that
/// event loop — replying to paste requests for content this side wrote, and noticing external changes through
/// XFixes so the other end of a session sees them.
///
/// Text is the case that matters and the case that is portable; images and rich text over X selections are a
/// larger, rarer job and are left to a follow-on. Reading a format this does not handle simply yields no item
/// for it, which the contract allows.
/// </summary>
public sealed class X11Clipboard : IClipboard, IFilePromiseClipboard
{
    /// <summary>
    /// How long a requestor is left waiting before we give up and refuse. X11 puts no deadline on an owner's
    /// reply, but the application that pasted is frozen until it comes, so one is imposed here.
    /// </summary>
    /// <summary>
    /// How long an unfinished INCR transfer is kept.
    /// </summary>
    /// <remarks>
    /// A requestor that dies mid-paste never deletes the property again, and we would otherwise keep
    /// selecting input on a window that no longer exists — a BadWindow nobody sees, because the error
    /// handler swallows it.
    /// </remarks>
    private static readonly TimeSpan IncrDeadline = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan PromiseDeadline = TimeSpan.FromMinutes(5);

    /// <summary>
    /// A paste of promised files that has been asked for and not yet answered. X11 cannot stream a selection
    /// and a URI must name a file that exists, so the bytes have to be on disk before the reply goes out.
    /// Answering early is silent corruption — the requestor reads a half-written file and reports success —
    /// and materialising inside the event loop would freeze it for every other requestor. So the request is
    /// recorded, the fetch runs elsewhere, and SelectionNotify follows when the files are really there.
    /// </summary>
    private sealed record Pending(nint Requestor, nint Selection, nint Target, nint Property, nuint Time, long DeadlineTicks);

    private readonly ILogger _log;
    private readonly Channel<IReadOnlyList<ClipboardItem>> _changes =
        Channel.CreateBounded<IReadOnlyList<ClipboardItem>>(new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.DropOldest });

    private readonly nint _dpy;
    private readonly nint _window;
    private readonly nint _clipboard;   // CLIPBOARD atom
    private readonly nint _utf8;        // UTF8_STRING atom
    private readonly nint _string;      // STRING atom, for clients that ask the old way
    private readonly nint _targets;     // TARGETS atom
    private readonly nint _timestamp;   // TIMESTAMP atom; ICCCM requires an owner to answer it
    private readonly nint _atom;        // the ATOM type, for the TARGETS reply
    private readonly nint _integer;     // the INTEGER type, for the TIMESTAMP reply
    private readonly nint _png;         // image/png
    private readonly nint _incr;        // INCR, the type that says "this arrives in pieces"
    private readonly nint _prop;        // the property we ask owners to write into
    private readonly int _xfixesEventBase;
    private readonly Thread _loop;
    private readonly CancellationTokenSource _cts = new();

    private readonly nint _uriList;     // text/uri-list, the RFC 2483 one
    private readonly nint _gnomeCopied; // x-special/gnome-copied-files
    private readonly nint _kdeCut;      // application/x-kde-cutselection
    private readonly Lock _promiseGate = new();
    private readonly List<Pending> _pending = [];

    /// <summary>How much of a payload goes into one property write; see <see cref="IncrChunkSize"/>.</summary>
    private readonly int _chunk;

    /// <summary>Transfers in flight, keyed by nothing — there are never more than a handful.</summary>
    private readonly List<Incr> _incremental = [];

    /// <summary>
    /// One selection being handed over in pieces.
    /// </summary>
    /// <remarks>
    /// X has a limit on how much can travel in a single request — a few hundred kilobytes on most servers.
    /// Text never came close, so nothing here needed INCR before; a screenshot always exceeds it. The
    /// owner writes a chunk, the requestor deletes the property when it has taken it, and that deletion is
    /// the signal to write the next one. A zero-length write ends it.
    /// </remarks>
    private sealed record Incr(nint Requestor, nint Property, nint Target, byte[] Payload, int Offset, long DeadlineTicks);

    private volatile OwnedSelection _owned = OwnedSelection.Empty;
    private IFilePromiseSource? _promiseSource;
    private IReadOnlyList<FilePromiseEntry> _promiseEntries = [];
    private Task<IReadOnlyList<string>>? _materialising;
    private byte[] _lastSeen = [];      // a hash, so a big clipboard is not held twice to compare it
    private nuint _lastEventTime;       // the newest server time seen, used when taking ownership
    private bool _weOwn;
    private bool _disposed;

    public X11Clipboard(ILogger log)
    {
        Xlib.EnsureThreadSafe();
        _log = log;
        _dpy = Xlib.XOpenDisplay(null);
        if (_dpy == 0)
        {
            throw new InvalidOperationException("Cannot open the X display for the clipboard.");
        }

        nint root = Xlib.XDefaultRootWindow(_dpy);
        _window = Xlib.XCreateSimpleWindow(_dpy, root, 0, 0, 1, 1, 0, 0, 0);
        _clipboard = Xlib.XInternAtom(_dpy, "CLIPBOARD", false);
        _utf8 = Xlib.XInternAtom(_dpy, "UTF8_STRING", false);
        _string = Xlib.XInternAtom(_dpy, "STRING", false);
        _targets = Xlib.XInternAtom(_dpy, "TARGETS", false);
        _timestamp = Xlib.XInternAtom(_dpy, "TIMESTAMP", false);
        _atom = Xlib.XInternAtom(_dpy, "ATOM", false);
        _integer = Xlib.XInternAtom(_dpy, "INTEGER", false);
        _png = Xlib.XInternAtom(_dpy, "image/png", false);
        _incr = Xlib.XInternAtom(_dpy, "INCR", false);
        _prop = Xlib.XInternAtom(_dpy, "SUNLLO_CLIP", false);
        _chunk = IncrChunkSize(Xlib.XExtendedMaxRequestSize(_dpy), Xlib.XMaxRequestSize(_dpy));
        _uriList = Xlib.XInternAtom(_dpy, "text/uri-list", false);
        _gnomeCopied = Xlib.XInternAtom(_dpy, "x-special/gnome-copied-files", false);
        _kdeCut = Xlib.XInternAtom(_dpy, "application/x-kde-cutselection", false);

        if (!XFixes.XFixesQueryExtension(_dpy, out _xfixesEventBase, out _))
        {
            _log.LogWarning("XFixes unavailable; clipboard change notifications are off");
            _xfixesEventBase = -1;
        }
        else
        {
            XFixes.XFixesSelectSelectionInput(_dpy, _window, _clipboard, XFixes.SelectionEventMask);
        }

        Xlib.XFlush(_dpy);
        _loop = new Thread(EventLoop) { IsBackground = true, Name = "x11-clipboard" };
        _loop.Start();
    }

    public ChannelReader<IReadOnlyList<ClipboardItem>> Changes => _changes.Reader;

    public ValueTask<IReadOnlyList<ClipboardItem>> ReadAsync(CancellationToken ct)
    {
        // When this side owns the clipboard, the content is in hand; no round trip.
        if (_weOwn && _owned[_utf8] is { Length: > 0 } ours)
        {
            return ValueTask.FromResult<IReadOnlyList<ClipboardItem>>(
                [new ClipboardItem(ClipboardItemFormat.Text, ours)]);
        }

        string text = RequestText();
        IReadOnlyList<ClipboardItem> items = text.Length > 0
            ? [new ClipboardItem(ClipboardItemFormat.Text, Encoding.UTF8.GetBytes(text))]
            : [];
        return ValueTask.FromResult(items);
    }

    public ValueTask WriteAsync(IReadOnlyList<ClipboardItem> items, CancellationToken ct)
    {
        ClipboardItem? text = items.FirstOrDefault(i => i.Format == ClipboardItemFormat.Text);
        ClipboardItem? image = items.FirstOrDefault(i => i.Format == ClipboardItemFormat.ImagePng);
        if (text is null && image is null)
        {
            // Nothing this selection can offer. Leaving ownership alone is deliberate: Own([]) would
            // release the clipboard, throwing away whatever the user had for an update we cannot serve.
            return ValueTask.CompletedTask;
        }

        lock (_promiseGate)
        {
            // Something else was copied; nothing that was promised is reachable any more.
            FailPending();
            _promiseSource = null;
            _promiseEntries = [];
            _materialising = null;
        }

        var payloads = new List<(nint, byte[])>(3);
        if (text is not null)
        {
            byte[] utf8 = text.Payload.ToArray();
            payloads.Add((_utf8, utf8));
            payloads.Add((_string, utf8));
        }

        if (image is not null)
        {
            // Whatever is here goes out as it stands. A PNG that came from a phone is already a PNG, and
            // re-encoding it would cost time and lose nothing worth losing.
            payloads.Add((_png, image.Payload.ToArray()));
        }

        Own(payloads);
        return ValueTask.CompletedTask;
    }

    /// <summary>X11 can name a file it has yet to fetch, as long as it has fetched it by the time it answers.</summary>
    public bool CanPromiseFiles => !_disposed;

    public ValueTask<IReadOnlyList<string>> ReadCopiedFilePathsAsync(CancellationToken ct)
    {
        // Our own promise names files in staging, not files the user copied here.
        if (_weOwn && _promiseSource is not null)
        {
            return ValueTask.FromResult<IReadOnlyList<string>>([]);
        }

        byte[] bytes = RequestBytes(_gnomeCopied);
        if (bytes.Length == 0)
        {
            bytes = RequestBytes(_uriList);
        }

        return ValueTask.FromResult(UriListCodec.ParsePaths(bytes));
    }

    public ValueTask WriteWithPromiseAsync(
        IReadOnlyList<ClipboardItem> items,
        FilePromiseListing listing,
        IFilePromiseSource source,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_promiseGate)
        {
            FailPending();
            _promiseSource = source;
            _promiseEntries = listing.Entries;
            _materialising = null;
        }

        // The file targets carry no bytes here: their payload is only known once the files exist, and they
        // are served by answering late. KDE's marker is a constant, so it can be served at once.
        var payloads = new List<(nint, byte[])>
        {
            (_uriList, []),
            (_gnomeCopied, []),
            (_kdeCut, UriListCodec.KdeCutSelection(cut: false)),
        };

        if (items.FirstOrDefault(i => i.Format == ClipboardItemFormat.Text) is { } text)
        {
            byte[] utf8 = text.Payload.ToArray();
            payloads.Add((_utf8, utf8));
            payloads.Add((_string, utf8));
        }

        Own(payloads);
        _log.LogInformation(
            "Offering {Count} promised item(s) on the clipboard (token {Token})",
            listing.Entries.Count,
            listing.Token);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Becomes the CLIPBOARD owner offering exactly these targets. From here paste requests arrive at our
    /// window and are answered from the map; the fingerprint is recorded so the change our own write causes
    /// is not read back as someone else's.
    /// </summary>
    private void Own(IEnumerable<(nint Target, byte[] Bytes)> payloads)
    {
        OwnedSelection owned = OwnedSelection.Of(payloads, Volatile.Read(ref _lastEventTime));
        _owned = owned;
        _lastSeen = owned.Fingerprint;
        _weOwn = !owned.IsEmpty;
        Xlib.XSetSelectionOwner(_dpy, _clipboard, owned.IsEmpty ? 0 : _window, owned.TakenAt);
        Xlib.XFlush(_dpy);
    }

    /// <summary>
    /// The event loop, on its own display connection. It answers SelectionRequest (someone is pasting our
    /// content), notices SelectionClear (we lost ownership) and XFixesSelectionNotify (the clipboard changed
    /// under us), and publishes external changes so the session mirrors them.
    /// </summary>
    private void EventLoop()
    {
        byte[] ev = new byte[192]; // XEvent is a union; this is comfortably larger than any member.
        while (!_cts.IsCancellationRequested)
        {
            if (Xlib.XPending(_dpy) == 0)
            {
                Thread.Sleep(20); // no XNextEvent-with-timeout in Xlib; a short poll keeps latency low and CPU idle
                ExpirePending();
                continue;
            }

            Xlib.XNextEvent(_dpy, ev);
            int type = BitConverter.ToInt32(ev, 0);

            if (type == Xlib.SelectionRequest)
            {
                ServeSelectionRequest(ev);
            }
            else if (type == Xlib.PropertyNotify)
            {
                ServePropertyNotify(ev);
            }
            else if (type == Xlib.SelectionClear)
            {
                _weOwn = false;

                // Someone else owns the clipboard now, so nothing we promised can still be delivered.
                FailPending();
            }
            else if (_xfixesEventBase >= 0 && type == _xfixesEventBase)
            {
                // The clipboard changed. If it was not this side's own write, read it and publish.
                if (!_weOwn)
                {
                    string text = RequestText();
                    if (text.Length > 0)
                    {
                        byte[] utf8 = Encoding.UTF8.GetBytes(text);
                        byte[] fingerprint = OwnedSelection.Hash(utf8);
                        if (!fingerprint.AsSpan().SequenceEqual(_lastSeen))
                        {
                            _lastSeen = fingerprint;
                            _changes.Writer.TryWrite([new ClipboardItem(ClipboardItemFormat.Text, utf8)]);
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// How many bytes to put in one property write.
    /// </summary>
    /// <remarks>
    /// ICCCM suggests a quarter of the server's maximum request. Both figures are in four-byte units, and
    /// <see cref="Xlib.XExtendedMaxRequestSize"/> answers zero when the BIG-REQUESTS extension is missing,
    /// in which case the plain limit applies. Clamped at both ends so neither a miserly server nor a
    /// generous one produces a silly chunk: too small and a megabyte takes hundreds of round trips, too
    /// large and we are back to the failure this exists to avoid.
    /// </remarks>
    internal static int IncrChunkSize(long extendedUnits, long plainUnits)
    {
        long units = extendedUnits > 0 ? extendedUnits : plainUnits;
        long bytes = units > 0 ? units : 65_536;
        return (int)Math.Clamp(bytes, 64 * 1024, 1024 * 1024);
    }

    /// <summary>Answers a paste: writes our text (or the list of targets) into the requestor's property.</summary>
    private void ServeSelectionRequest(byte[] ev)
    {
        // XSelectionRequestEvent: type, serial, send_event, display, owner, requestor, selection, target, property,
        // time. The owner member is the one that is easy to miss -- requestor is the fifth, not the fourth. (The
        // SelectionNotify reply below is an XSelectionEvent, which has no owner, so its members sit one place earlier.)
        nint requestor = XEventBytes.ReadLong(ev, 5);
        nint selection = XEventBytes.ReadLong(ev, 6);
        nint target = XEventBytes.ReadLong(ev, 7);
        nint property = XEventBytes.ReadLong(ev, 8);
        nuint time = XEventBytes.ReadULong(ev, 9);
        Volatile.Write(ref _lastEventTime, time);

        if (property == 0)
        {
            property = target; // obsolete clients pass None; reply into the target atom
        }

        OwnedSelection owned = _owned;
        nint notifyProperty = property;

        if (target == _targets)
        {
            // What we can actually convert to, not a fixed pair. TARGETS and TIMESTAMP are always there;
            // the rest is whatever the last write put on the clipboard.
            nint[] atoms = [_targets, _timestamp, .. owned.Targets];
            Xlib.XChangeProperty(_dpy, requestor, property, _atom, 32, Xlib.PropModeReplace, XEventBytes.Longs(atoms), atoms.Length);
        }
        else if (target == _timestamp)
        {
            // ICCCM requires an owner to answer this, and refusing it makes some managers drop the
            // selection. It is the time ownership was taken, not the time now.
            byte[] bytes = XEventBytes.Longs([(nint)owned.TakenAt]);
            Xlib.XChangeProperty(_dpy, requestor, property, _integer, 32, Xlib.PropModeReplace, bytes, 1);
        }
        else if ((target == _uriList || target == _gnomeCopied) && _promiseSource is not null)
        {
            // Not answered here. The files do not exist yet and a URI must name one that does.
            Defer(new Pending(requestor, selection, target, property, time, DateTime.UtcNow.Add(PromiseDeadline).Ticks));
            return;
        }
        else if (owned[target] is { } payload)
        {
            if (payload.Length > _chunk)
            {
                // Too big for one request. Announce the size as type INCR and hand it over in pieces; the
                // requestor deleting the property is what asks for each next one.
                //
                // XSelectInput on a window we do not own has to happen before the first write, or the
                // PropertyNotify that drives the rest is never delivered. An error here is invisible —
                // Xlib.InstallErrorHandler swallows everything — so the deadline below is what recovers a
                // transfer whose requestor has gone away.
                Xlib.XSelectInput(_dpy, requestor, Xlib.PropertyChangeMask);

                byte[] size = XEventBytes.Longs([payload.Length]);
                Xlib.XChangeProperty(_dpy, requestor, property, _incr, 32, Xlib.PropModeReplace, size, 1);

                lock (_promiseGate)
                {
                    _incremental.Add(new Incr(requestor, property, target, payload, 0, DateTime.UtcNow.Add(IncrDeadline).Ticks));
                }

                _log.LogDebug("clipboard: sending {Bytes} bytes in {Chunk}-byte pieces", payload.Length, _chunk);
            }
            else
            {
                Xlib.XChangeProperty(_dpy, requestor, property, target, 8, Xlib.PropModeReplace, payload, payload.Length);
            }
        }
        else
        {
            notifyProperty = 0; // cannot convert to this target
        }

        // Send the SelectionNotify that tells the requestor the property is ready.
        byte[] notify = new byte[192];
        BitConverter.GetBytes(Xlib.SelectionNotify).CopyTo(notify, 0);
        XEventBytes.WriteLong(notify, 4, requestor);
        XEventBytes.WriteLong(notify, 5, selection);
        XEventBytes.WriteLong(notify, 6, target);
        XEventBytes.WriteLong(notify, 7, notifyProperty); // 0 = refused
        XEventBytes.WriteULong(notify, 8, time);
        Xlib.XSendEvent(_dpy, requestor, propagate: false, 0, notify);
        Xlib.XFlush(_dpy);
    }

    /// <summary>
    /// Sends the next piece of an INCR transfer, or ends it.
    /// </summary>
    /// <remarks>
    /// The requestor deleting our property is the whole protocol: it means "I have taken that piece, send
    /// the next". A zero-length write of the target type is the terminator. Anything else — a property
    /// being created, or one we are not transferring — is not ours to act on.
    /// </remarks>
    private void ServePropertyNotify(byte[] ev)
    {
        // XPropertyEvent: type, serial, send_event, display, window, atom, time, state.
        nint window = XEventBytes.ReadLong(ev, 4);
        nint atom = XEventBytes.ReadLong(ev, 5);
        int state = XEventBytes.ReadInt(ev, 7);

        if (state != Xlib.PropertyDelete)
        {
            return;
        }

        Incr? transfer = null;
        lock (_promiseGate)
        {
            int index = _incremental.FindIndex(i => i.Requestor == window && i.Property == atom);
            if (index < 0)
            {
                return;
            }

            transfer = _incremental[index];
            int take = Math.Min(_chunk, transfer.Payload.Length - transfer.Offset);
            if (take > 0)
            {
                _incremental[index] = transfer with { Offset = transfer.Offset + take };
            }
            else
            {
                _incremental.RemoveAt(index);
            }
        }

        int remaining = transfer.Payload.Length - transfer.Offset;
        if (remaining <= 0)
        {
            // The terminator, and then stop watching a window that is none of our business again.
            Xlib.XChangeProperty(_dpy, window, atom, transfer.Target, 8, Xlib.PropModeReplace, [], 0);
            Xlib.XSelectInput(_dpy, window, 0);
            Xlib.XFlush(_dpy);
            return;
        }

        int size = Math.Min(_chunk, remaining);
        byte[] piece = new byte[size];
        Array.Copy(transfer.Payload, transfer.Offset, piece, 0, size);
        Xlib.XChangeProperty(_dpy, window, atom, transfer.Target, 8, Xlib.PropModeReplace, piece, size);
        Xlib.XFlush(_dpy);
    }

    /// <summary>
    /// Records a request we cannot answer yet and starts fetching. Returning immediately is the point: the
    /// event loop goes on serving SelectionClear and other requestors while the transfer runs.
    /// </summary>
    private void Defer(Pending request)
    {
        Task<IReadOnlyList<string>> materialising;
        lock (_promiseGate)
        {
            _pending.Add(request);
            materialising = _materialising ??= Task.Run(() => MaterialiseAsync(_cts.Token));
        }

        materialising.ContinueWith(
            t => AnswerPending(t.IsCompletedSuccessfully ? t.Result : []),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Fetches every promised item into staging, once, whatever asked for it. A uri-list names all of them,
    /// so one request means all of them; this is where the pasting application's wait is spent.
    /// </summary>
    private async Task<IReadOnlyList<string>> MaterialiseAsync(CancellationToken ct)
    {
        IFilePromiseSource? source = _promiseSource;
        IReadOnlyList<FilePromiseEntry> entries = _promiseEntries;
        if (source is null || entries.Count == 0)
        {
            return [];
        }

        var staged = new List<string>(entries.Count);
        foreach (FilePromiseEntry entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            string destination = source.StagedPathFor(entry.Id);
            await source.FetchAsync(entry.Id, destination, null, ct).ConfigureAwait(false);
            staged.Add(destination);
        }

        _log.LogDebug("Staged {Count} promised item(s) for a paste", staged.Count);
        return staged;
    }

    /// <summary>Answers everything that was waiting, with the files if they arrived and a refusal if not.</summary>
    private void AnswerPending(IReadOnlyList<string> staged)
    {
        Pending[] waiting;
        lock (_promiseGate)
        {
            waiting = [.. _pending];
            _pending.Clear();
        }

        foreach (Pending request in waiting)
        {
            if (staged.Count == 0)
            {
                Refuse(request);
                continue;
            }

            byte[] payload = request.Target == _gnomeCopied
                ? UriListCodec.GnomeCopiedFiles(staged)
                : UriListCodec.UriList(staged);

            Xlib.XChangeProperty(_dpy, request.Requestor, request.Property, request.Target, 8, Xlib.PropModeReplace, payload, payload.Length);
            Notify(request, request.Property);
        }

        Xlib.XFlush(_dpy);
    }

    /// <summary>Fails everything outstanding. A requestor told "no" recovers; one told nothing waits for ever.</summary>
    private void FailPending()
    {
        Pending[] waiting;
        lock (_promiseGate)
        {
            waiting = [.. _pending];
            _pending.Clear();
        }

        foreach (Pending request in waiting)
        {
            Refuse(request);
        }

        if (waiting.Length > 0)
        {
            Xlib.XFlush(_dpy);
        }
    }

    /// <summary>Drops anything that has waited past the deadline, so a dead session does not freeze a paste for ever.</summary>
    private void ExpirePending()
    {
        long now = DateTime.UtcNow.Ticks;
        Pending[] expired;
        lock (_promiseGate)
        {
            // A requestor that died mid-paste will never delete the property again. Dropping the transfer
            // also stops us watching a window that may no longer exist.
            foreach (Incr stale in _incremental.Where(i => i.DeadlineTicks <= now).ToArray())
            {
                _incremental.Remove(stale);
                _log.LogDebug("clipboard: gave up on an unfinished transfer after {Seconds:0}s", IncrDeadline.TotalSeconds);
            }

            expired = [.. _pending.Where(p => p.DeadlineTicks <= now)];
            foreach (Pending request in expired)
            {
                _pending.Remove(request);
            }
        }

        foreach (Pending request in expired)
        {
            _log.LogWarning("Giving up on a paste of promised files after {Minutes:0} minutes", PromiseDeadline.TotalMinutes);
            Refuse(request);
        }

        if (expired.Length > 0)
        {
            Xlib.XFlush(_dpy);
        }
    }

    private void Refuse(Pending request) => Notify(request, 0);

    /// <summary>The reply that tells a requestor the property is ready, or that we could not convert.</summary>
    private void Notify(Pending request, nint property)
    {
        byte[] notify = new byte[192];
        BitConverter.GetBytes(Xlib.SelectionNotify).CopyTo(notify, 0);
        XEventBytes.WriteLong(notify, 4, request.Requestor);
        XEventBytes.WriteLong(notify, 5, request.Selection);
        XEventBytes.WriteLong(notify, 6, request.Target);
        XEventBytes.WriteLong(notify, 7, property);
        XEventBytes.WriteULong(notify, 8, request.Time);
        Xlib.XSendEvent(_dpy, request.Requestor, propagate: false, 0, notify);
    }

    /// <summary>Asks the current owner for UTF8_STRING and waits briefly for the answer.</summary>
    private string RequestText()
    {
        byte[] bytes = RequestBytes(_utf8);
        return bytes.Length == 0 ? string.Empty : Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// Asks the current owner to convert the selection to one target and waits for the answer. The owner may
    /// legitimately take its time — ours does, when files have to be fetched first — so this waits out a
    /// window rather than assuming a reply within a frame.
    /// </summary>
    private byte[] RequestBytes(nint target)
    {
        if (Xlib.XGetSelectionOwner(_dpy, _clipboard) == 0)
        {
            return [];
        }

        Xlib.XConvertSelection(_dpy, _clipboard, target, _prop, _window, 0);
        Xlib.XFlush(_dpy);

        // The answer is a SelectionNotify on our window. The event loop thread also pumps events, so rather
        // than fight it this reads the property after a short settle.
        for (int i = 0; i < 50; i++)
        {
            Thread.Sleep(4);
            int rc = Xlib.XGetWindowProperty(_dpy, _window, _prop, 0, 1 << 20, delete: true, 0,
                out nint actualType, out _, out nuint nItems, out _, out nint data);
            if (rc == 0 && data != 0)
            {
                try
                {
                    if (actualType != 0 && nItems > 0)
                    {
                        byte[] bytes = new byte[(int)nItems];
                        Marshal.Copy(data, bytes, 0, bytes.Length);
                        return bytes;
                    }
                }
                finally
                {
                    Xlib.XFree(data);
                }
            }
        }

        return [];
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;

            // Anything waiting on a paste is told no before the display goes away; a requestor that is never
            // answered waits for ever.
            FailPending();
            _cts.Cancel();
            _loop.Join(TimeSpan.FromSeconds(1));
            if (_window != 0)
            {
                Xlib.XDestroyWindow(_dpy, _window);
            }

            if (_dpy != 0)
            {
                Xlib.XCloseDisplay(_dpy);
            }

            _changes.Writer.TryComplete();
            _cts.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}
