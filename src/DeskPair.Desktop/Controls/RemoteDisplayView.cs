using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using DeskPair.Core.Video;
using DeskPair.Platform.Abstractions.Codec;
using DeskPair.Protocol.Messages;

namespace DeskPair.Desktop.Controls;

/// <summary>
/// Paints decoded frames (CPU path: <see cref="WriteableBitmap"/>) and turns pointer/keyboard input into
/// wire events with coordinates in remote display pixels. GPU zero-copy rendering can replace the
/// bitmap later without touching the input side.
/// </summary>
public sealed class RemoteDisplayView : Control
{
    private const int TypeMove = 0, TypeDown = 1, TypeUp = 2, TypeWheel = 3;

    private const int MaxQueuedFrames = 3;
    private static readonly TimeSpan MovingInterpolationHold = TimeSpan.FromMilliseconds(500);

    private readonly object _frameLock = new();
    private readonly Queue<QueuedFrame> _queue = new();
    private readonly Stack<byte[]> _pool = new();
    private readonly FramePacer _pacer = new(TimeProvider.System);
    private bool _presentScheduled;
    private bool _movingInterpolation;
    private DispatcherTimer? _settleTimer;
    private long _framesPresented;
    private WriteableBitmap? _bitmap;
    private bool _invalidateQueued;
    private (int X, int Y)? _cursorPosition;
    private Bitmap? _cursorBitmap;
    private (int HotX, int HotY) _cursorHotspot;
    private readonly Dictionary<ulong, RemoteCursor> _cursors = new();
    private DateTime _lastMove;
    private TopLevel? _topLevel;

