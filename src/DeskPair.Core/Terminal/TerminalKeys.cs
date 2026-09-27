using System.Text;

namespace DeskPair.Core.Terminal;

/// <summary>The keys that are not text. Letters, digits and punctuation go through <see cref="TerminalKeys.Text"/>.</summary>
public enum TerminalKey
{
    Enter,
    Tab,
    Backspace,
    Escape,
    Up,
    Down,
    Right,
    Left,
    Home,
    End,
    PageUp,
    PageDown,
    Insert,
    Delete,
    F1,
    F2,
    F3,
    F4,
    F5,
    F6,
    F7,
    F8,
    F9,
    F10,
    F11,
    F12,
}

[Flags]
public enum TerminalModifiers
{
    None = 0,
    Shift = 1,
    Alt = 2,
    Control = 4,
}

/// <summary>
/// What a key press becomes on the wire, as xterm sends it. Kept here rather than in a view so the
/// desktop window and both phones type the same bytes -- the plan's rule for everything that would
/// otherwise drift between three front ends.
/// </summary>
public static class TerminalKeys
{
    /// <summary>A special key, with modifiers encoded the xterm way (<c>ESC [1;5A</c> for Ctrl+Up).</summary>
    public static byte[] Key(TerminalKey key, TerminalModifiers modifiers, bool applicationCursorKeys)
    {
        // xterm's modifier parameter: 1 + Shift(1) + Alt(2) + Ctrl(4).
        int mod = 1 + (modifiers.HasFlag(TerminalModifiers.Shift) ? 1 : 0) + (modifiers.HasFlag(TerminalModifiers.Alt) ? 2 : 0) + (modifiers.HasFlag(TerminalModifiers.Control) ? 4 : 0);
        string sequence = key switch
        {
            TerminalKey.Enter => modifiers.HasFlag(TerminalModifiers.Alt) ? "\x1b\r" : "\r",
            TerminalKey.Tab => modifiers.HasFlag(TerminalModifiers.Shift) ? "\x1b[Z" : "\t",
            TerminalKey.Backspace => modifiers.HasFlag(TerminalModifiers.Alt) ? "\x1b\x7f" : modifiers.HasFlag(TerminalModifiers.Control) ? "\x08" : "\x7f",
            TerminalKey.Escape => "\x1b",
            TerminalKey.Up => Cursor('A'),
            TerminalKey.Down => Cursor('B'),
            TerminalKey.Right => Cursor('C'),
            TerminalKey.Left => Cursor('D'),
            TerminalKey.Home => Cursor('H'),
            TerminalKey.End => Cursor('F'),
            TerminalKey.Insert => Tilde(2),
            TerminalKey.Delete => Tilde(3),
            TerminalKey.PageUp => Tilde(5),
            TerminalKey.PageDown => Tilde(6),
            TerminalKey.F1 => Ss3('P'),
            TerminalKey.F2 => Ss3('Q'),
            TerminalKey.F3 => Ss3('R'),
            TerminalKey.F4 => Ss3('S'),
            TerminalKey.F5 => Tilde(15),
            TerminalKey.F6 => Tilde(17),
            TerminalKey.F7 => Tilde(18),
            TerminalKey.F8 => Tilde(19),
            TerminalKey.F9 => Tilde(20),
            TerminalKey.F10 => Tilde(21),
            TerminalKey.F11 => Tilde(23),
            TerminalKey.F12 => Tilde(24),
            _ => string.Empty,
        };
        return Encoding.ASCII.GetBytes(sequence);

        string Cursor(char final) => mod > 1 ? $"\x1b[1;{mod}{final}" : applicationCursorKeys ? $"\x1bO{final}" : $"\x1b[{final}";

        string Ss3(char final) => mod > 1 ? $"\x1b[1;{mod}{final}" : $"\x1bO{final}";

        string Tilde(int number) => mod > 1 ? $"\x1b[{number};{mod}~" : $"\x1b[{number}~";
    }

    /// <summary>
    /// Text typed with modifiers. Ctrl+letter is the C0 control (Ctrl+C is 0x03, which is SIGINT at the
    /// other end), Ctrl+Space and Ctrl+@ are NUL, Ctrl+[ \ ] ^ _ are 0x1B..0x1F; Alt prefixes ESC, the
    /// "meta sends escape" convention every shell's line editor reads.
    /// </summary>
    public static byte[] Text(string text, TerminalModifiers modifiers)
    {
        byte[] bytes;
        if (modifiers.HasFlag(TerminalModifiers.Control) && text.Length == 1 && Control(text[0]) is { } control)
        {
            bytes = [control];
        }
        else
        {
            bytes = Encoding.UTF8.GetBytes(text);
        }

        return modifiers.HasFlag(TerminalModifiers.Alt) ? [0x1B, .. bytes] : bytes;
    }

    /// <summary>
    /// A paste. Line endings become CR, as a typed Enter would be. In bracketed-paste mode the text is
    /// framed, and any end marker inside it is removed first: otherwise pasted text could end the frame
    /// early and have the rest run as if typed.
    /// </summary>
    public static byte[] Paste(string text, bool bracketed)
    {
        string body = text.Replace("\r\n", "\r", StringComparison.Ordinal).Replace('\n', '\r');
        if (!bracketed)
        {
            return Encoding.UTF8.GetBytes(body);
        }

        body = body.Replace("\x1b[201~", string.Empty, StringComparison.Ordinal);
        return Encoding.UTF8.GetBytes("\x1b[200~" + body + "\x1b[201~");
    }

    /// <summary>Whether a paste should be confirmed first: more than one line runs more than one command.</summary>
    public static bool IsMultiLine(string text) => text.TrimEnd('\r', '\n').IndexOfAny(['\r', '\n']) >= 0;

    private static byte? Control(char c) => c switch
    {
        >= 'a' and <= 'z' => (byte)(c - 'a' + 1),
        >= 'A' and <= 'Z' => (byte)(c - 'A' + 1),
        ' ' or '@' or '2' => 0,
        '[' or '3' => 0x1B,
        '\\' or '4' => 0x1C,
        ']' or '5' => 0x1D,
        '^' or '6' => 0x1E,
        '_' or '7' or '/' => 0x1F,
        '8' or '?' => 0x7F,
        _ => null,
    };
}
