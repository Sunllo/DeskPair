using Microsoft.Extensions.Logging;
using DeskPair.Platform.Abstractions.Input;
using DeskPair.Platform.MacOS.Native;

namespace DeskPair.Platform.MacOS.Input;

/// <summary>
/// Injects mouse and keyboard through CoreGraphics events (the shim wraps CGEventPost). The controller runs
/// on another OS, so raw scancodes are not portable; input arrives as a named control key or as text, which
/// become macOS virtual keycodes or a unicode string. Modifiers are tracked as a running CGEventFlags mask
/// applied to every event, because on macOS a modifier only affects other keys when its flag is set on their
/// events — posting a lone Command keydown does not, by itself, make the next key a Command chord.
///
/// A button held down turns a move into a drag, and macOS distinguishes the two by event type rather than by
/// any state it keeps for us, so the pressed button is tracked here and every move is posted accordingly.
///
/// Coordinates arrive as pixels in the host's virtual screen; CGEvent wants global points, so each move is
/// divided by the containing display's backing scale. Posting needs Accessibility (TCC) consent; without it
/// the window server drops the events, which the permission surface reports.
/// </summary>
public sealed class MacInputInjector : IInputInjector
{
    private readonly ILogger _log;
    private readonly nint _handle;
    private readonly List<DisplayBox> _displays;
    private ulong _flags;
    private double _lastX;
    private double _lastY;

    /// <summary>
    /// Which button is down, so a move can be posted as the drag it is: -1 none, 0 left, 1 right, 2 other.
    /// Tracked here for the same reason the modifier mask is — macOS decides from the event, not from some
    /// ambient state, so every move has to say whether a button is holding it.
    /// </summary>
    private int _held = -1;
    private bool _disposed;

    public MacInputInjector(ILogger log)
    {
        _log = log;
        _handle = MacShim.fd_input_create();
        if (_handle == 0)
        {
            throw new InvalidOperationException("Could not create the macOS input source.");
        }

        _displays = LoadDisplays();
    }

    public void EnsureInputDesktop()
    {
        // macOS has one input path; there is no secure-desktop attach.
    }

