using DeskPair.Platform.Abstractions.Input;

namespace DeskPair.Platform.Linux.Native;

/// <summary>
/// Key codes as the kernel names them, for the uinput keyboard.
///
/// The protocol's Map-mode code already <em>is</em> an evdev code: <c>X11InputInjector</c> adds eight to
/// it to make an X keycode, because that is how X numbers them. Through uinput there is nothing to add,
/// which makes this the one input path with no translation table for ordinary keys. The tables here are
/// for the other two kinds of key the protocol sends: a named control key, and a character.
///
/// Characters are a US-layout table, because a uinput device has no keysym layer and no way to ask what
/// layout the compositor will apply to it. That is an honest limitation with a sharp edge: a symbol in a
/// password typed under a non-US layout comes out as a different symbol, and a password field does not
/// echo. The verification matrix types into the greeter's username field first for exactly that reason.
/// </summary>
internal static class Evdev
{
    public const ushort KeyEsc = 1;
    public const ushort KeyBackspace = 14;
    public const ushort KeyTab = 15;
    public const ushort KeyEnter = 28;
    public const ushort KeyLeftCtrl = 29;
    public const ushort KeyLeftShift = 42;
    public const ushort KeyRightShift = 54;
    public const ushort KeyKpAsterisk = 55;
    public const ushort KeyLeftAlt = 56;
    public const ushort KeySpace = 57;
    public const ushort KeyCapsLock = 58;
    public const ushort KeyNumLock = 69;
    public const ushort KeyScrollLock = 70;
    public const ushort KeyKpMinus = 74;
    public const ushort KeyKpPlus = 78;
    public const ushort KeyKpDot = 83;
    public const ushort KeyF11 = 87;
    public const ushort KeyF12 = 88;
    public const ushort KeyKpEnter = 96;
    public const ushort KeyRightCtrl = 97;
    public const ushort KeyKpSlash = 98;
    public const ushort KeySysRq = 99;
    public const ushort KeyRightAlt = 100;
    public const ushort KeyHome = 102;
    public const ushort KeyUp = 103;
    public const ushort KeyPageUp = 104;
    public const ushort KeyLeft = 105;
    public const ushort KeyRight = 106;
    public const ushort KeyEnd = 107;
    public const ushort KeyDown = 108;
    public const ushort KeyPageDown = 109;
    public const ushort KeyInsert = 110;
    public const ushort KeyDelete = 111;
    public const ushort KeyMute = 113;
    public const ushort KeyVolumeDown = 114;
    public const ushort KeyVolumeUp = 115;
    public const ushort KeyPause = 119;
    public const ushort KeyLeftMeta = 125;
    public const ushort KeyRightMeta = 126;
    public const ushort KeyCompose = 127;

    /// <summary>The highest code a keyboard has to advertise for everything here to be deliverable.</summary>
    public const ushort MaxKey = 0xFF;

