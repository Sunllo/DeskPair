using DeskPair.Platform.Abstractions.Input;
using DeskPair.Platform.Linux.Hosting;
using DeskPair.Platform.Linux.Native;
using Microsoft.Extensions.Logging;

namespace DeskPair.Platform.Linux.Input;

/// <summary>
/// Input through the daemon's virtual keyboard and pointer: the path that reaches the login screen and
/// the lock screen, where there is no X server for XTest to talk to.
///
/// Same dispatch as <see cref="X11InputInjector"/>, one layer lower. The pointer is placed absolutely —
/// the engine cannot ask where the pointer is at the login screen, and a relative pointer driven blind
/// accumulates the compositor's acceleration into an error — scaled to the full axis range the daemon
/// registered, against the virtual screen it is given. The compositor spreads that range over its whole
/// desktop, so the virtual screen is taken to be the desktop: with one monitor it is, and with more the
/// caller that knows the compositor's layout gives the desktop and the point on it
/// (<see cref="Wayland.ScanoutPortalHost"/>, from the session agent). Without that -- the login screen of
/// a machine with two monitors -- a click lands off target; typing is unaffected.
///
/// What it claims honestly: mouse, keyboard, and locking the screen. Not the lock-key lamps, which it
/// cannot read back; and not secure attention, on purpose -- through uinput Ctrl+Alt+Del is real, and on
/// a Linux console real Ctrl+Alt+Del is a reboot. A button on the viewer that restarts the host is the
/// kind of thing found by a user rather than by a test, so the button does nothing here.
/// </summary>
public sealed class UinputInputInjector : IInputInjector
{
    /// <summary>
    /// The pause between typed characters. The kernel keeps each reader's events for a device in a small queue (64
    /// on the lab machine's keyboard), and a character is four events, eight with Shift. Written as fast as the
    /// loop runs, a string of more than a handful overflows the compositor's queue before it reads, and everything
    /// after the overflow is gone: twenty-six letters arrived as "abcdefg". Two milliseconds a character is still
    /// five hundred a second, faster than anybody types, and leaves the compositor thirty to catch up in.
    /// </summary>
    internal static readonly TimeSpan CharacterInterval = TimeSpan.FromMilliseconds(2);

    private readonly Action<int, ushort, ushort, int> _emit;
    private readonly Action<TimeSpan> _pause;
    private readonly int _keyboard;
    private readonly int _pointer;
    private readonly int _relative;
    private readonly Func<string?> _lockSeat;
    private readonly ILogger _log;
    private readonly HashSet<ushort> _pressedKeys = [];
    private readonly HashSet<ushort> _pressedButtons = [];
    private bool _disposed;

    /// <param name="keyboardFd">The daemon's keyboard, received over the socketpair.</param>
    /// <param name="pointerFd">The daemon's absolute pointer, likewise: placement and buttons.</param>
    /// <param name="relativeFd">The daemon's relative pointer: deltas, wheels, the nudge.</param>
    /// <param name="log">Where a character that cannot be typed is said, once per character.</param>
    /// <param name="lockSeat">
    /// Locks whoever is on the seat at the moment of the request, and answers null when it did or the
    /// reason when it did not. This engine outlives sessions -- started at the login screen, still running
    /// after somebody signs in -- so the session to lock is decided per request, never remembered from
    /// start-up. Under the daemon this is a request to it, because polkit will not let the engine's own
    /// account lock another user's session; the default asks logind directly, for an engine that is root.
    /// </param>
    public UinputInputInjector(int keyboardFd, int pointerFd, int relativeFd, ILogger log, Func<string?>? lockSeat = null)
        : this(keyboardFd, pointerFd, relativeFd, log, static (fd, type, code, value) => Uinput.Emit(fd, type, code, value), lockSeat)
    {
    }

