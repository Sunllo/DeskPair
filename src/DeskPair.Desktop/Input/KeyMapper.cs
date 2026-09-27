using Avalonia.Input;
using DeskPair.Protocol.Messages;
using ProtoControlKey = DeskPair.Protocol.Messages.ControlKey;

namespace DeskPair.Desktop.Input;

/// <summary>
/// Turns Avalonia key events into wire key events: positional mode carries a PC (set 1) scancode derived
/// from the physical key, translate mode carries the typed character; control keys always go by name.
/// </summary>
public static class KeyMapper
{
    private static readonly Dictionary<PhysicalKey, uint> Scancodes = new()
    {
        [PhysicalKey.Escape] = 0x01, [PhysicalKey.Digit1] = 0x02, [PhysicalKey.Digit2] = 0x03, [PhysicalKey.Digit3] = 0x04, [PhysicalKey.Digit4] = 0x05,
        [PhysicalKey.Digit5] = 0x06, [PhysicalKey.Digit6] = 0x07, [PhysicalKey.Digit7] = 0x08, [PhysicalKey.Digit8] = 0x09, [PhysicalKey.Digit9] = 0x0A,
        [PhysicalKey.Digit0] = 0x0B, [PhysicalKey.Minus] = 0x0C, [PhysicalKey.Equal] = 0x0D, [PhysicalKey.Backspace] = 0x0E, [PhysicalKey.Tab] = 0x0F,
        [PhysicalKey.Q] = 0x10, [PhysicalKey.W] = 0x11, [PhysicalKey.E] = 0x12, [PhysicalKey.R] = 0x13, [PhysicalKey.T] = 0x14, [PhysicalKey.Y] = 0x15,
        [PhysicalKey.U] = 0x16, [PhysicalKey.I] = 0x17, [PhysicalKey.O] = 0x18, [PhysicalKey.P] = 0x19, [PhysicalKey.BracketLeft] = 0x1A, [PhysicalKey.BracketRight] = 0x1B,
        [PhysicalKey.Enter] = 0x1C, [PhysicalKey.ControlLeft] = 0x1D, [PhysicalKey.A] = 0x1E, [PhysicalKey.S] = 0x1F, [PhysicalKey.D] = 0x20, [PhysicalKey.F] = 0x21,
        [PhysicalKey.G] = 0x22, [PhysicalKey.H] = 0x23, [PhysicalKey.J] = 0x24, [PhysicalKey.K] = 0x25, [PhysicalKey.L] = 0x26, [PhysicalKey.Semicolon] = 0x27,
        [PhysicalKey.Quote] = 0x28, [PhysicalKey.Backquote] = 0x29, [PhysicalKey.ShiftLeft] = 0x2A, [PhysicalKey.Backslash] = 0x2B, [PhysicalKey.Z] = 0x2C,
        [PhysicalKey.X] = 0x2D, [PhysicalKey.C] = 0x2E, [PhysicalKey.V] = 0x2F, [PhysicalKey.B] = 0x30, [PhysicalKey.N] = 0x31, [PhysicalKey.M] = 0x32,
        [PhysicalKey.Comma] = 0x33, [PhysicalKey.Period] = 0x34, [PhysicalKey.Slash] = 0x35, [PhysicalKey.ShiftRight] = 0x36, [PhysicalKey.NumPadMultiply] = 0x37,
        [PhysicalKey.AltLeft] = 0x38, [PhysicalKey.Space] = 0x39, [PhysicalKey.CapsLock] = 0x3A, [PhysicalKey.F1] = 0x3B, [PhysicalKey.F2] = 0x3C,
        [PhysicalKey.F3] = 0x3D, [PhysicalKey.F4] = 0x3E, [PhysicalKey.F5] = 0x3F, [PhysicalKey.F6] = 0x40, [PhysicalKey.F7] = 0x41, [PhysicalKey.F8] = 0x42,
        [PhysicalKey.F9] = 0x43, [PhysicalKey.F10] = 0x44, [PhysicalKey.NumLock] = 0x45, [PhysicalKey.ScrollLock] = 0x46, [PhysicalKey.NumPad7] = 0x47,
        [PhysicalKey.NumPad8] = 0x48, [PhysicalKey.NumPad9] = 0x49, [PhysicalKey.NumPadSubtract] = 0x4A, [PhysicalKey.NumPad4] = 0x4B, [PhysicalKey.NumPad5] = 0x4C,
        [PhysicalKey.NumPad6] = 0x4D, [PhysicalKey.NumPadAdd] = 0x4E, [PhysicalKey.NumPad1] = 0x4F, [PhysicalKey.NumPad2] = 0x50, [PhysicalKey.NumPad3] = 0x51,
        [PhysicalKey.NumPad0] = 0x52, [PhysicalKey.NumPadDecimal] = 0x53, [PhysicalKey.IntlBackslash] = 0x56, [PhysicalKey.F11] = 0x57, [PhysicalKey.F12] = 0x58,
        [PhysicalKey.IntlRo] = 0x73, [PhysicalKey.Convert] = 0x79, [PhysicalKey.NonConvert] = 0x7B, [PhysicalKey.IntlYen] = 0x7D,
        // Extended (0xE0 prefix) keys.
        [PhysicalKey.NumPadEnter] = 0xE01C, [PhysicalKey.ControlRight] = 0xE01D, [PhysicalKey.NumPadDivide] = 0xE035, [PhysicalKey.PrintScreen] = 0xE037,
        [PhysicalKey.AltRight] = 0xE038, [PhysicalKey.Home] = 0xE047, [PhysicalKey.ArrowUp] = 0xE048, [PhysicalKey.PageUp] = 0xE049, [PhysicalKey.ArrowLeft] = 0xE04B,
        [PhysicalKey.ArrowRight] = 0xE04D, [PhysicalKey.End] = 0xE04F, [PhysicalKey.ArrowDown] = 0xE050, [PhysicalKey.PageDown] = 0xE051, [PhysicalKey.Insert] = 0xE052,
        [PhysicalKey.Delete] = 0xE053, [PhysicalKey.MetaLeft] = 0xE05B, [PhysicalKey.MetaRight] = 0xE05C, [PhysicalKey.ContextMenu] = 0xE05D,
        [PhysicalKey.AudioVolumeMute] = 0xE020, [PhysicalKey.AudioVolumeDown] = 0xE02E, [PhysicalKey.AudioVolumeUp] = 0xE030, [PhysicalKey.Pause] = 0xE045,
    };

