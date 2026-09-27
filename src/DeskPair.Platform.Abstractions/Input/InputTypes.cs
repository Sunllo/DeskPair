namespace DeskPair.Platform.Abstractions.Input;

[Flags]
public enum MouseButtons
{
    None = 0,
    Left = 1 << 0,
    Right = 1 << 1,
    Middle = 1 << 2,
    Back = 1 << 3,
    Forward = 1 << 4,
}

public enum MouseAction
{
    Move,
    MoveRelative,
    Down,
    Up,
    Wheel,
    HorizontalWheel,
}

/// <summary>Coordinates are absolute pixels in the host's virtual screen unless <see cref="MouseAction.MoveRelative"/>.</summary>
public readonly record struct MouseInput(MouseAction Action, MouseButtons Buttons, int X, int Y, int WheelDelta);

public enum KeyInputMode
{
    /// <summary>Positional: <see cref="KeyInput.Code"/> is a platform scancode/keycode.</summary>
    Map,
    /// <summary>Semantic: inject <see cref="KeyInput.Text"/> (unicode) or a named control key.</summary>
    Translate,
}

public enum ControlKey
{
    None,
    Alt,
    Backspace,
    CapsLock,
    Control,
    Delete,
    DownArrow,
    End,
    Escape,
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
    Home,
    LeftArrow,
    Meta,
    PageDown,
    PageUp,
    Return,
    RightArrow,
    Shift,
    Space,
    Tab,
    UpArrow,
    Numpad0, Numpad1, Numpad2, Numpad3, Numpad4, Numpad5, Numpad6, Numpad7, Numpad8, Numpad9,
    NumpadEnter,
    Multiply,
    Add,
    Subtract,
    Decimal,
    Divide,
    Insert,
    Pause,
    PrintScreen,
    ScrollLock,
    NumLock,
    Menu,
    RightShift,
    RightControl,
    RightAlt,
    RightMeta,
    VolumeMute,
    VolumeUp,
    VolumeDown,
}

public readonly record struct KeyInput(
    KeyInputMode Mode,
    bool Down,
    uint Code,
    ControlKey Control,
    string? Text);

public readonly record struct VirtualScreenRect(int X, int Y, int Width, int Height);

public readonly record struct LockKeyStates(bool CapsLock, bool NumLock, bool ScrollLock);
