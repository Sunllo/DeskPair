using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Abstractions.Input;
using DeskPair.Protocol.Messages;
using ProtoControlKey = DeskPair.Protocol.Messages.ControlKey;
using ProtoKeyboardMode = DeskPair.Protocol.Messages.KeyboardMode;

namespace DeskPair.Core.Session.Host.Handlers;

/// <summary>Translates wire mouse/keyboard events into injector calls, gated by the keyboard permission.</summary>
public sealed class InputHandler : ISessionHandler<HostSessionContext>
{
    private const int TypeMask = 0x7;
    private const int TypeMove = 0;
    private const int TypeDown = 1;
    private const int TypeUp = 2;
    private const int TypeWheel = 3;
    private const int TypeMoveRelative = 5;

    private readonly IInputInjector _injector;
    private readonly IDisplayEnumerator _displays;
    private readonly TimeProvider _time;
    private readonly Dictionary<int, DateTimeOffset> _lastInput = new();
    private readonly object _lock = new();

    public InputHandler(IInputInjector injector, IDisplayEnumerator displays, TimeProvider time)
    {
        _injector = injector;
        _displays = displays;
        _time = time;
    }

    public IEnumerable<Message.UnionOneofCase> Handles => [Message.UnionOneofCase.MouseEvent, Message.UnionOneofCase.KeyEvent];

    /// <summary>True when the connection injected input within the given window (used to suppress cursor echo).</summary>
    public bool InjectedRecently(int connectionId, TimeSpan window)
    {
        lock (_lock)
        {
            return _lastInput.TryGetValue(connectionId, out DateTimeOffset t) && _time.GetUtcNow() - t < window;
        }
    }

    public ValueTask HandleAsync(HostSessionContext context, Message message, CancellationToken ct)
    {
        if (!context.Permissions.Has(Permission.PermKeyboard))
        {
            return ValueTask.CompletedTask;
        }

        lock (_lock)
        {
            _lastInput[context.ConnectionId] = _time.GetUtcNow();
        }

        _injector.EnsureInputDesktop();
        switch (message.UnionCase)
        {
            case Message.UnionOneofCase.MouseEvent:
                HandleMouse(message.MouseEvent);
                break;
            case Message.UnionOneofCase.KeyEvent:
                HandleKey(message.KeyEvent);
                break;
        }

        return ValueTask.CompletedTask;
    }

    private void HandleMouse(MouseEvent e)
    {
        int type = e.Mask & TypeMask;
        var buttons = (MouseButtons)((e.Mask >> 3) & 0x1F);
        IReadOnlyList<DisplayDescriptor> displays = _displays.GetDisplays();
        VirtualScreenRect virt = VirtualScreen(displays);

        // The viewer's coordinates are pixels of the display it is watching (its picture starts at 0,0
        // whichever monitor that is); the injector wants the virtual screen, where a second monitor starts
        // at the first one's width. Without this a click on display 1 landed on display 0.
        int x = e.X;
        int y = e.Y;
        if (e.Display >= 0 && e.Display < displays.Count)
        {
            x += displays[e.Display].X;
            y += displays[e.Display].Y;
        }

        MouseInput input = type switch
        {
            TypeMove => new MouseInput(MouseAction.Move, MouseButtons.None, x, y, 0),
            TypeMoveRelative => new MouseInput(MouseAction.MoveRelative, MouseButtons.None, Math.Clamp(e.X, -10_000, 10_000), Math.Clamp(e.Y, -10_000, 10_000), 0),
            TypeDown => new MouseInput(MouseAction.Down, buttons, x, y, 0),
            TypeUp => new MouseInput(MouseAction.Up, buttons, x, y, 0),
            TypeWheel when e.X != 0 => new MouseInput(MouseAction.HorizontalWheel, MouseButtons.None, 0, 0, -e.X),
            TypeWheel => new MouseInput(MouseAction.Wheel, MouseButtons.None, 0, 0, e.Y),
            _ => default,
        };
        if (type is TypeDown or TypeUp or TypeWheel or TypeMove or TypeMoveRelative)
        {
            _injector.InjectMouse(input, virt);
        }
    }