    public RemoteDisplayView()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = Cursor.Default;
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);
    }

    public event Action<MouseEvent>? MouseInput;

    public event Action<KeyEventArgs, bool>? KeyInput;

    /// <summary>
    /// Fit shrinks the picture to the control when it does not fit but never enlarges it (1:1 whenever it fits,
    /// so lossless pixels stay lossless); Original always paints 1:1 (scrolling is left to a parent ScrollViewer).
    /// 1:1 is in the screen's pixels, not in layout units -- see <see cref="PictureLayout"/>.
    /// </summary>
    public bool FitToWindow { get; set; } = true;

    /// <summary>Also paint the remote cursor at its remote position (useful when the remote user moves it).</summary>
    public bool ShowRemoteCursor { get; set; }

    /// <summary>
    /// Present frames at the cadence they were produced instead of the moment they arrive: a small jitter
    /// buffer (up to three frames, so under 50 ms at 60 fps) that turns network burstiness into steady motion.
    /// </summary>
    public bool SmoothPlayback
    {
        get => _pacer.Smooth;
        set => _pacer.Smooth = value;
    }

    /// <summary>Frames actually uploaded and painted; diagnostics.</summary>
    public long FramesPresented => Interlocked.Read(ref _framesPresented);

    /// <summary>Frames dropped because the viewer fell more than the buffer behind; diagnostics.</summary>
    public long FramesDropped { get; private set; }

    public int RemoteWidth { get; private set; }

    public int RemoteHeight { get; private set; }

    public int DisplayIndex { get; set; }

    /// <summary>
    /// Where this display's top-left corner sits on the host's desktop. The host reports its pointer in desktop
    /// coordinates, so a display to the right of the primary has to take its own origin off before drawing --
    /// without it the pointer on a second display was drawn a whole screen's width away, off the picture.
    /// </summary>
    public (int X, int Y) Origin { get; set; }

    /// <summary>Called from the session thread; the frame is valid only during the call.</summary>
    public void SubmitFrame(in DecodedFrame frame)
    {
        if (frame.IsGpuSurface || frame.Width <= 0 || frame.Height <= 0)
        {
            return;
        }

        int rowBytes = frame.Width * 4;
        int need = rowBytes * frame.Height;
        byte[] buffer;
        lock (_frameLock)
        {
            if (_pool.Count > 0 && _pool.Peek().Length != need)
            {
                _pool.Clear(); // resolution changed; the old buffers are useless
            }

            buffer = _pool.Count > 0 ? _pool.Pop() : new byte[need];
        }

        // The 14 MB copy happens outside the lock so the UI thread is never held up by it.
        ReadOnlySpan<byte> src = frame.Cpu.Span;
        if (frame.Stride == rowBytes)
        {
            src[..need].CopyTo(buffer);
        }
        else
        {
            for (int y = 0; y < frame.Height; y++)
            {
                src.Slice(y * frame.Stride, rowBytes).CopyTo(buffer.AsSpan(y * rowBytes));
            }
        }

        bool schedule;
        lock (_frameLock)
        {
            long due = _pacer.Schedule();
            _queue.Enqueue(new QueuedFrame(buffer, frame.Width, frame.Height, due));
            while (_queue.Count > MaxQueuedFrames)
            {
                // Every frame carries the whole picture, so the oldest one is safe to skip.
                _pool.Push(_queue.Dequeue().Buffer);
                FramesDropped++;
            }

            schedule = !_presentScheduled;
            _presentScheduled = true;
        }

        if (schedule)
        {
            Dispatcher.UIThread.Post(Present, DispatcherPriority.Render);
        }
    }

    /// <summary>
    /// UI thread: paint whatever is due, then ride the compositor's animation-frame callback (vsync aligned,
    /// no coarse dispatcher timer) until the queue is empty.
    /// </summary>
    private void Present()
    {
        bool painted;
        try
        {
            painted = UploadPending();
        }
        catch (Exception e)
        {
            RenderFailed?.Invoke(e);
            painted = false;
        }

        if (painted)
        {
            Interlocked.Increment(ref _framesPresented);
            UseMovingInterpolation();
            InvalidateVisual();
        }

        bool more;
        lock (_frameLock)
        {
            more = _queue.Count > 0;
            _presentScheduled = more;
        }

        if (more)
        {
            TopLevel? top = TopLevel.GetTopLevel(this);
            if (top is not null)
            {
                top.RequestAnimationFrame(_ => Present());
            }
            else
            {
                DispatcherTimer.RunOnce(Present, TimeSpan.FromMilliseconds(4), DispatcherPriority.Render);
            }
        }
    }

    /// <summary>
    /// Cheap bilinear while frames are flowing; cubic once the picture has settled. Neither is used when every remote
    /// pixel covers whole screen pixels: the picture is then copied as it is.
    /// </summary>
    private void UseMovingInterpolation()
    {
        if (!_movingInterpolation)
        {
            _movingInterpolation = true;
            RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.MediumQuality);
        }

        _settleTimer ??= new DispatcherTimer(MovingInterpolationHold, DispatcherPriority.Background, (_, _) =>
        {
            _settleTimer!.Stop();
            _movingInterpolation = false;
            RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);
            InvalidateVisual();
        });
        _settleTimer.Stop();
        _settleTimer.Start();
    }

    private sealed record QueuedFrame(byte[] Buffer, int Width, int Height, long Due);

    public void SetCursorShape(CursorData shape)
    {
        if (shape.Width <= 0 || shape.Height <= 0 || shape.Bgra.Length < shape.Width * shape.Height * 4)
        {
            return;
        }

        var bitmap = new WriteableBitmap(new PixelSize(shape.Width, shape.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (ILockedFramebuffer fb = bitmap.Lock())
        {
            unsafe
            {
                ReadOnlySpan<byte> src = shape.Bgra.Span;
                for (int y = 0; y < shape.Height; y++)
                {
                    src.Slice(y * shape.Width * 4, shape.Width * 4).CopyTo(new Span<byte>((byte*)fb.Address + y * fb.RowBytes, shape.Width * 4));
                }
            }
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_cursors.Remove(shape.Id, out RemoteCursor? old))
            {
                old.Dispose();
            }

            _cursors[shape.Id] = new RemoteCursor(bitmap, shape.Hotx, shape.Hoty);
            SelectCursor(shape.Id);
        });
    }

    public void SetCursorId(ulong id) => Dispatcher.UIThread.Post(() => SelectCursor(id));

    public void SetCursorPosition(int x, int y)
    {
        _cursorPosition = (x, y);
        QueueInvalidate();
    }

    /// <summary>
    /// Points the local pointer at the shape the far end is using. The local pointer is what the user aims
    /// with, so it is never taken away: the far end reports id 0 whenever Windows has hidden its own cursor,
    /// which it does every time nobody has moved a mouse there for a moment, and an unknown id arrives
    /// whenever the host could not read a cursor's image at all. Blanking the pointer for the first and
    /// flashing the default arrow for the second is what made the pointer flicker while the remote one was
    /// not being drawn over it.
    ///
    /// What the far end's state does decide is the overlay: with nothing to draw, nothing is drawn at the
    /// remote position, whether or not the user asked to see it.
    /// </summary>
    private void SelectCursor(ulong id)
    {
        if (_cursors.TryGetValue(id, out RemoteCursor? c))
        {
            _cursorBitmap = c.Image;
            _cursorHotspot = (c.HotX, c.HotY);
            Cursor = c.Pointer;
        }
        else
        {
            _cursorBitmap = null;
        }

        InvalidateVisual();
    }

    /// <summary>
    /// One cursor shape from the far end, and the local pointer made from it. Both are built once and kept
    /// for as long as the session lasts: a shape arrives once per id, and the pointer is rebuilt on every
    /// change of id otherwise, which for a window the user is working in is thousands of times.
    /// </summary>
    private sealed class RemoteCursor : IDisposable
    {
        public RemoteCursor(Bitmap image, int hotX, int hotY)
        {
            Image = image;
            HotX = hotX;
            HotY = hotY;
            try
            {
                Pointer = new Cursor(image, new PixelPoint(hotX, hotY));
            }
            catch (Exception)
            {
                // A shape the platform will not take as a pointer still draws fine as an overlay.
                Pointer = Cursor.Default;
            }
        }

        public Bitmap Image { get; }

        public int HotX { get; }

        public int HotY { get; }

        public Cursor Pointer { get; }

        public void Dispose()
        {
            Image.Dispose();
            if (!ReferenceEquals(Pointer, Cursor.Default))
            {
                Pointer.Dispose();
            }
        }
    }

    private void QueueInvalidate()
    {
        if (Interlocked.Exchange(ref _invalidateQueued, true))
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            _invalidateQueued = false;
            InvalidateVisual();
        }, DispatcherPriority.Render);
    }

    // ---- rendering ----

    public event Action<Exception>? RenderFailed;

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Black, new Rect(Bounds.Size));
        try
        {
            if (_bitmap is null)
            {
                return;
            }

            PictureLayout layout = Layout();
            Rect dest = layout.Destination;
            if (layout.IsWhole)
            {
                using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.None }))
                {
                    context.DrawImage(_bitmap, new Rect(0, 0, RemoteWidth, RemoteHeight), dest);
                }
            }
            else
            {
                context.DrawImage(_bitmap, new Rect(0, 0, RemoteWidth, RemoteHeight), dest);
            }

            if (ShowRemoteCursor && _cursorBitmap is not null && _cursorPosition is (int px, int py)
                && px - Origin.X is var cx and >= 0 && cx < RemoteWidth && py - Origin.Y is var cy and >= 0 && cy < RemoteHeight)
            {
                // Only the display the pointer is on draws it; with several windows open the others would
                // otherwise pin it to their edges.
                double scale = dest.Width / RemoteWidth;
                double x = dest.X + (cx - _cursorHotspot.HotX) * scale;
                double y = dest.Y + (cy - _cursorHotspot.HotY) * scale;
                context.DrawImage(_cursorBitmap, new Rect(x, y, _cursorBitmap.PixelSize.Width * scale, _cursorBitmap.PixelSize.Height * scale));
            }
        }
        catch (Exception e)
        {
            RenderFailed?.Invoke(e); // a render exception would take the whole app down
        }
    }

    private bool UploadPending()
    {
        QueuedFrame? frame = null;
        lock (_frameLock)
        {
            long now = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 500; // anything due within 2 ms goes on this vsync
            while (_queue.Count > 0 && _queue.Peek().Due <= now)
            {
                if (frame is not null)
                {
                    _pool.Push(frame.Buffer);
                    FramesDropped++;
                }

                frame = _queue.Dequeue();
            }
        }

        if (frame is null)
        {
            return false;
        }

        try
        {
            if (_bitmap is null || RemoteWidth != frame.Width || RemoteHeight != frame.Height)
            {
                _bitmap?.Dispose();
                _bitmap = new WriteableBitmap(new PixelSize(frame.Width, frame.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
                RemoteWidth = frame.Width;
                RemoteHeight = frame.Height;
                InvalidateMeasure();
            }

            using ILockedFramebuffer fb = _bitmap.Lock();
            unsafe
            {
                int rowBytes = frame.Width * 4;
                if (fb.RowBytes == rowBytes)
                {
                    frame.Buffer.AsSpan(0, rowBytes * frame.Height).CopyTo(new Span<byte>((byte*)fb.Address, rowBytes * frame.Height));
                }
                else
                {
                    for (int y = 0; y < frame.Height; y++)
                    {
                        frame.Buffer.AsSpan(y * rowBytes, rowBytes).CopyTo(new Span<byte>((byte*)fb.Address + y * fb.RowBytes, rowBytes));
                    }
                }
            }

            return true;
        }
        finally
        {
            lock (_frameLock)
            {
                if (_pool.Count < MaxQueuedFrames + 1)
                {
                    _pool.Push(frame.Buffer);
                }
            }
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // Infinite constraints (inside a ScrollViewer) must never be returned as a size.
        Size natural = PictureLayout.NaturalSize(Scaling, RemoteWidth, RemoteHeight);
        double w = double.IsInfinity(availableSize.Width) ? natural.Width : availableSize.Width;
        double h = double.IsInfinity(availableSize.Height) ? natural.Height : availableSize.Height;
        return FitToWindow || RemoteWidth == 0 ? new Size(w, h) : natural;
    }

    private PictureLayout Layout() => PictureLayout.Compute(Bounds.Size, Scaling, RemoteWidth, RemoteHeight, FitToWindow);

    /// <summary>Screen pixels per layout unit where this control is shown; 1 until it is in a window.</summary>
    private double Scaling => VisualRoot is null ? 1 : LayoutHelper.GetLayoutScale(this);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is not null)
        {
            _topLevel.ScalingChanged += OnScalingChanged;
        }
    }

    /// <summary>
    /// The window went to a screen with another scaling: the same picture now takes a different number of units,
    /// and may now be drawn whole where it was smoothed, or the other way round.
    /// </summary>
    private void OnScalingChanged(object? sender, EventArgs e)
    {
        InvalidateMeasure();
        InvalidateVisual();
        FillingSizeChanged?.Invoke();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        FillingSizeChanged?.Invoke();
    }

    /// <summary>
    /// The remote size that would fill this control at 1:1, in the remote display's pixels, and the scale at which the
    /// host's own interface would look the size of this screen's: what a display following its window asks for.
    /// </summary>
    public (int Width, int Height, double UiScale) FillingSize
    {
        get
        {
            double scaling = Scaling;
            (int width, int height) = PictureLayout.RemoteSizeFilling(Bounds.Size, scaling);
            return (width, height, scaling / PictureLayout.NaturalFactor(scaling));
        }
    }

    /// <summary>Raised when <see cref="FillingSize"/> may have changed: the control was resized, or moved to another scaling.</summary>
    public event Action? FillingSizeChanged;

    // ---- input ----

    private bool TryToRemote(Point local, out int x, out int y)
    {
        Rect dest = Layout().Destination;
        if (dest.Width <= 0)
        {
            x = y = 0;
            return false;
        }

        x = (int)Math.Round((local.X - dest.X) * RemoteWidth / dest.Width);
        y = (int)Math.Round((local.Y - dest.Y) * RemoteHeight / dest.Height);
        x = Math.Clamp(x, 0, RemoteWidth - 1);
        y = Math.Clamp(y, 0, RemoteHeight - 1);
        return true;
    }

    private static int ButtonBits(PointerUpdateKind kind) => kind switch
    {
        PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased => 1,
        PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => 2,
        PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => 4,
        PointerUpdateKind.XButton1Pressed or PointerUpdateKind.XButton1Released => 8,
        PointerUpdateKind.XButton2Pressed or PointerUpdateKind.XButton2Released => 16,
        _ => 0,
    };

    private void Send(int type, int buttons, int x, int y, KeyModifiers modifiers)
    {
        var e = new MouseEvent { Mask = type | (buttons << 3), X = x, Y = y, Display = DisplayIndex };
        if (type != TypeWheel)
        {
            // Pixels of this picture: should the display have changed size meanwhile, the host scales the point.
            e.FrameWidth = RemoteWidth;
            e.FrameHeight = RemoteHeight;
        }

        if ((modifiers & KeyModifiers.Shift) != 0)
        {
            e.Modifiers.Add(Protocol.Messages.ControlKey.CkShift);
        }

        if ((modifiers & KeyModifiers.Control) != 0)
        {
            e.Modifiers.Add(Protocol.Messages.ControlKey.CkControl);
        }

        if ((modifiers & KeyModifiers.Alt) != 0)
        {
            e.Modifiers.Add(Protocol.Messages.ControlKey.CkAlt);
        }

        MouseInput?.Invoke(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        DateTime now = DateTime.UtcNow;
        if ((now - _lastMove).TotalMilliseconds < 8)
        {
            return; // ~120 Hz cap
        }

        _lastMove = now;
        if (TryToRemote(e.GetPosition(this), out int x, out int y))
        {
            Send(TypeMove, 0, x, y, e.KeyModifiers);
        }

        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        PointerPoint p = e.GetCurrentPoint(this);
        if (TryToRemote(p.Position, out int x, out int y))
        {
            Send(TypeDown, ButtonBits(p.Properties.PointerUpdateKind), x, y, e.KeyModifiers);
        }

        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        PointerPoint p = e.GetCurrentPoint(this);
        if (TryToRemote(p.Position, out int x, out int y))
        {
            Send(TypeUp, ButtonBits(p.Properties.PointerUpdateKind), x, y, e.KeyModifiers);
        }

        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        // Wire: y = vertical notches (positive away from the user), x = horizontal notches.
        int dx = (int)Math.Round(e.Delta.X);
        int dy = (int)Math.Round(e.Delta.Y);
        if (dx != 0 || dy != 0)
        {
            Send(TypeWheel, 0, dx, dy, e.KeyModifiers);
        }

        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        KeyInput?.Invoke(e, true);
        e.Handled = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        KeyInput?.Invoke(e, false);
        e.Handled = true;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_topLevel is not null)
        {
            _topLevel.ScalingChanged -= OnScalingChanged;
            _topLevel = null;
        }

        _bitmap?.Dispose();
        _bitmap = null;
        foreach (RemoteCursor cursor in _cursors.Values)
        {
            cursor.Dispose();
        }

        _cursors.Clear();
        _cursorBitmap = null;
    }
}
