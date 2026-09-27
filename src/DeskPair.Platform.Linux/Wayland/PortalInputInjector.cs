using System.Text;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Abstractions.Clipboard;
using DeskPair.Platform.Abstractions.Input;
using DeskPair.Platform.Linux.Hosting;
using DeskPair.Platform.Linux.Native;
using Microsoft.Extensions.Logging;

namespace DeskPair.Platform.Linux.Wayland;

/// <summary>The input half of a portal session: what <see cref="PortalInputInjector"/> sends, and to whom.</summary>
internal interface IPortalInput
{
    /// <summary>Whether the person allowed the pointer (the dialog's "allow remote interaction").</summary>
    bool Pointer { get; }

    /// <summary>Whether the person allowed the keyboard.</summary>
    bool Keyboard { get; }

    /// <summary>The pointer to (<paramref name="x"/>, <paramref name="y"/>) in <paramref name="stream"/>'s logical coordinates.</summary>
    void PointerMotionAbsolute(uint stream, double x, double y);

    void PointerMotion(double dx, double dy);

    /// <summary>An evdev button code (<c>BTN_LEFT</c> and so on).</summary>
    void PointerButton(int button, bool pressed);

    /// <summary>Axis 0 is vertical, 1 horizontal; a positive step scrolls down or right.</summary>
    void PointerAxisDiscrete(uint axis, int steps);

    /// <summary>An evdev key code: the X keycode less eight.</summary>
    void KeyboardKeycode(int keycode, bool pressed);

    /// <summary>An X keysym, which the compositor finds a key for on the current layout.</summary>
    void KeyboardKeysym(int keysym, bool pressed);
}

/// <summary>
/// Pointer and keyboard on a Wayland desktop, through the portal's RemoteDesktop session. A compositor accepts
/// input from no client on its own say, and the uinput devices the scanout daemon owns are root's; the portal is
/// how an ordinary process in the user's session gets to move the pointer, once the person allowed it.
///
/// The coordinates are where this goes wrong first. The engine hands over a point on its virtual screen, in pixels;
/// the portal wants a point on one stream, in that stream's logical units, and the stream's node to say which.
/// <see cref="ToStream"/> does that conversion, and is pure so it can be tested anywhere. The keyboard is the other
/// place: the portal's keycodes are evdev codes, which is what the protocol's positional codes already are -- an X
/// keycode would put every key one row off. Text goes as keysyms, which the compositor places on the current layout
/// itself -- as far as the layout has keys: GNOME drops a keysym the layout cannot produce ("No keycode found for
/// keyval"), so text with anything beyond printable ASCII is pasted through the clipboard instead, as on X11.
/// </summary>
public sealed class PortalInputInjector : IInputInjector
{
    private const int Pressed = 1;

    /// <summary>How long the clipboard is left holding pasted text before what was there is put back.</summary>
    private static readonly TimeSpan PasteSettle = TimeSpan.FromMilliseconds(250);

    private readonly IPortalInput _portal;
    private readonly Func<IReadOnlyList<DisplayDescriptor>> _displays;
    private readonly ILogger _log;
    private readonly IClipboard? _clipboard;
    private readonly Action<TimeSpan> _pause;
    private readonly object _lock = new();
    private readonly HashSet<int> _pressedKeys = [];
    private readonly HashSet<int> _pressedButtons = [];
    private bool _saidNoPointer;
    private bool _saidNoKeyboard;
    private bool _disposed;

    /// <param name="session">The portal session whose devices this drives.</param>
    /// <param name="displays">The session's monitors as the engine sees them, for turning a point into a stream.</param>
    /// <param name="log">Where a refused or impossible event is said, once.</param>
    /// <param name="clipboard">
    /// The desktop's clipboard, for text the keyboard layout cannot type; without one such text is sent as keysyms and
    /// whatever the layout has no key for is lost.
    /// </param>
    internal PortalInputInjector(IPortalSession session, IDisplayEnumerator displays, ILogger log, IClipboard? clipboard = null)
        : this(session, displays.GetDisplays, log, clipboard)
    {
    }

    internal PortalInputInjector(
        IPortalInput portal, Func<IReadOnlyList<DisplayDescriptor>> displays, ILogger log, IClipboard? clipboard = null, Action<TimeSpan>? pause = null)
    {
        _portal = portal;
        _displays = displays;
        _log = log;
        _clipboard = clipboard;
        _pause = pause ?? Thread.Sleep;
    }

    /// <summary>
    /// What the person allowed, and locking the screen, which is logind's. The lock keys cannot be read through the
    /// portal, and Ctrl+Alt+Del is three keys to the compositor rather than an attention sequence.
    /// </summary>
    public InputCapabilities Capabilities =>
        (_portal.Pointer ? InputCapabilities.Mouse : InputCapabilities.None) |
        (_portal.Keyboard ? InputCapabilities.Keyboard : InputCapabilities.None) |
        InputCapabilities.LockScreen;