    private void HandleKey(KeyEvent e)
    {
        KeyInputMode mode = e.Mode == ProtoKeyboardMode.KmMap ? KeyInputMode.Map : KeyInputMode.Translate;
        KeyInput input = e.UnionCase switch
        {
            KeyEvent.UnionOneofCase.ControlKey => new KeyInput(KeyInputMode.Translate, e.Down, 0, MapControlKey(e.ControlKey), null),
            KeyEvent.UnionOneofCase.Chr => new KeyInput(mode, e.Down, e.Chr, Platform.Abstractions.Input.ControlKey.None, null),
            KeyEvent.UnionOneofCase.Unicode => new KeyInput(KeyInputMode.Translate, e.Down, 0, Platform.Abstractions.Input.ControlKey.None, char.ConvertFromUtf32((int)e.Unicode)),
            KeyEvent.UnionOneofCase.Seq => new KeyInput(KeyInputMode.Translate, true, 0, Platform.Abstractions.Input.ControlKey.None, e.Seq),
            _ => default,
        };

        if (e.UnionCase == KeyEvent.UnionOneofCase.ControlKey && e.ControlKey == ProtoControlKey.CkCtrlAltDel)
        {
            if (e.Down)
            {
                _injector.SendCtrlAltDel();
            }

            return;
        }

        if (e.UnionCase == KeyEvent.UnionOneofCase.ControlKey && e.ControlKey == ProtoControlKey.CkLockScreen)
        {
            if (e.Down)
            {
                _injector.LockWorkstation();
            }

            return;
        }

        if (e.Press)
        {
            _injector.InjectKey(input with { Down = true });
            _injector.InjectKey(input with { Down = false });
        }
        else if (e.UnionCase != KeyEvent.UnionOneofCase.None)
        {
            _injector.InjectKey(input);
        }
    }

    private static VirtualScreenRect VirtualScreen(IReadOnlyList<DisplayDescriptor> displays)
    {
        if (displays.Count == 0)
        {
            return new VirtualScreenRect(0, 0, 1, 1);
        }

        int minX = displays.Min(d => d.X);
        int minY = displays.Min(d => d.Y);
        int maxX = displays.Max(d => d.X + d.Width);
        int maxY = displays.Max(d => d.Y + d.Height);
        return new VirtualScreenRect(minX, minY, maxX - minX, maxY - minY);
    }