    private static readonly Dictionary<PhysicalKey, ProtoControlKey> ControlKeys = new()
    {
        [PhysicalKey.AltLeft] = ProtoControlKey.CkAlt, [PhysicalKey.AltRight] = ProtoControlKey.CkRalt,
        [PhysicalKey.Backspace] = ProtoControlKey.CkBackspace, [PhysicalKey.CapsLock] = ProtoControlKey.CkCapsLock,
        [PhysicalKey.ControlLeft] = ProtoControlKey.CkControl, [PhysicalKey.ControlRight] = ProtoControlKey.CkRcontrol,
        [PhysicalKey.Delete] = ProtoControlKey.CkDelete, [PhysicalKey.ArrowDown] = ProtoControlKey.CkDownArrow,
        [PhysicalKey.End] = ProtoControlKey.CkEnd, [PhysicalKey.Escape] = ProtoControlKey.CkEscape,
        [PhysicalKey.F1] = ProtoControlKey.CkF1, [PhysicalKey.F2] = ProtoControlKey.CkF2, [PhysicalKey.F3] = ProtoControlKey.CkF3, [PhysicalKey.F4] = ProtoControlKey.CkF4,
        [PhysicalKey.F5] = ProtoControlKey.CkF5, [PhysicalKey.F6] = ProtoControlKey.CkF6, [PhysicalKey.F7] = ProtoControlKey.CkF7, [PhysicalKey.F8] = ProtoControlKey.CkF8,
        [PhysicalKey.F9] = ProtoControlKey.CkF9, [PhysicalKey.F10] = ProtoControlKey.CkF10, [PhysicalKey.F11] = ProtoControlKey.CkF11, [PhysicalKey.F12] = ProtoControlKey.CkF12,
        [PhysicalKey.Home] = ProtoControlKey.CkHome, [PhysicalKey.ArrowLeft] = ProtoControlKey.CkLeftArrow,
        [PhysicalKey.MetaLeft] = ProtoControlKey.CkMeta, [PhysicalKey.MetaRight] = ProtoControlKey.CkRwin,
        [PhysicalKey.PageDown] = ProtoControlKey.CkPageDown, [PhysicalKey.PageUp] = ProtoControlKey.CkPageUp,
        [PhysicalKey.Enter] = ProtoControlKey.CkReturn, [PhysicalKey.ArrowRight] = ProtoControlKey.CkRightArrow,
        [PhysicalKey.ShiftLeft] = ProtoControlKey.CkShift, [PhysicalKey.ShiftRight] = ProtoControlKey.CkRshift,
        [PhysicalKey.Space] = ProtoControlKey.CkSpace, [PhysicalKey.Tab] = ProtoControlKey.CkTab, [PhysicalKey.ArrowUp] = ProtoControlKey.CkUpArrow,
        [PhysicalKey.NumPad0] = ProtoControlKey.CkNumpad0, [PhysicalKey.NumPad1] = ProtoControlKey.CkNumpad1, [PhysicalKey.NumPad2] = ProtoControlKey.CkNumpad2,
        [PhysicalKey.NumPad3] = ProtoControlKey.CkNumpad3, [PhysicalKey.NumPad4] = ProtoControlKey.CkNumpad4, [PhysicalKey.NumPad5] = ProtoControlKey.CkNumpad5,
        [PhysicalKey.NumPad6] = ProtoControlKey.CkNumpad6, [PhysicalKey.NumPad7] = ProtoControlKey.CkNumpad7, [PhysicalKey.NumPad8] = ProtoControlKey.CkNumpad8,
        [PhysicalKey.NumPad9] = ProtoControlKey.CkNumpad9, [PhysicalKey.NumPadEnter] = ProtoControlKey.CkNumpadEnter,
        [PhysicalKey.NumPadMultiply] = ProtoControlKey.CkMultiply, [PhysicalKey.NumPadAdd] = ProtoControlKey.CkAdd, [PhysicalKey.NumPadSubtract] = ProtoControlKey.CkSubtract,
        [PhysicalKey.NumPadDecimal] = ProtoControlKey.CkDecimal, [PhysicalKey.NumPadDivide] = ProtoControlKey.CkDivide,
        [PhysicalKey.Insert] = ProtoControlKey.CkInsert, [PhysicalKey.Pause] = ProtoControlKey.CkPause, [PhysicalKey.PrintScreen] = ProtoControlKey.CkSnapshot,
        [PhysicalKey.ScrollLock] = ProtoControlKey.CkScroll, [PhysicalKey.NumLock] = ProtoControlKey.CkNumLock, [PhysicalKey.ContextMenu] = ProtoControlKey.CkMenu,
        [PhysicalKey.AudioVolumeMute] = ProtoControlKey.CkVolumeMute, [PhysicalKey.AudioVolumeUp] = ProtoControlKey.CkVolumeUp, [PhysicalKey.AudioVolumeDown] = ProtoControlKey.CkVolumeDown,
    };

