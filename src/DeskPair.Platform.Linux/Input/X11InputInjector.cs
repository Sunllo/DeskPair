using Microsoft.Extensions.Logging;
using DeskPair.Platform.Abstractions.Clipboard;
using DeskPair.Platform.Abstractions.Input;
using DeskPair.Platform.Linux.Native;

namespace DeskPair.Platform.Linux.Input;

/// <summary>
/// Injects mouse and keyboard through XTest, the X11 counterpart to SendInput. Absolute pointer moves go
/// straight in; keys arrive as either a named control key or typed text, both of which become keysyms and
/// then live keycodes — the controller is on another OS, so its raw scancodes (Map mode) are not portable
/// and this platform works from the semantic paths the protocol also provides.
///
/// A character with no key on the current layout is typed by borrowing a spare keycode, remapping it to the
/// character for one press, and restoring it — the standard trick for injecting arbitrary Unicode through
/// XTest, and what lets a controller type text the Linux keyboard has no key for.
/// </summary>
public sealed class X11InputInjector : IInputInjector
{
    private readonly ILogger _log;
    private readonly nint _dpy;
    private readonly Platform.Abstractions.Clipboard.IClipboard? _clipboard;
    private readonly HashSet<uint> _pressedKeys = [];
    private readonly HashSet<uint> _pressedButtons = [];
    private readonly int _spareKeycode;
    private readonly byte _shiftKeycode;
    private readonly Dictionary<char, (byte Keycode, bool Shift)> _charKeys = [];
    private bool _disposed;

    public X11InputInjector(ILogger log, Platform.Abstractions.Clipboard.IClipboard? clipboard = null)
    {
        Xlib.EnsureThreadSafe();
        _log = log;
        _clipboard = clipboard;
        _dpy = Xlib.XOpenDisplay(null);
        if (_dpy == 0)
        {
            throw new InvalidOperationException("Cannot open the X display for input.");
        }

        // The highest keycode is almost always unassigned; it is the one this borrows for arbitrary characters.
        XTest.XDisplayKeycodes(_dpy, out int min, out int max);
        _spareKeycode = max;
        _shiftKeycode = XTest.XKeysymToKeycode(_dpy, 0xFFE1); // XK_Shift_L
        BuildCharKeys(min, max);
    }

    /// <summary>
    /// Records, for every printable ASCII character, which real key produces it and whether Shift is needed.
    /// Typing through the real keys avoids remapping a spare keycode per character, which a modern GTK/XKB
    /// client resolves against a lagging copy of the keymap — so a fast remap-and-tap stream comes out as the
    /// last character repeated. Only characters with no key of their own (non-ASCII) fall back to the remap.
    /// </summary>
    private void BuildCharKeys(int min, int max)
    {
        int count = max - min + 1;
        nint mapping = XTest.XGetKeyboardMapping(_dpy, (byte)min, count, out int per);
        if (mapping == 0 || per <= 0)
        {
            return;
        }

        try
        {
            for (int i = 0; i < count; i++)
            {
                for (int level = 0; level < Math.Min(per, 2); level++)
                {
                    var keysym = (nuint)System.Runtime.InteropServices.Marshal.ReadIntPtr(mapping, (i * per + level) * nint.Size);
                    if (keysym is >= 0x20 and <= 0x7E && !_charKeys.ContainsKey((char)keysym))
                    {
                        _charKeys[(char)keysym] = ((byte)(min + i), level == 1);
                    }
                }
            }
        }
        finally
        {
            Xlib.XFree(mapping);
        }
    }

    public void EnsureInputDesktop()
    {
        // X has one input path per display connection; there is no secure-desktop switch to attach to.
    }

    public void InjectMouse(in MouseInput input, in VirtualScreenRect virtualScreen)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        switch (input.Action)
        {
            case MouseAction.Move:
                XTest.XTestFakeMotionEvent(_dpy, -1, input.X, input.Y, 0);
                break;
            case MouseAction.MoveRelative:
                // X and Y are a delta here, not a point. Sending them to XTestFakeMotionEvent warped the
                // pointer to the top-left corner of the screen instead of nudging it.
                XTest.XTestFakeRelativeMotionEvent(_dpy, input.X, input.Y, 0);
                break;
            case MouseAction.Down:
            case MouseAction.Up:
                uint button = ButtonOf(input.Buttons);
                if (button != 0)
                {
                    XTest.XTestFakeMotionEvent(_dpy, -1, input.X, input.Y, 0);
                    bool down = input.Action == MouseAction.Down;
                    XTest.XTestFakeButtonEvent(_dpy, button, down, 0);
                    if (down)
                    {
                        _pressedButtons.Add(button);
                    }
                    else
                    {
                        _pressedButtons.Remove(button);
                    }
                }

                break;
            case MouseAction.Wheel:
                // X wheels are buttons: 4 up, 5 down. One click per notch of the delta's sign.
                Wheel(input.WheelDelta > 0 ? 4u : 5u, Math.Abs(input.WheelDelta));
                break;
            case MouseAction.HorizontalWheel:
                Wheel(input.WheelDelta > 0 ? 7u : 6u, Math.Abs(input.WheelDelta));
                break;
        }