    public static Platform.Abstractions.Input.ControlKey MapControlKey(ProtoControlKey key) => key switch
    {
        ProtoControlKey.CkAlt => Platform.Abstractions.Input.ControlKey.Alt,
        ProtoControlKey.CkBackspace => Platform.Abstractions.Input.ControlKey.Backspace,
        ProtoControlKey.CkCapsLock => Platform.Abstractions.Input.ControlKey.CapsLock,
        ProtoControlKey.CkControl => Platform.Abstractions.Input.ControlKey.Control,
        ProtoControlKey.CkDelete => Platform.Abstractions.Input.ControlKey.Delete,
        ProtoControlKey.CkDownArrow => Platform.Abstractions.Input.ControlKey.DownArrow,
        ProtoControlKey.CkEnd => Platform.Abstractions.Input.ControlKey.End,
        ProtoControlKey.CkEscape => Platform.Abstractions.Input.ControlKey.Escape,
        ProtoControlKey.CkF1 => Platform.Abstractions.Input.ControlKey.F1,
        ProtoControlKey.CkF2 => Platform.Abstractions.Input.ControlKey.F2,
        ProtoControlKey.CkF3 => Platform.Abstractions.Input.ControlKey.F3,
        ProtoControlKey.CkF4 => Platform.Abstractions.Input.ControlKey.F4,
        ProtoControlKey.CkF5 => Platform.Abstractions.Input.ControlKey.F5,
        ProtoControlKey.CkF6 => Platform.Abstractions.Input.ControlKey.F6,
        ProtoControlKey.CkF7 => Platform.Abstractions.Input.ControlKey.F7,
        ProtoControlKey.CkF8 => Platform.Abstractions.Input.ControlKey.F8,
        ProtoControlKey.CkF9 => Platform.Abstractions.Input.ControlKey.F9,
        ProtoControlKey.CkF10 => Platform.Abstractions.Input.ControlKey.F10,
        ProtoControlKey.CkF11 => Platform.Abstractions.Input.ControlKey.F11,
        ProtoControlKey.CkF12 => Platform.Abstractions.Input.ControlKey.F12,
        ProtoControlKey.CkHome => Platform.Abstractions.Input.ControlKey.Home,
        ProtoControlKey.CkLeftArrow => Platform.Abstractions.Input.ControlKey.LeftArrow,
        ProtoControlKey.CkMeta => Platform.Abstractions.Input.ControlKey.Meta,
        ProtoControlKey.CkOption => Platform.Abstractions.Input.ControlKey.Alt,
        ProtoControlKey.CkPageDown => Platform.Abstractions.Input.ControlKey.PageDown,
        ProtoControlKey.CkPageUp => Platform.Abstractions.Input.ControlKey.PageUp,
        ProtoControlKey.CkReturn => Platform.Abstractions.Input.ControlKey.Return,
        ProtoControlKey.CkRightArrow => Platform.Abstractions.Input.ControlKey.RightArrow,
        ProtoControlKey.CkShift => Platform.Abstractions.Input.ControlKey.Shift,
        ProtoControlKey.CkSpace => Platform.Abstractions.Input.ControlKey.Space,
        ProtoControlKey.CkTab => Platform.Abstractions.Input.ControlKey.Tab,
        ProtoControlKey.CkUpArrow => Platform.Abstractions.Input.ControlKey.UpArrow,
        ProtoControlKey.CkNumpad0 => Platform.Abstractions.Input.ControlKey.Numpad0,
        ProtoControlKey.CkNumpad1 => Platform.Abstractions.Input.ControlKey.Numpad1,
        ProtoControlKey.CkNumpad2 => Platform.Abstractions.Input.ControlKey.Numpad2,
        ProtoControlKey.CkNumpad3 => Platform.Abstractions.Input.ControlKey.Numpad3,
        ProtoControlKey.CkNumpad4 => Platform.Abstractions.Input.ControlKey.Numpad4,
        ProtoControlKey.CkNumpad5 => Platform.Abstractions.Input.ControlKey.Numpad5,
        ProtoControlKey.CkNumpad6 => Platform.Abstractions.Input.ControlKey.Numpad6,
        ProtoControlKey.CkNumpad7 => Platform.Abstractions.Input.ControlKey.Numpad7,
        ProtoControlKey.CkNumpad8 => Platform.Abstractions.Input.ControlKey.Numpad8,
        ProtoControlKey.CkNumpad9 => Platform.Abstractions.Input.ControlKey.Numpad9,
        ProtoControlKey.CkNumpadEnter => Platform.Abstractions.Input.ControlKey.NumpadEnter,
        ProtoControlKey.CkMultiply => Platform.Abstractions.Input.ControlKey.Multiply,
        ProtoControlKey.CkAdd => Platform.Abstractions.Input.ControlKey.Add,
        ProtoControlKey.CkSubtract => Platform.Abstractions.Input.ControlKey.Subtract,
        ProtoControlKey.CkDecimal => Platform.Abstractions.Input.ControlKey.Decimal,
        ProtoControlKey.CkDivide => Platform.Abstractions.Input.ControlKey.Divide,
        ProtoControlKey.CkInsert => Platform.Abstractions.Input.ControlKey.Insert,
        ProtoControlKey.CkPause => Platform.Abstractions.Input.ControlKey.Pause,
        ProtoControlKey.CkSnapshot => Platform.Abstractions.Input.ControlKey.PrintScreen,
        ProtoControlKey.CkScroll => Platform.Abstractions.Input.ControlKey.ScrollLock,
        ProtoControlKey.CkNumLock => Platform.Abstractions.Input.ControlKey.NumLock,
        ProtoControlKey.CkMenu or ProtoControlKey.CkApps => Platform.Abstractions.Input.ControlKey.Menu,
        ProtoControlKey.CkRshift => Platform.Abstractions.Input.ControlKey.RightShift,
        ProtoControlKey.CkRcontrol => Platform.Abstractions.Input.ControlKey.RightControl,
        ProtoControlKey.CkRalt => Platform.Abstractions.Input.ControlKey.RightAlt,
        ProtoControlKey.CkRwin => Platform.Abstractions.Input.ControlKey.RightMeta,
        ProtoControlKey.CkVolumeMute => Platform.Abstractions.Input.ControlKey.VolumeMute,
        ProtoControlKey.CkVolumeUp => Platform.Abstractions.Input.ControlKey.VolumeUp,
        ProtoControlKey.CkVolumeDown => Platform.Abstractions.Input.ControlKey.VolumeDown,
        _ => Platform.Abstractions.Input.ControlKey.None,
    };
}
