using DeskPair.Platform.Abstractions.Input;

namespace DeskPair.Platform.Linux.Native;

/// <summary>
/// X keysyms (from keysymdef.h) for the named control keys the protocol carries, and for turning a typed
/// character into the keysym XTest needs. The controller sends either a <see cref="ControlKey"/> (a named
/// key, layout-independent) or text (Unicode characters); both become keysyms here, which XKeysymToKeycode
/// then resolves against the live keyboard layout.
///
/// Map mode carries a Windows scancode, which does not translate to an X keycode across operating systems,
/// so this platform relies on the Control and Text paths — which is what a controller on a different OS
/// sends anyway.
/// </summary>
internal static class Keysyms
{
    /// <summary>The keysym for a control key, or 0 when there is none to send.</summary>
    public static nuint ForControl(ControlKey key) => key switch
    {
        ControlKey.Alt => 0xFFE9,          // XK_Alt_L
        ControlKey.Backspace => 0xFF08,
        ControlKey.CapsLock => 0xFFE5,
        ControlKey.Control => 0xFFE3,      // XK_Control_L
        ControlKey.Delete => 0xFFFF,
        ControlKey.DownArrow => 0xFF54,
        ControlKey.End => 0xFF57,
        ControlKey.Return => 0xFF0D,
        ControlKey.Escape => 0xFF1B,
        ControlKey.Home => 0xFF50,
        ControlKey.Insert => 0xFF63,
        ControlKey.LeftArrow => 0xFF51,
        ControlKey.Menu => 0xFF67,         // XK_Menu (context menu)
        ControlKey.Meta => 0xFFEB,         // XK_Super_L
        ControlKey.PageDown => 0xFF56,
        ControlKey.PageUp => 0xFF55,
        ControlKey.Pause => 0xFF13,
        ControlKey.PrintScreen => 0xFF61,
        ControlKey.RightArrow => 0xFF53,
        ControlKey.ScrollLock => 0xFF14,
        ControlKey.Shift => 0xFFE1,        // XK_Shift_L
        ControlKey.Space => 0x0020,
        ControlKey.Tab => 0xFF09,
        ControlKey.UpArrow => 0xFF52,
        ControlKey.NumLock => 0xFF7F,
        ControlKey.RightShift => 0xFFE2,
        ControlKey.RightControl => 0xFFE4,
        ControlKey.RightAlt => 0xFFEA,     // XK_Alt_R
        ControlKey.RightMeta => 0xFFEC,    // XK_Super_R
        ControlKey.F1 => 0xFFBE,
        ControlKey.F2 => 0xFFBF,
        ControlKey.F3 => 0xFFC0,
        ControlKey.F4 => 0xFFC1,
        ControlKey.F5 => 0xFFC2,
        ControlKey.F6 => 0xFFC3,
        ControlKey.F7 => 0xFFC4,
        ControlKey.F8 => 0xFFC5,
        ControlKey.F9 => 0xFFC6,
        ControlKey.F10 => 0xFFC7,
        ControlKey.F11 => 0xFFC8,
        ControlKey.F12 => 0xFFC9,
        ControlKey.Numpad0 => 0xFFB0,
        ControlKey.Numpad1 => 0xFFB1,
        ControlKey.Numpad2 => 0xFFB2,
        ControlKey.Numpad3 => 0xFFB3,
        ControlKey.Numpad4 => 0xFFB4,
        ControlKey.Numpad5 => 0xFFB5,
        ControlKey.Numpad6 => 0xFFB6,
        ControlKey.Numpad7 => 0xFFB7,
        ControlKey.Numpad8 => 0xFFB8,
        ControlKey.Numpad9 => 0xFFB9,
        ControlKey.NumpadEnter => 0xFF8D,
        ControlKey.Multiply => 0xFFAA,
        ControlKey.Add => 0xFFAB,
        ControlKey.Subtract => 0xFFAD,
        ControlKey.Decimal => 0xFFAE,
        ControlKey.Divide => 0xFFAF,
        _ => 0,
    };

    /// <summary>
    /// The keysym for a single character. Latin-1 maps to its own code point (that is how X was defined);
    /// everything else uses the Unicode range 0x01000000 + code point, which modern X servers accept.
    /// </summary>
    public static nuint ForChar(char c) => c <= 0xFF ? c : (nuint)(0x01000000 + c);
}