    /// <summary>For tests: every event goes to <paramref name="emit"/> instead of a device.</summary>
    internal UinputInputInjector(
        int keyboardFd, int pointerFd, int relativeFd, ILogger log, Action<int, ushort, ushort, int> emit, Func<string?>? lockSeat = null, Action<TimeSpan>? pause = null)
    {
        _pause = pause ?? Thread.Sleep;
        _keyboard = keyboardFd;
        _pointer = pointerFd;
        _relative = relativeFd;
        _lockSeat = lockSeat ?? LockSignedInSession;
        _log = log;
        HasDevices = keyboardFd >= 0 && pointerFd >= 0 && relativeFd >= 0;

        // A daemon on a machine without /dev/uinput hands over no devices (-1). Writing to -1 would be
        // EBADF on every keystroke; dropping the events here keeps the rest of the engine -- the lock
        // request, and later the terminal -- exactly as it is.
        _emit = HasDevices ? emit : static (_, _, _, _) => { };
        if (!HasDevices)
        {
            log.LogWarning("No virtual input devices: keyboard and pointer input from viewers is ignored on this machine");
        }
    }

    /// <summary>False when the daemon had no <c>/dev/uinput</c> to create devices from; every event is then dropped.</summary>
    public bool HasDevices { get; }

    public InputCapabilities Capabilities => InputCapabilities.Mouse | InputCapabilities.Keyboard | InputCapabilities.LockScreen;

    public void EnsureInputDesktop()
    {
        // There is one desktop, and the kernel delivers to whatever is on it.
    }

    public void InjectMouse(in MouseInput input, in VirtualScreenRect virtualScreen)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        switch (input.Action)
        {
            case MouseAction.Move:
                Place(input.X, input.Y, virtualScreen);
                break;

            case MouseAction.MoveRelative:
                _emit(_relative, Uinput.EvRel, Uinput.RelX, input.X);
                _emit(_relative, Uinput.EvRel, Uinput.RelY, input.Y);
                Sync(_relative);
                break;

            case MouseAction.Down:
            case MouseAction.Up:
                ushort button = ButtonOf(input.Buttons);
                if (button != 0)
                {
                    Place(input.X, input.Y, virtualScreen);
                    bool down = input.Action == MouseAction.Down;
                    _emit(_pointer, Uinput.EvKey, button, down ? 1 : 0);
                    Sync(_pointer);
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
                Wheel(Uinput.RelWheel, input.WheelDelta);
                break;

            case MouseAction.HorizontalWheel:
                Wheel(Uinput.RelHWheel, input.WheelDelta);
                break;
        }
    }

    /// <summary>The pointer at a screen position, as values of the axes the daemon registered.</summary>
    private void Place(int x, int y, in VirtualScreenRect screen)
    {
        _emit(_pointer, Uinput.EvAbs, Uinput.AbsX, Axis(x - screen.X, screen.Width));
        _emit(_pointer, Uinput.EvAbs, Uinput.AbsY, Axis(y - screen.Y, screen.Height));
        Sync(_pointer);
    }

    /// <summary>
    /// The value for the centre of pixel <paramref name="pixel"/> of <paramref name="size"/>. Compositors scale an
    /// absolute axis as libinput does, (value - minimum) x size / (maximum - minimum + 1), and the centre's value
    /// comes back as the pixel exactly for any screen up to 32768 wide. Aiming at the pixel's edge, as this did, came
    /// back a pixel short for the first few dozen pixels of a wide desktop.
    /// </summary>
    internal static int Axis(int pixel, int size)
    {
        size = Math.Max(size, 1);
        long value = (2L * Math.Clamp(pixel, 0, size - 1) + 1) * (Uinput.AbsMax + 1L) / (2L * size);
        return (int)Math.Min(value, Uinput.AbsMax);
    }

    /// <summary>One notch per unit of the delta's magnitude, in its direction, as X does with buttons 4 and 5.</summary>
    private void Wheel(ushort axis, int delta)
    {
        int notches = Math.Abs(delta);
        int step = delta > 0 ? 1 : -1;
        for (int i = 0; i < notches; i++)
        {
            _emit(_relative, Uinput.EvRel, axis, step);
            Sync(_relative);
        }
    }

    private static ushort ButtonOf(MouseButtons buttons) => buttons switch
    {
        MouseButtons.Left => Uinput.BtnLeft,
        MouseButtons.Right => Uinput.BtnRight,
        MouseButtons.Middle => Uinput.BtnMiddle,
        MouseButtons.Back => Uinput.BtnSide,
        MouseButtons.Forward => Uinput.BtnExtra,
        _ => 0,
    };