    public void EnsureInputDesktop()
    {
        // One desktop, and the compositor delivers to whatever has focus on it.
    }

    public void InjectMouse(in MouseInput input, in VirtualScreenRect virtualScreen)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_portal.Pointer)
        {
            SayOnce(ref _saidNoPointer, "the pointer");
            return;
        }

        switch (input.Action)
        {
            case MouseAction.Move:
                Place(input.X, input.Y);
                break;

            case MouseAction.MoveRelative:
                _portal.PointerMotion(input.X, input.Y);
                break;

            case MouseAction.Down:
            case MouseAction.Up:
                int button = ButtonOf(input.Buttons);
                if (button != 0)
                {
                    Place(input.X, input.Y);
                    bool down = input.Action == MouseAction.Down;
                    _portal.PointerButton(button, down);
                    lock (_lock)
                    {
                        _ = down ? _pressedButtons.Add(button) : _pressedButtons.Remove(button);
                    }
                }

                break;

            case MouseAction.Wheel:
                // The protocol's wheel is positive away from the user, the portal's positive towards: down.
                if (input.WheelDelta != 0)
                {
                    _portal.PointerAxisDiscrete(0, -input.WheelDelta);
                }

                break;

            case MouseAction.HorizontalWheel:
                if (input.WheelDelta != 0)
                {
                    _portal.PointerAxisDiscrete(1, input.WheelDelta);
                }

                break;
        }
    }

    public void InjectKey(in KeyInput input)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_portal.Keyboard)
        {
            SayOnce(ref _saidNoKeyboard, "the keyboard");
            return;
        }

        if (input.Control != ControlKey.None)
        {
            ushort code = Evdev.ForControl(input.Control);
            if (code != 0)
            {
                Key(code, input.Down);
            }

            return;
        }

        // A positional key (Map mode): the protocol's code is the evdev code, which is what the portal takes.
        if (input.Mode == KeyInputMode.Map && input.Code != 0)
        {
            if (input.Code <= Evdev.MaxKey)
            {
                Key((int)input.Code, input.Down);
            }

            return;
        }

        // Typed text: each character pressed and released as a keysym. Text carries its own key state.
        if (!string.IsNullOrEmpty(input.Text))
        {
            if (_clipboard is not null && NeedsPaste(input.Text))
            {
                Paste(input.Text);
                return;
            }

            foreach (Rune rune in input.Text.EnumerateRunes())
            {
                int keysym = KeysymOf(rune);
                if (keysym == 0)
                {
                    continue;
                }

                _portal.KeyboardKeysym(keysym, true);
                _portal.KeyboardKeysym(keysym, false);
            }
        }
    }

    public LockKeyStates GetLockKeyStates() => default;

    public void SetLockKeyStates(LockKeyStates states)
    {
        // Not readable through the portal, so not settable either: toggling blind turns a lamp on as often as off.
    }

    public void ReleaseAll()
    {
        if (_disposed)
        {
            return;
        }

        int[] keys;
        int[] buttons;
        lock (_lock)
        {
            keys = [.. _pressedKeys];
            buttons = [.. _pressedButtons];
            _pressedKeys.Clear();
            _pressedButtons.Clear();
        }

        foreach (int key in keys)
        {
            _portal.KeyboardKeycode(key, false);
        }

        foreach (int button in buttons)
        {
            _portal.PointerButton(button, false);
        }
    }

    public void SendCtrlAltDel()
    {
        // The compositor's shortcut (GNOME's is the log-out dialog), not an attention sequence: through the portal
        // the keys reach the compositor, never the kernel's console handler.
        if (!_portal.Keyboard)
        {
            return;
        }

        Key(Evdev.KeyLeftCtrl, true);
        Key(Evdev.KeyLeftAlt, true);
        Key(Evdev.KeyDelete, true);
        Key(Evdev.KeyDelete, false);
        Key(Evdev.KeyLeftAlt, false);
        Key(Evdev.KeyLeftCtrl, false);
    }

    public void LockWorkstation()
    {
        // This engine is the session's own user, and logind lets a session's owner lock it.
        ActiveSession? active = LinuxSessions.Active();
        if (active is not { Role: SessionRole.User })
        {
            _log.LogInformation("Not locking: nobody is signed in on the seat");
            return;
        }

        if (!LinuxSessions.Lock(active.Id))
        {
            _log.LogWarning("logind refused to lock session {Session}", active.Id);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            ReleaseAll();
            _disposed = true;
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// A point on the engine's virtual screen as the portal wants it: the stream it is on and where on that stream,
    /// in the stream's logical units. A point off every monitor goes to the nearest edge of the nearest one, the way
    /// a pointer pushed past the edge of a screen stays on it. Null only when there are no monitors.
    /// </summary>
    internal static (uint Stream, double X, double Y)? ToStream(IReadOnlyList<DisplayDescriptor> displays, int x, int y)
    {
        if (displays.Count == 0)
        {
            return null;
        }

        DisplayDescriptor nearest = displays[0];
        long best = long.MaxValue;
        foreach (DisplayDescriptor d in displays)
        {
            long dx = x < d.X ? d.X - (long)x : x >= d.X + d.Width ? x - (long)(d.X + d.Width - 1) : 0;
            long dy = y < d.Y ? d.Y - (long)y : y >= d.Y + d.Height ? y - (long)(d.Y + d.Height - 1) : 0;
            long distance = (dx * dx) + (dy * dy);
            if (distance < best)
            {
                best = distance;
                nearest = d;
                if (distance == 0)
                {
                    break;
                }
            }
        }

        double scale = nearest.Scale > 0 ? nearest.Scale : 1.0;
        int px = Math.Clamp(x - nearest.X, 0, Math.Max(nearest.Width - 1, 0));
        int py = Math.Clamp(y - nearest.Y, 0, Math.Max(nearest.Height - 1, 0));
        return ((uint)nearest.AdapterLuid, px / scale, py / scale);
    }

    /// <summary>
    /// The X keysym for a typed character: its code point for Latin-1, the Unicode range above that, and the
    /// function keys for the control characters that mean something when typed. Zero for anything else.
    /// </summary>
    internal static int KeysymOf(Rune rune) => rune.Value switch
    {
        '\n' or '\r' => 0xFF0D, // Return
        '\t' => 0xFF09,         // Tab
        '\b' => 0xFF08,         // BackSpace
        < 0x20 or 0x7F => 0,
        (>= 0x20 and <= 0x7E) or (>= 0xA0 and <= 0xFF) => rune.Value,
        >= 0x80 and < 0xA0 => 0,
        _ => 0x01000000 | rune.Value,
    };

    /// <summary>Whether <paramref name="text"/> has a character a US layout has no key for: anything past printable ASCII.</summary>
    internal static bool NeedsPaste(string text)
    {
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (rune.Value > 0x7E)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Types <paramref name="text"/> by pasting it: the clipboard is saved, given the text, Ctrl+V is pressed, and the
    /// old contents are put back once the application has had time to ask for the new. Ctrl+V is what GUI applications
    /// paste with; a terminal wants Ctrl+Shift+V, so a terminal gets nothing -- the same gap X11 has.
    /// </summary>
    private void Paste(string text)
    {
        try
        {
            IReadOnlyList<ClipboardItem> saved = _clipboard!.ReadAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
            _clipboard.WriteAsync([new ClipboardItem(ClipboardItemFormat.Text, Encoding.UTF8.GetBytes(text))], CancellationToken.None).AsTask().GetAwaiter().GetResult();
            _pause(TimeSpan.FromMilliseconds(40));
            ushort v = Evdev.ForChar('v')?.Code ?? 47;
            Key(Evdev.KeyLeftCtrl, true);
            Key(v, true);
            Key(v, false);
            Key(Evdev.KeyLeftCtrl, false);
            _pause(PasteSettle);
            if (saved.Count > 0)
            {
                _clipboard.WriteAsync(saved, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _log.LogWarning(e, "Pasting text the keyboard layout cannot type failed");
        }
    }

    private void Place(int x, int y)
    {
        if (ToStream(_displays(), x, y) is { } point)
        {
            _portal.PointerMotionAbsolute(point.Stream, point.X, point.Y);
        }
    }

    private void Key(int code, bool down)
    {
        _portal.KeyboardKeycode(code, down);
        lock (_lock)
        {
            _ = down ? _pressedKeys.Add(code) : _pressedKeys.Remove(code);
        }
    }

    private void SayOnce(ref bool said, string what)
    {
        if (!said)
        {
            said = true;
            _log.LogWarning("Input to {What} is dropped: the person at this machine did not allow remote interaction when screen sharing started", what);
        }
    }

    private static int ButtonOf(MouseButtons buttons) => buttons switch
    {
        MouseButtons.Left => Uinput.BtnLeft,
        MouseButtons.Right => Uinput.BtnRight,
        MouseButtons.Middle => Uinput.BtnMiddle,
        MouseButtons.Back => Uinput.BtnSide,
        MouseButtons.Forward => Uinput.BtnExtra,
        _ => 0,
    };
}
