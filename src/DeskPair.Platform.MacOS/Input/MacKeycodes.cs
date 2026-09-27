using DeskPair.Platform.Abstractions.Input;

namespace DeskPair.Platform.MacOS.Input;

/// <summary>
/// Maps the protocol's named control keys to macOS virtual key codes (Carbon <c>kVK_*</c>) and the modifier
/// keys to <c>CGEventFlags</c> bits. Ordinary text is injected as a unicode string instead, so only the keys
/// that have no character — modifiers, arrows, function and editing keys — need a code here.
/// </summary>
internal static class MacKeycodes
{
    // CGEventFlags modifier bits.
    public const ulong FlagShift = 1UL << 17;
    public const ulong FlagControl = 1UL << 18;
    public const ulong FlagAlternate = 1UL << 19; // Option
    public const ulong FlagCommand = 1UL << 20;

    /// <summary>The kVK_* virtual keycode for a control key, or -1 when it has none.</summary>
    public static int ForControl(ControlKey key) => key switch
    {
        ControlKey.Return or ControlKey.NumpadEnter => 0x24,
        ControlKey.Tab => 0x30,
        ControlKey.Space => 0x31,
        ControlKey.Backspace => 0x33,
        ControlKey.Escape => 0x35,
        ControlKey.Meta => 0x37,
        ControlKey.Shift => 0x38,
        ControlKey.CapsLock => 0x39,
        ControlKey.Alt => 0x3A,
        ControlKey.Control => 0x3B,
        ControlKey.RightShift => 0x3C,
        ControlKey.RightAlt => 0x3D,
        ControlKey.RightControl => 0x3E,
        ControlKey.RightMeta => 0x36,
        ControlKey.F1 => 0x7A,
        ControlKey.F2 => 0x78,
        ControlKey.F3 => 0x63,
        ControlKey.F4 => 0x76,
        ControlKey.F5 => 0x60,
        ControlKey.F6 => 0x61,
        ControlKey.F7 => 0x62,
        ControlKey.F8 => 0x64,
        ControlKey.F9 => 0x65,
        ControlKey.F10 => 0x6D,
        ControlKey.F11 => 0x67,
        ControlKey.F12 => 0x6F,
        ControlKey.Home => 0x73,
        ControlKey.PageUp => 0x74,
        ControlKey.Delete => 0x75, // forward delete
        ControlKey.End => 0x77,
        ControlKey.PageDown => 0x79,
        ControlKey.LeftArrow => 0x7B,
        ControlKey.RightArrow => 0x7C,
        ControlKey.DownArrow => 0x7D,
        ControlKey.UpArrow => 0x7E,
        ControlKey.Numpad0 => 0x52,
        ControlKey.Numpad1 => 0x53,
        ControlKey.Numpad2 => 0x54,
        ControlKey.Numpad3 => 0x55,
        ControlKey.Numpad4 => 0x56,
        ControlKey.Numpad5 => 0x57,
        ControlKey.Numpad6 => 0x58,
        ControlKey.Numpad7 => 0x59,
        ControlKey.Numpad8 => 0x5B,
        ControlKey.Numpad9 => 0x5C,
        ControlKey.Decimal => 0x41,
        ControlKey.Multiply => 0x43,
        ControlKey.Add => 0x45,
        ControlKey.Subtract => 0x4E,
        ControlKey.Divide => 0x4B,
        _ => -1,
    };

    /// <summary>
    /// Maps a PC set-1 scancode (as the controller sends a shortcut's positional key) to the macOS virtual
    /// key code, so Cmd+C and friends land on the right key regardless of layout. Covers the letters, digits
    /// and symbols a shortcut uses; returns -1 for anything else.
    /// </summary>
    public static int ForScancode(uint scan) => scan switch
    {
        0x1E => 0, 0x1F => 1, 0x20 => 2, 0x21 => 3, 0x23 => 4, 0x22 => 5, 0x2C => 6, 0x2D => 7, 0x2E => 8,
        0x2F => 9, 0x30 => 11, 0x10 => 12, 0x11 => 13, 0x12 => 14, 0x13 => 15, 0x15 => 16, 0x14 => 17,
        0x16 => 32, 0x17 => 34, 0x18 => 31, 0x19 => 35, 0x24 => 38, 0x25 => 40, 0x26 => 37, 0x31 => 45,
        0x32 => 46, // letters
        0x02 => 18, 0x03 => 19, 0x04 => 20, 0x05 => 21, 0x06 => 23, 0x07 => 22, 0x08 => 26, 0x09 => 28,
        0x0A => 25, 0x0B => 29, // digits
        0x0C => 27, 0x0D => 24, 0x1A => 33, 0x1B => 30, 0x2B => 42, 0x27 => 41, 0x28 => 39, 0x29 => 50,
        0x33 => 43, 0x34 => 47, 0x35 => 44, 0x0F => 48, 0x39 => 49, // symbols
        _ => -1,
    };

    /// <summary>The modifier flag a control key contributes while held, or 0 for a non-modifier.</summary>
    public static ulong FlagFor(ControlKey key) => key switch
    {
        ControlKey.Shift or ControlKey.RightShift => FlagShift,
        ControlKey.Control or ControlKey.RightControl => FlagControl,
        ControlKey.Alt or ControlKey.RightAlt => FlagAlternate,
        ControlKey.Meta or ControlKey.RightMeta => FlagCommand,
        _ => 0,
    };
}