    public void InjectKey(in KeyInput input)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (input.Control != ControlKey.None)
        {
            ushort code = Evdev.ForControl(input.Control);
            if (code != 0)
            {
                Key(code, input.Down);
            }

            return;
        }

        // A positional key (Map mode): the protocol's code is the evdev code itself. X11InputInjector adds
        // eight to make an X keycode; there is nothing to add here.
        if (input.Mode == KeyInputMode.Map && input.Code != 0)
        {
            if (input.Code <= Evdev.MaxKey)
            {
                Key((ushort)input.Code, input.Down);
            }

            return;
        }

        // Typed text: each character pressed and released, with Shift where the US layout needs it.
        // Text carries its own key state, so Down is ignored. Anything the table cannot type is dropped,
        // and said once, rather than typed as some other character.
        if (!string.IsNullOrEmpty(input.Text))
        {
            bool first = true;
            foreach (char c in input.Text)
            {
                (ushort Code, bool Shift)? key = Evdev.ForChar(c);
                if (key is null)
                {
                    _log.LogWarning("Cannot type U+{Code:X4} through the virtual keyboard: only US-layout ASCII is typeable there", (int)c);
                    continue;
                }

                if (!first)
                {
                    _pause(CharacterInterval);
                }

                first = false;
                if (key.Value.Shift)
                {
                    Key(Evdev.KeyLeftShift, true);
                }

                Key(key.Value.Code, true);
                Key(key.Value.Code, false);
                if (key.Value.Shift)
                {
                    Key(Evdev.KeyLeftShift, false);
                }
            }
        }
    }

    private void Key(ushort code, bool down)
    {
        _emit(_keyboard, Uinput.EvKey, code, down ? 1 : 0);
        Sync(_keyboard);
        if (down)
        {
            _pressedKeys.Add(code);
        }
        else
        {
            _pressedKeys.Remove(code);
        }
    }

    private void Sync(int fd) => _emit(fd, Uinput.EvSyn, Uinput.SynReport, 0);

    /// <summary>
    /// A pointer movement too small to notice, to wake a display the compositor has turned off.
    ///
    /// An idle login screen switches the CRTC off entirely, and then there is no framebuffer to read --
    /// not a black one, none. A person would walk up and move the mouse; this is that.
    /// </summary>
    public void Nudge()
    {
        _emit(_relative, Uinput.EvRel, Uinput.RelX, 1);
        Sync(_relative);
        _emit(_relative, Uinput.EvRel, Uinput.RelX, -1);
        Sync(_relative);
    }

    /// <summary>Not readable through uinput, so not claimed: the default is the honest answer, as on X11.</summary>
    public LockKeyStates GetLockKeyStates() => default;

    public void SetLockKeyStates(LockKeyStates states)
    {
    }

    /// <summary>Every key and button this injector still has down, lifted. Called when a session ends.</summary>
    public void ReleaseAll()
    {
        foreach (ushort code in _pressedKeys.ToArray())
        {
            _emit(_keyboard, Uinput.EvKey, code, 0);
        }

        Sync(_keyboard);
        foreach (ushort button in _pressedButtons.ToArray())
        {
            _emit(_pointer, Uinput.EvKey, button, 0);
        }

        Sync(_pointer);
        _pressedKeys.Clear();
        _pressedButtons.Clear();
    }

    /// <summary>Deliberately nothing: real Ctrl+Alt+Del on a Linux console reboots the machine.</summary>
    public void SendCtrlAltDel()
    {
    }

    public void LockWorkstation()
    {
        if (_lockSeat() is { } reason)
        {
            _log.LogWarning("Cannot lock the screen: {Reason}", reason);
        }
    }

    /// <summary>Asks logind directly: for an engine that is root, or a test bench. The login screen is not lockable.</summary>
    private static string? LockSignedInSession()
    {
        ActiveSession? active = LinuxSessions.Active();
        if (active is not { Role: SessionRole.User })
        {
            return "nobody is signed in on the seat, so there is nothing to lock";
        }

        return LinuxSessions.Lock(active.Id) ? null : $"logind refused to lock session {active.Id}";
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
}