    /// <summary>The evdev code for a named key, or 0 for one this path does not send.</summary>
    public static ushort ForControl(ControlKey key) => key switch
    {
        ControlKey.Alt => KeyLeftAlt,
        ControlKey.Backspace => KeyBackspace,
        ControlKey.CapsLock => KeyCapsLock,
        ControlKey.Control => KeyLeftCtrl,
        ControlKey.Delete => KeyDelete,
        ControlKey.DownArrow => KeyDown,
        ControlKey.End => KeyEnd,
        ControlKey.Escape => KeyEsc,
        ControlKey.F1 => 59,
        ControlKey.F2 => 60,
        ControlKey.F3 => 61,
        ControlKey.F4 => 62,
        ControlKey.F5 => 63,
        ControlKey.F6 => 64,
        ControlKey.F7 => 65,
        ControlKey.F8 => 66,
        ControlKey.F9 => 67,
        ControlKey.F10 => 68,
        ControlKey.F11 => KeyF11,
        ControlKey.F12 => KeyF12,
        ControlKey.Home => KeyHome,
        ControlKey.LeftArrow => KeyLeft,
        ControlKey.Meta => KeyLeftMeta,
        ControlKey.PageDown => KeyPageDown,
        ControlKey.PageUp => KeyPageUp,
        ControlKey.Return => KeyEnter,
        ControlKey.RightArrow => KeyRight,
        ControlKey.Shift => KeyLeftShift,
        ControlKey.Space => KeySpace,
        ControlKey.Tab => KeyTab,
        ControlKey.UpArrow => KeyUp,
        ControlKey.Numpad0 => 82,
        ControlKey.Numpad1 => 79,
        ControlKey.Numpad2 => 80,
        ControlKey.Numpad3 => 81,
        ControlKey.Numpad4 => 75,
        ControlKey.Numpad5 => 76,
        ControlKey.Numpad6 => 77,
        ControlKey.Numpad7 => 71,
        ControlKey.Numpad8 => 72,
        ControlKey.Numpad9 => 73,
        ControlKey.NumpadEnter => KeyKpEnter,
        ControlKey.Multiply => KeyKpAsterisk,
        ControlKey.Add => KeyKpPlus,
        ControlKey.Subtract => KeyKpMinus,
        ControlKey.Decimal => KeyKpDot,
        ControlKey.Divide => KeyKpSlash,
        ControlKey.Insert => KeyInsert,
        ControlKey.Pause => KeyPause,
        ControlKey.PrintScreen => KeySysRq,
        ControlKey.ScrollLock => KeyScrollLock,
        ControlKey.NumLock => KeyNumLock,
        ControlKey.Menu => KeyCompose,
        ControlKey.RightShift => KeyRightShift,
        ControlKey.RightControl => KeyRightCtrl,
        ControlKey.RightAlt => KeyRightAlt,
        ControlKey.RightMeta => KeyRightMeta,
        ControlKey.VolumeMute => KeyMute,
        ControlKey.VolumeUp => KeyVolumeUp,
        ControlKey.VolumeDown => KeyVolumeDown,
        _ => 0,
    };

    // The US layout, row by row: the unshifted character, then what Shift makes of the same key.
    private const string Row1 = "`1234567890-=";
    private const string Row1Shift = "~!@#$%^&*()_+";
    private const string Row2 = "qwertyuiop[]\\";
    private const string Row2Shift = "QWERTYUIOP{}|";
    private const string Row3 = "asdfghjkl;'";
    private const string Row3Shift = "ASDFGHJKL:\"";
    private const string Row4 = "zxcvbnm,./";
    private const string Row4Shift = "ZXCVBNM<>?";

    private static readonly ushort[] Row1Codes = [41, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13];
    private static readonly ushort[] Row2Codes = [16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 43];
    private static readonly ushort[] Row3Codes = [30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40];
    private static readonly ushort[] Row4Codes = [44, 45, 46, 47, 48, 49, 50, 51, 52, 53];

    /// <summary>The key, and whether Shift is held, that types <paramref name="c"/> on a US keyboard; null otherwise.</summary>
    public static (ushort Code, bool Shift)? ForChar(char c)
    {
        switch (c)
        {
            case ' ': return (KeySpace, false);
            case '\t': return (KeyTab, false);
            case '\n' or '\r': return (KeyEnter, false);
        }

        return Find(Row1, Row1Shift, Row1Codes, c)
            ?? Find(Row2, Row2Shift, Row2Codes, c)
            ?? Find(Row3, Row3Shift, Row3Codes, c)
            ?? Find(Row4, Row4Shift, Row4Codes, c);
    }

    private static (ushort, bool)? Find(string plain, string shifted, ushort[] codes, char c)
    {
        int i = plain.IndexOf(c);
        if (i >= 0)
        {
            return (codes[i], false);
        }

        i = shifted.IndexOf(c);
        return i >= 0 ? (codes[i], true) : null;
    }
}