    public void InjectMouse(in MouseInput input, in VirtualScreenRect virtualScreen)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        switch (input.Action)
        {
            case MouseAction.Move:
                (_lastX, _lastY) = ToPoint(input.X, input.Y);
                MacShim.fd_input_mouse_move(_handle, _lastX, _lastY, _flags, _held);
                break;
            case MouseAction.MoveRelative:
                // X and Y are a delta here, not a point. Running them through ToPoint treated the delta as a
                // screen coordinate and threw the pointer to the top-left corner instead of nudging it.
                // CGEvent has no relative post, so the delta is added to where the pointer actually is.
                (double fromX, double fromY) = MacShim.fd_cursor_position(out double curX, out double curY) != 0
                    ? (curX, curY)
                    : (_lastX, _lastY);
                double scale = ScaleAt(fromX, fromY);
                _lastX = fromX + (input.X / scale);
                _lastY = fromY + (input.Y / scale);
                MacShim.fd_input_mouse_move(_handle, _lastX, _lastY, _flags, _held);
                break;
            case MouseAction.Down:
            case MouseAction.Up:
                (_lastX, _lastY) = ToPoint(input.X, input.Y);
                int button = ButtonOf(input.Buttons);
                if (button >= 0)
                {
                    bool down = input.Action == MouseAction.Down;
                    MacShim.fd_input_mouse_button(_handle, button, down ? 1 : 0, _lastX, _lastY, _flags);

                    // Releasing a button other than the one being dragged with must not end the drag.
                    _held = down ? button : (_held == button ? -1 : _held);
                }

                break;
            case MouseAction.Wheel:
                MacShim.fd_input_scroll(_handle, 0, NormalizeWheel(input.WheelDelta));
                break;
            case MouseAction.HorizontalWheel:
                MacShim.fd_input_scroll(_handle, NormalizeWheel(input.WheelDelta), 0);
                break;
        }
    }

    public void InjectKey(in KeyInput input)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (input.Control != ControlKey.None)
        {
            ulong flag = MacKeycodes.FlagFor(input.Control);
            if (flag != 0)
            {
                // A modifier: keep the running mask in step; do not emit a keystroke of its own.
                _flags = input.Down ? _flags | flag : _flags & ~flag;
                return;
            }

            int code = MacKeycodes.ForControl(input.Control);
            if (code >= 0)
            {
                MacShim.fd_input_key(_handle, (ushort)code, input.Down ? 1 : 0, _flags);
            }

            return;
        }

        // A positional key by scancode (Map mode) is how the controller sends the letter of a shortcut. It
        // must be injected as a real key with the current modifier flags applied — typing it as text (below)
        // ignores the modifiers, so Cmd+C would insert 'c' instead of copying. The scancode is PC set-1;
        // MacKeycodes maps it to the macOS virtual key.
        if (input.Mode == KeyInputMode.Map && input.Code != 0)
        {
            int vk = MacKeycodes.ForScancode(input.Code);
            if (vk >= 0)
            {
                MacShim.fd_input_key(_handle, (ushort)vk, input.Down ? 1 : 0, _flags);
            }

            return;
        }

        if (!string.IsNullOrEmpty(input.Text))
        {
            // Text carries its own state; a chord like Cmd+C comes through Control keys, so plain text is only
            // typed on the down edge to avoid a double insert.
            if (!input.Down)
            {
                return;
            }

            TypeText(input.Text);
        }
    }

    /// <summary>
    /// Pointer, keyboard and the screen lock, which Command+Control+Q really does perform.
    ///
    /// Not the lock keys, which are not read. Not a secure attention sequence either: macOS has no
    /// equivalent, and Command+Option+Escape opens Force Quit, which is an application any application can
    /// be in front of.
    /// </summary>
    public InputCapabilities Capabilities => InputCapabilities.Mouse | InputCapabilities.Keyboard | InputCapabilities.LockScreen;

    public LockKeyStates GetLockKeyStates() => default;

    public void SetLockKeyStates(LockKeyStates states)
    {
    }

    public void ReleaseAll()
    {
        if (_disposed)
        {
            return;
        }

        _flags = 0; // drop every held modifier so the next session starts clean
    }

    public void SendCtrlAltDel()
    {
        // No macOS equivalent; the closest recognised interrupt is Command+Option+Esc (Force Quit).
        MacShim.fd_input_key(_handle, 0x35 /* Escape */, 1, MacKeycodes.FlagCommand | MacKeycodes.FlagAlternate);
        MacShim.fd_input_key(_handle, 0x35, 0, MacKeycodes.FlagCommand | MacKeycodes.FlagAlternate);
    }

    public void LockWorkstation()
    {
        // Command+Control+Q locks the screen on modern macOS.
        MacShim.fd_input_key(_handle, 0x0C /* Q */, 1, MacKeycodes.FlagCommand | MacKeycodes.FlagControl);
        MacShim.fd_input_key(_handle, 0x0C, 0, MacKeycodes.FlagCommand | MacKeycodes.FlagControl);
    }

    private unsafe void TypeText(string text)
    {
        ReadOnlySpan<char> chars = text;
        fixed (char* p = chars)
        {
            MacShim.fd_input_text(_handle, (ushort*)p, chars.Length);
        }
    }

    /// <summary>
    /// The backing scale of the display a point lies on, for turning a pixel delta into the points CGEvent
    /// wants. A relative move has no absolute coordinate to look the display up by, so it asks where the
    /// pointer already is.
    /// </summary>
    private double ScaleAt(double pointX, double pointY)
    {
        foreach (DisplayBox d in _displays)
        {
            if (pointX >= d.PointX && pointX < d.PointX + (d.PixelW / d.Scale) && pointY >= d.PointY && pointY < d.PointY + (d.PixelH / d.Scale))
            {
                return d.Scale;
            }
        }

        return _displays.Count > 0 ? _displays[0].Scale : 1.0;
    }

    private (double X, double Y) ToPoint(int x, int y)
    {
        foreach (DisplayBox d in _displays)
        {
            if (x >= d.PixelX && x < d.PixelX + d.PixelW && y >= d.PixelY && y < d.PixelY + d.PixelH)
            {
                return (d.PointX + (x - d.PixelX) / d.Scale, d.PointY + (y - d.PixelY) / d.Scale);
            }
        }

        // Outside every known display (or none loaded): fall back to the primary scale.
        double scale = _displays.Count > 0 ? _displays[0].Scale : 1.0;
        return (x / scale, y / scale);
    }

    private static List<DisplayBox> LoadDisplays()
    {
        var result = new List<DisplayBox>();
        int count = MacShim.fd_displays_get(null, 0);
        if (count <= 0)
        {
            return result;
        }

        var arr = new MacShim.FdDisplay[count];
        int got = MacShim.fd_displays_get(arr, count);
        for (int i = 0; i < got; i++)
        {
            MacShim.FdDisplay d = arr[i];
            double scale = d.Scale > 0 ? d.Scale : 1.0;
            // The reported origin is in points; place the display on a pixel grid so a pixel coordinate can be
            // matched to it and then converted back to points.
            result.Add(new DisplayBox((int)(d.X * scale), (int)(d.Y * scale), d.Width, d.Height, d.X, d.Y, scale));
        }

        return result;
    }

    private static int ButtonOf(MouseButtons buttons) => buttons switch
    {
        MouseButtons.Left => 0,
        MouseButtons.Right => 1,
        MouseButtons.Middle => 2,
        _ => -1,
    };

    private static int NormalizeWheel(int delta) => Math.Clamp(delta / 40, -10, 10) is var n && n != 0 ? n : Math.Sign(delta);

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (_handle != 0)
            {
                MacShim.fd_input_destroy(_handle);
            }
        }

        return ValueTask.CompletedTask;
    }

    private readonly record struct DisplayBox(int PixelX, int PixelY, int PixelW, int PixelH, int PointX, int PointY, double Scale);
}