        Xlib.XFlush(_dpy);
    }

    public void InjectKey(in KeyInput input)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (input.Control != ControlKey.None)
        {
            nuint keysym = Keysyms.ForControl(input.Control);
            if (keysym != 0)
            {
                SendKeysym(keysym, input.Down);
            }

            return;
        }

        // A positional key by scancode (Map mode). This is how the controller sends the letter of a shortcut
        // (Ctrl+C, Alt+Tab): the modifier arrives as a held control key and the letter as its PC set-1
        // scancode, so it must be injected as a real key press with the modifier down, not typed as text
        // (typing ignores the modifier). Linux input keycodes are the PC set-1 scancodes and the X keycode is
        // that plus eight, which covers every letter, digit and symbol a shortcut uses.
        if (input.Mode == KeyInputMode.Map && input.Code != 0)
        {
            if (input.Code < 0x80)
            {
                uint keycode = input.Code + 8;
                XTest.XTestFakeKeyEvent(_dpy, keycode, input.Down, 0);
                if (input.Down)
                {
                    _pressedKeys.Add(keycode);
                }
                else
                {
                    _pressedKeys.Remove(keycode);
                }

                Xlib.XFlush(_dpy);
            }

            return;
        }

        // Typed text: press and release each character. Text carries its own key state, so this ignores Down.
        if (!string.IsNullOrEmpty(input.Text))
        {
            InjectText(input.Text);
            return;
        }
    }

    /// <summary>
    /// Types ASCII through real keys (fast and exact), but pastes anything with non-ASCII characters through
    /// the clipboard. Synthetic key injection of Unicode is unreliable on a GNOME session: the desktop runs
    /// IBus, which intercepts the injected keysyms and mangles them (and the characters after). A clipboard
    /// round-trip — save what is there, put the text on the clipboard, Ctrl+V, restore — reproduces the exact
    /// text and is what most tools do for CJK and emoji. It needs the paste target to accept Ctrl+V, which GUI
    /// apps do; a terminal wants Ctrl+Shift+V, so terminal Unicode paste stays a gap.
    /// </summary>
    private void InjectText(string text)
    {
        bool hasNonAscii = false;
        foreach (char c in text)
        {
            if (c > 0x7F)
            {
                hasNonAscii = true;
                break;
            }
        }

        if (hasNonAscii && _clipboard is not null)
        {
            PasteText(text);
            return;
        }

        foreach (char c in text)
        {
            TypeChar(c);
        }
    }

    private void PasteText(string text)
    {
        try
        {
            IReadOnlyList<ClipboardItem> saved = _clipboard!.ReadAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
            _clipboard.WriteAsync([new ClipboardItem(ClipboardItemFormat.Text, System.Text.Encoding.UTF8.GetBytes(text))], CancellationToken.None).AsTask().GetAwaiter().GetResult();
            Thread.Sleep(40); // let the selection ownership settle before the paste asks for it

            // Ctrl+V: hold Ctrl (a real modifier key), tap the V key, release Ctrl.
            byte v = _charKeys.TryGetValue('v', out (byte Keycode, bool Shift) key) ? key.Keycode : (byte)(0x2F + 8);
            SendKeysym(Keysyms.ForControl(ControlKey.Control), true);
            XTest.XTestFakeKeyEvent(_dpy, v, true, 0);
            XTest.XTestFakeKeyEvent(_dpy, v, false, 0);
            SendKeysym(Keysyms.ForControl(ControlKey.Control), false);
            Xlib.XFlush(_dpy);

            Thread.Sleep(80); // give the paste time to complete before restoring the clipboard
            if (saved.Count > 0)
            {
                _clipboard.WriteAsync(saved, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            }
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Clipboard paste of Unicode text failed");
        }
    }

    /// <summary>
    /// Pointer and keyboard, and nothing else that this interface offers.
    ///
    /// The lock keys are readable -- XkbGetIndicatorState -- but nothing reads them here, so saying so
    /// would be a claim about a value that is not fetched. Ctrl+Alt+Del is three keys sent at a desktop
    /// that may or may not have anything bound to them, which is not the sequence an application cannot
    /// intercept. And locking the screen belongs to the session's screensaver, so the method below does
    /// nothing at all: the viewer's button for it has been silently doing nothing on Linux.
    /// </summary>
    public InputCapabilities Capabilities => InputCapabilities.Mouse | InputCapabilities.Keyboard;

    public LockKeyStates GetLockKeyStates() => default; // X exposes these via XkbGetIndicatorState; not needed for control.

    public void SetLockKeyStates(LockKeyStates states)
    {
    }

    public void ReleaseAll()
    {
        if (_disposed)
        {
            return;
        }

        foreach (uint keycode in _pressedKeys.ToArray())
        {
            XTest.XTestFakeKeyEvent(_dpy, keycode, false, 0);
        }

        _pressedKeys.Clear();

        foreach (uint button in _pressedButtons.ToArray())
        {
            XTest.XTestFakeButtonEvent(_dpy, button, false, 0);
        }

        _pressedButtons.Clear();
        Xlib.XFlush(_dpy);
    }

    public void SendCtrlAltDel()
    {
        // Linux has no single VT-agnostic SAS; Ctrl+Alt+Del is a shortcut the DE or logind owns. Sending the
        // three keys is the closest a normal client can do, and reaches applications that bind it.
        SendKeysym(Keysyms.ForControl(ControlKey.Control), true);
        SendKeysym(Keysyms.ForControl(ControlKey.Alt), true);
        SendKeysym(Keysyms.ForControl(ControlKey.Delete), true);
        SendKeysym(Keysyms.ForControl(ControlKey.Delete), false);
        SendKeysym(Keysyms.ForControl(ControlKey.Alt), false);
        SendKeysym(Keysyms.ForControl(ControlKey.Control), false);
    }

    public void LockWorkstation()
    {
        // Owned by the session's screensaver/DE; no X primitive. Left to the host policy to wire to loginctl.
    }

    private void Wheel(uint button, int clicks)
    {
        for (int i = 0; i < Math.Max(1, clicks); i++)
        {
            XTest.XTestFakeButtonEvent(_dpy, button, true, 0);
            XTest.XTestFakeButtonEvent(_dpy, button, false, 0);
        }
    }

    private void SendKeysym(nuint keysym, bool down)
    {
        byte keycode = XTest.XKeysymToKeycode(_dpy, keysym);
        if (keycode == 0)
        {
            return;
        }

        XTest.XTestFakeKeyEvent(_dpy, keycode, down, 0);
        if (down)
        {
            _pressedKeys.Add(keycode);
        }
        else
        {
            _pressedKeys.Remove(keycode);
        }

        Xlib.XFlush(_dpy);
    }

    private void TypeChar(char c)
    {
        // Fast path: the character has a real key. Tap it, holding Shift when it lives on the shift level. No
        // remapping means no keymap-lag race, so a whole word types correctly at speed.
        if (_charKeys.TryGetValue(c, out (byte Keycode, bool Shift) key))
        {
            if (key.Shift && _shiftKeycode != 0)
            {
                XTest.XTestFakeKeyEvent(_dpy, _shiftKeycode, true, 0);
            }

            XTest.XTestFakeKeyEvent(_dpy, key.Keycode, true, 0);
            XTest.XTestFakeKeyEvent(_dpy, key.Keycode, false, 0);
            if (key.Shift && _shiftKeycode != 0)
            {
                XTest.XTestFakeKeyEvent(_dpy, _shiftKeycode, false, 0);
            }

            Xlib.XFlush(_dpy);
            return;
        }

        if (_spareKeycode <= 0)
        {
            return;
        }

        // Fallback for characters with no key of their own (non-ASCII): point a spare keycode at the exact
        // keysym and tap it. A GTK/XKB client resolves keys against its own copy of the keymap, which updates a
        // beat behind a core mapping change, so this races when done back to back; a short settle after the tap
        // lets the client apply the mapping notify before the next character overwrites it. This path is only
        // hit for the occasional Unicode glyph, so the pause does not slow ordinary typing.
        nuint keysym = Keysyms.ForChar(c);
        nuint[] map = [keysym, keysym];
        XTest.XChangeKeyboardMapping(_dpy, _spareKeycode, 2, map, 1);
        Xlib.XSync(_dpy, discard: false);

        // The tap must wait for the CLIENT — not just the server — to apply the mapping change. A GTK/XKB
        // client processes the MappingNotify on its own loop a beat later, and a tap sent before then resolves
        // against the stale keymap: the character comes out as the spare keycode's previous keysym (so a run of
        // remapped characters lags by one, and a following real key can be misread too). XSync only proves the
        // server has it, so a settle here gives the client time to catch up before the key is pressed.
        Thread.Sleep(40);
        XTest.XTestFakeKeyEvent(_dpy, (uint)_spareKeycode, true, 0);
        XTest.XTestFakeKeyEvent(_dpy, (uint)_spareKeycode, false, 0);
        Xlib.XSync(_dpy, discard: false);
        Thread.Sleep(20);
    }

    private static uint ButtonOf(MouseButtons buttons) => buttons switch
    {
        MouseButtons.Left => 1,
        MouseButtons.Middle => 2,
        MouseButtons.Right => 3,
        MouseButtons.Back => 8,
        MouseButtons.Forward => 9,
        _ => 0,
    };

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            ReleaseAll();
            if (_dpy != 0)
            {
                Xlib.XCloseDisplay(_dpy);
            }
        }

        return ValueTask.CompletedTask;
    }
}