    public static bool IsModifier(PhysicalKey key) => key is PhysicalKey.ShiftLeft or PhysicalKey.ShiftRight or PhysicalKey.ControlLeft or PhysicalKey.ControlRight
        or PhysicalKey.AltLeft or PhysicalKey.AltRight or PhysicalKey.MetaLeft or PhysicalKey.MetaRight;

    /// <summary>
    /// Builds the wire event for a key transition. Returns null when the key cannot be represented.
    /// In translate mode, printable keys are sent as Unicode (using the key symbol) unless a non-shift
    /// modifier is held, in which case the positional code keeps shortcuts working.
    /// </summary>
    public static KeyEvent? Map(KeyEventArgs e, bool down, bool translate)
    {
        var wire = new KeyEvent { Down = down, Mode = translate ? KeyboardMode.KmTranslate : KeyboardMode.KmMap };
        AddModifiers(wire, e.KeyModifiers);
        if (ControlKeys.TryGetValue(e.PhysicalKey, out ProtoControlKey control))
        {
            wire.ControlKey = control;
            return wire;
        }

        bool shortcut = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0;
        if (translate && !shortcut && e.KeySymbol is { Length: > 0 } symbol && !char.IsControl(symbol[0]))
        {
            wire.Unicode = (uint)char.ConvertToUtf32(symbol, 0);
            return wire;
        }

        if (Scancodes.TryGetValue(e.PhysicalKey, out uint scan))
        {
            wire.Chr = scan;
            wire.Mode = KeyboardMode.KmMap;
            return wire;
        }

        return null;
    }

    public static KeyEvent Special(ProtoControlKey key, bool down)
        => new() { Down = down, ControlKey = key, Mode = KeyboardMode.KmMap };

    private static void AddModifiers(KeyEvent wire, KeyModifiers modifiers)
    {
        if ((modifiers & KeyModifiers.Shift) != 0)
        {
            wire.Modifiers.Add(ProtoControlKey.CkShift);
        }

        if ((modifiers & KeyModifiers.Control) != 0)
        {
            wire.Modifiers.Add(ProtoControlKey.CkControl);
        }

        if ((modifiers & KeyModifiers.Alt) != 0)
        {
            wire.Modifiers.Add(ProtoControlKey.CkAlt);
        }

        if ((modifiers & KeyModifiers.Meta) != 0)
        {
            wire.Modifiers.Add(ProtoControlKey.CkMeta);
        }
    }
}
