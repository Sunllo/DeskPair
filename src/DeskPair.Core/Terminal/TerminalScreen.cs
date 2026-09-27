using System.Text;
using Wcwidth;

namespace DeskPair.Core.Terminal;

/// <summary>
/// The terminal's picture: a grid of <see cref="TerminalCell"/>, a cursor, the modes that change what the
/// next byte means, and the lines that scrolled off the top. <see cref="Feed(ReadOnlySpan{byte})"/> takes the shell's bytes;
/// the control reads <see cref="Row"/> and redraws the rows <see cref="TakeDirtyRows"/> names. It knows
/// nothing about fonts, pixels or the network, which is what lets the same behaviour be pinned by the
/// shared vectors in <c>tests/fixtures/vt</c> on every platform.
///
/// Three places where a hand-written terminal usually goes wrong, and what this one does:
/// the right margin -- a character printed in the last column leaves the cursor there with a pending wrap,
/// and only the next printable character moves to the next line; a wide character that would not fit in
/// the last column wraps first; and the alternate screen has its own grid and no scrollback, so leaving
/// vim puts the shell's screen back exactly as it was.
/// </summary>
public sealed class TerminalScreen : IVtHandler
{
    /// <summary>Lines kept above the main screen; a bound, so a runaway loop costs a fixed amount of memory.</summary>
    public const int ScrollbackLines = 10_000;

    private const int MaxDimension = 1000;

    private readonly VtParser _parser;
    private readonly List<TerminalCell[]> _scrollback = [];
    private readonly List<byte[]> _replies = [];
    private TerminalCell[][] _primary;
    private TerminalCell[][] _alternate;
    private TerminalCell[][] _grid;
    private bool[] _dirty;
    private bool[] _tabStops;
    private int _x;
    private int _y;
    private bool _wrapPending;
    private CellColors _pen = CellColors.Plain;
    private int _top;
    private int _bottom;
    private bool _originMode;
    private bool _g0Graphics;
    private bool _g1Graphics;
    private bool _shiftOut;
    private Saved _savedPrimary;
    private Saved _savedAlternate;

    private readonly record struct Saved(int X, int Y, CellColors Pen, bool OriginMode, bool G0Graphics, bool G1Graphics, bool ShiftOut, bool WrapPending);

    public TerminalScreen(int columns, int rows)
    {
        Columns = Math.Clamp(columns, 1, MaxDimension);
        Rows = Math.Clamp(rows, 1, MaxDimension);
        _primary = Blank(Columns, Rows);
        _alternate = Blank(Columns, Rows);
        _grid = _primary;
        _dirty = new bool[Rows];
        _tabStops = TabStops(Columns);
        _bottom = Rows - 1;
        _parser = new VtParser(this);
        MarkAll();
    }

    public int Columns { get; private set; }

    public int Rows { get; private set; }

    public int CursorColumn => _x;

    public int CursorRow => _y;

    public bool CursorVisible { get; private set; } = true;

    /// <summary>DECAWM.</summary>
    public bool AutoWrap { get; private set; } = true;

    /// <summary>IRM: printing shifts the rest of the line right instead of overwriting.</summary>
    public bool InsertMode { get; private set; }

    /// <summary>DECCKM: the arrow keys send <c>ESC O</c> rather than <c>ESC [</c>.</summary>
    public bool ApplicationCursorKeys { get; private set; }

    /// <summary>DECKPAM, recorded for the key encoder.</summary>
    public bool ApplicationKeypad { get; private set; }

    /// <summary>Mode 2004: a paste is framed by <c>ESC [200~</c> and <c>ESC [201~</c>.</summary>
    public bool BracketedPaste { get; private set; }

    /// <summary>LNM: a line feed also returns the carriage.</summary>
    public bool NewLineMode { get; private set; }

    public bool OnAlternateScreen => ReferenceEquals(_grid, _alternate);

    /// <summary>What OSC 0 or 2 last set; the window uses it as its title.</summary>
    public string Title { get; private set; } = string.Empty;

    public IReadOnlyList<TerminalCell[]> Scrollback => _scrollback;

    /// <summary>Raised on BEL.</summary>
    public event Action? Bell;

    /// <summary>Raised when <see cref="Title"/> changes.</summary>
    public event Action<string>? TitleChanged;

    public void Feed(ReadOnlySpan<byte> bytes) => _parser.Feed(bytes);

    public void Feed(string text) => Feed(Encoding.UTF8.GetBytes(text));

    public TerminalCell[] Row(int row) => _grid[row];

    /// <summary>A visible row as text, trailing blanks trimmed; the vectors compare against this.</summary>
    public string RowText(int row)
    {
        var sb = new StringBuilder(Columns);
        foreach (TerminalCell cell in _grid[row])
        {
            sb.Append(cell.Text);
        }

        return sb.ToString().TrimEnd(' ');
    }

    /// <summary>
    /// Bytes the terminal owes the shell -- answers to "where is the cursor" and "what are you" --
    /// taken once. The session sends them back as input.
    /// </summary>
    public IReadOnlyList<byte[]> TakeReplies()
    {
        if (_replies.Count == 0)
        {
            return [];
        }

        byte[][] taken = [.. _replies];
        _replies.Clear();
        return taken;
    }

    /// <summary>The rows changed since the last call, so the control redraws only those.</summary>
    public IReadOnlyList<int> TakeDirtyRows()
    {
        var rows = new List<int>();
        for (int i = 0; i < _dirty.Length; i++)
        {
            if (_dirty[i])
            {
                rows.Add(i);
                _dirty[i] = false;
            }
        }

        return rows;
    }

    /// <summary>
    /// A new size. No reflow: rows are cut or padded on the right. When the screen gets shorter, the rows
    /// above the cursor go to the scrollback so the line being typed on stays in view, which is what
    /// makes a window that is dragged smaller and back again leave the prompt where it was.
    /// </summary>
    public void Resize(int columns, int rows)
    {
        columns = Math.Clamp(columns, 1, MaxDimension);
        rows = Math.Clamp(rows, 1, MaxDimension);
        if (columns == Columns && rows == Rows)
        {
            return;
        }

        bool alternate = OnAlternateScreen;
        int shift = Math.Max(0, _y - (rows - 1));
        if (!alternate)
        {
            for (int i = 0; i < shift; i++)
            {
                PushScrollback(_primary[i]);
            }
        }

        _primary = Refit(_primary, columns, rows, alternate ? 0 : shift);
        _alternate = Refit(_alternate, columns, rows, alternate ? shift : 0);
        _grid = alternate ? _alternate : _primary;
        Columns = columns;
        Rows = rows;
        _dirty = new bool[rows];
        _tabStops = TabStops(columns);
        _top = 0;
        _bottom = rows - 1;
        _y = Math.Clamp(_y - shift, 0, rows - 1);
        _x = Math.Min(_x, columns - 1);
        _wrapPending = false;
        MarkAll();
    }

    // ---- IVtHandler ----

    void IVtHandler.Print(Rune rune)
    {
        int code = rune.Value;
        if (_shiftOut ? _g1Graphics : _g0Graphics)
        {
            code = DecGraphics(code);
        }

        int width = code < 0x7F ? 1 : UnicodeCalculator.GetWidth(new Rune(code));
        if (width <= 0)
        {
            // Combining marks and zero-width characters: v1 does not compose them onto the previous cell.
            return;
        }

        if (_wrapPending || (width == 2 && _x == Columns - 1 && AutoWrap))
        {
            if (AutoWrap)
            {
                _x = 0;
                LineFeed();
            }

            _wrapPending = false;
        }

        if (width == 2 && _x == Columns - 1)
        {
            // No room and no wrap: xterm draws nothing rather than half a character.
            return;
        }

        TerminalCell[] line = _grid[_y];
        if (InsertMode)
        {
            Array.Copy(line, _x, line, _x + width, Columns - _x - width);
        }

        ClearWideAt(line, _x);
        line[_x] = new TerminalCell(code, _pen, (byte)width);
        if (width == 2)
        {
            ClearWideAt(line, _x + 1);
            line[_x + 1] = new TerminalCell(0, _pen, 0);
        }

        _dirty[_y] = true;
        if (_x + width >= Columns)
        {
            _x = Columns - 1;
            _wrapPending = AutoWrap;
        }
        else
        {
            _x += width;
        }
    }

    void IVtHandler.Execute(byte control)
    {
        switch (control)
        {
            case 0x07:
                Bell?.Invoke();
                break;
            case 0x08:
                if (_x > 0)
                {
                    _x--;
                }

                _wrapPending = false;
                break;
            case 0x09:
                _x = NextTab(_x);
                _wrapPending = false;
                break;
            case 0x0A or 0x0B or 0x0C:
                LineFeed();
                if (NewLineMode)
                {
                    _x = 0;
                }

                _wrapPending = false;
                break;
            case 0x0D:
                _x = 0;
                _wrapPending = false;
                break;
            case 0x0E:
                _shiftOut = true;
                break;
            case 0x0F:
                _shiftOut = false;
                break;
        }
    }

    void IVtHandler.EscDispatch(ReadOnlySpan<byte> intermediates, byte final)
    {
        if (intermediates.Length == 1 && intermediates[0] is (byte)'(' or (byte)')')
        {
            bool graphics = final == (byte)'0';
            if (intermediates[0] == (byte)'(')
            {
                _g0Graphics = graphics;
            }
            else
            {
                _g1Graphics = graphics;
            }

            return;
        }

        if (intermediates.Length == 1 && intermediates[0] == (byte)'#' && final == (byte)'8')
        {
            // DECALN: fill with E, the alignment pattern.
            foreach (TerminalCell[] line in _grid)
            {
                Array.Fill(line, new TerminalCell('E', CellColors.Plain, 1));
            }

            MarkAll();
            return;
        }

        if (intermediates.Length != 0)
        {
            return;
        }

        switch (final)
        {
            case (byte)'7':
                Save();
                break;
            case (byte)'8':
                Restore();
                break;
            case (byte)'D':
                LineFeed();
                break;
            case (byte)'E':
                _x = 0;
                LineFeed();
                break;
            case (byte)'M':
                ReverseLineFeed();
                break;
            case (byte)'H':
                _tabStops[_x] = true;
                break;
            case (byte)'=':
                ApplicationKeypad = true;
                break;
            case (byte)'>':
                ApplicationKeypad = false;
                break;
            case (byte)'c':
                Reset();
                break;
        }

        _wrapPending = false;
    }

    void IVtHandler.CsiDispatch(ReadOnlySpan<int> p, ReadOnlySpan<byte> intermediates, byte final)
    {
        byte marker = intermediates.Length > 0 ? intermediates[0] : (byte)0;
        if (marker == (byte)'?')
        {
            if (final is (byte)'h' or (byte)'l')
            {
                foreach (int mode in p)
                {
                    SetPrivateMode(mode, final == (byte)'h');
                }
            }

            return;
        }

        if (marker == (byte)'>')
        {
            if (final == (byte)'c')
            {
                Reply("\x1b[>0;10;1c"); // secondary DA: a VT100-class terminal, nothing more claimed
            }

            return;
        }

        if (marker != 0)
        {
            return; // intermediates such as ' ' in DECSCUSR: cursor shape, not drawn differently in v1
        }

        int n = Param(p, 0, 1);
        switch (final)
        {
            case (byte)'A':
                MoveTo(_x, Math.Max(_y - n, _y >= _top ? _top : 0));
                break;
            case (byte)'B' or (byte)'e':
                MoveTo(_x, Math.Min(_y + n, _y <= _bottom ? _bottom : Rows - 1));
                break;
            case (byte)'C' or (byte)'a':
                MoveTo(_x + n, _y);
                break;
            case (byte)'D':
                MoveTo(_x - n, _y);
                break;
            case (byte)'E':
                MoveTo(0, Math.Min(_y + n, _bottom));
                break;
            case (byte)'F':
                MoveTo(0, Math.Max(_y - n, _top));
                break;
            case (byte)'G' or (byte)'`':
                MoveTo(n - 1, _y);
                break;
            case (byte)'d':
                MoveTo(_x, (_originMode ? _top : 0) + n - 1);
                break;
            case (byte)'H' or (byte)'f':
                MoveTo(Param(p, 1, 1) - 1, (_originMode ? _top : 0) + Param(p, 0, 1) - 1);
                break;
            case (byte)'J':
                EraseDisplay(Param(p, 0, 0));
                break;
            case (byte)'K':
                EraseLine(Param(p, 0, 0));
                break;
            case (byte)'L':
                InsertLines(n);
                break;
            case (byte)'M':
                DeleteLines(n);
                break;
            case (byte)'@':
                InsertChars(n);
                break;
            case (byte)'P':
                DeleteChars(n);
                break;
            case (byte)'X':
                EraseChars(n);
                break;
            case (byte)'S':
                for (int i = 0; i < n; i++)
                {
                    ScrollUp(_top, _bottom);
                }

                break;
            case (byte)'T':
                for (int i = 0; i < n; i++)
                {
                    ScrollDown(_top, _bottom);
                }

                break;
            case (byte)'g':
                if (Param(p, 0, 0) == 3)
                {
                    Array.Fill(_tabStops, false);
                }
                else if (Param(p, 0, 0) == 0)
                {
                    _tabStops[_x] = false;
                }

                break;
            case (byte)'r':
                SetScrollRegion(Param(p, 0, 1) - 1, Param(p, 1, Rows) - 1);
                break;
            case (byte)'m':
                SelectGraphicRendition(p);
                break;
            case (byte)'h' or (byte)'l':
                foreach (int mode in p)
                {
                    if (mode == 4)
                    {
                        InsertMode = final == (byte)'h';
                    }
                    else if (mode == 20)
                    {
                        NewLineMode = final == (byte)'h';
                    }
                }

                break;
            case (byte)'n':
                if (Param(p, 0, 0) == 5)
                {
                    Reply("\x1b[0n");
                }
                else if (Param(p, 0, 0) == 6)
                {
                    int row = _originMode ? _y - _top : _y;
                    Reply($"\x1b[{row + 1};{_x + 1}R");
                }

                break;
            case (byte)'c':
                if (Param(p, 0, 0) == 0)
                {
                    Reply("\x1b[?1;2c"); // primary DA: VT100 with advanced video
                }

                break;
            case (byte)'s':
                Save();
                break;
            case (byte)'u':
                Restore();
                break;
        }
    }

    void IVtHandler.OscDispatch(ReadOnlySpan<byte> data)
    {
        int semi = data.IndexOf((byte)';');
        if (semi <= 0)
        {
            return;
        }

        string command = Encoding.ASCII.GetString(data[..semi]);
        if (command is "0" or "2")
        {
            string title = Encoding.UTF8.GetString(data[(semi + 1)..]);
            if (title != Title)
            {
                Title = title;
                TitleChanged?.Invoke(title);
            }
        }
    }

    // ---- behaviour ----

    private void SetPrivateMode(int mode, bool on)
    {
        switch (mode)
        {
            case 1:
                ApplicationCursorKeys = on;
                break;
            case 6:
                _originMode = on;
                MoveTo(0, on ? _top : 0);
                break;
            case 7:
                AutoWrap = on;
                if (!on)
                {
                    _wrapPending = false;
                }

                break;
            case 25:
                CursorVisible = on;
                break;
            case 47 or 1047:
                SwitchScreen(on, clearOnEnter: mode == 1047, saveCursor: false);
                break;
            case 1048:
                if (on)
                {
                    Save();
                }
                else
                {
                    Restore();
                }

                break;
            case 1049:
                SwitchScreen(on, clearOnEnter: true, saveCursor: true);
                break;
            case 2004:
                BracketedPaste = on;
                break;
        }
    }

    private void SwitchScreen(bool alternate, bool clearOnEnter, bool saveCursor)
    {
        if (alternate == OnAlternateScreen)
        {
            return;
        }

        if (alternate)
        {
            if (saveCursor)
            {
                Save();
            }

            _grid = _alternate;
            if (clearOnEnter)
            {
                foreach (TerminalCell[] line in _alternate)
                {
                    Array.Fill(line, TerminalCell.Space(_pen));
                }
            }
        }
        else
        {
            _grid = _primary;
            if (saveCursor)
            {
                Restore();
            }
        }

        MarkAll();
    }

    private void Save()
    {
        var saved = new Saved(_x, _y, _pen, _originMode, _g0Graphics, _g1Graphics, _shiftOut, _wrapPending);
        if (OnAlternateScreen)
        {
            _savedAlternate = saved;
        }
        else
        {
            _savedPrimary = saved;
        }
    }

    private void Restore()
    {
        Saved s = OnAlternateScreen ? _savedAlternate : _savedPrimary;
        _x = Math.Min(s.X, Columns - 1);
        _y = Math.Min(s.Y, Rows - 1);
        _pen = s.Pen;
        _originMode = s.OriginMode;
        _g0Graphics = s.G0Graphics;
        _g1Graphics = s.G1Graphics;
        _shiftOut = s.ShiftOut;
        _wrapPending = s.WrapPending;
    }

    private void Reset()
    {
        _primary = Blank(Columns, Rows);
        _alternate = Blank(Columns, Rows);
        _grid = _primary;
        _scrollback.Clear();
        _tabStops = TabStops(Columns);
        _x = 0;
        _y = 0;
        _wrapPending = false;
        _pen = CellColors.Plain;
        _top = 0;
        _bottom = Rows - 1;
        _originMode = false;
        _g0Graphics = _g1Graphics = _shiftOut = false;
        _savedPrimary = _savedAlternate = default;
        AutoWrap = true;
        CursorVisible = true;
        InsertMode = ApplicationCursorKeys = ApplicationKeypad = BracketedPaste = NewLineMode = false;
        MarkAll();
    }

    private void MoveTo(int x, int y)
    {
        int minY = _originMode ? _top : 0;
        int maxY = _originMode ? _bottom : Rows - 1;
        _x = Math.Clamp(x, 0, Columns - 1);
        _y = Math.Clamp(y, minY, maxY);
        _wrapPending = false;
    }

    private void LineFeed()
    {
        if (_y == _bottom)
        {
            ScrollUp(_top, _bottom);
        }
        else if (_y < Rows - 1)
        {
            _y++;
        }
    }

    private void ReverseLineFeed()
    {
        if (_y == _top)
        {
            ScrollDown(_top, _bottom);
        }
        else if (_y > 0)
        {
            _y--;
        }
    }

    /// <summary>The region's top line leaves; only a full-screen scroll of the main screen keeps it.</summary>
    private void ScrollUp(int top, int bottom)
    {
        TerminalCell[] leaving = _grid[top];
        if (top == 0 && bottom == Rows - 1 && !OnAlternateScreen)
        {
            PushScrollback(leaving);
            leaving = new TerminalCell[Columns];
        }

        Array.Copy(_grid, top + 1, _grid, top, bottom - top);
        Array.Fill(leaving, TerminalCell.Space(_pen));
        _grid[bottom] = leaving;
        Mark(top, bottom);
    }

    private void ScrollDown(int top, int bottom)
    {
        TerminalCell[] leaving = _grid[bottom];
        Array.Copy(_grid, top, _grid, top + 1, bottom - top);
        Array.Fill(leaving, TerminalCell.Space(_pen));
        _grid[top] = leaving;
        Mark(top, bottom);
    }

    private void PushScrollback(TerminalCell[] line)
    {
        _scrollback.Add(line);
        if (_scrollback.Count > ScrollbackLines)
        {
            _scrollback.RemoveRange(0, _scrollback.Count - ScrollbackLines);
        }
    }

    private void SetScrollRegion(int top, int bottom)
    {
        bottom = Math.Min(bottom, Rows - 1);
        if (top < 0 || top >= bottom)
        {
            return;
        }

        _top = top;
        _bottom = bottom;
        MoveTo(0, _originMode ? _top : 0);
    }

    private void InsertLines(int n)
    {
        if (_y < _top || _y > _bottom)
        {
            return;
        }

        for (int i = 0; i < Math.Min(n, _bottom - _y + 1); i++)
        {
            ScrollDown(_y, _bottom);
        }

        _x = 0;
        _wrapPending = false;
    }

    private void DeleteLines(int n)
    {
        if (_y < _top || _y > _bottom)
        {
            return;
        }

        for (int i = 0; i < Math.Min(n, _bottom - _y + 1); i++)
        {
            TerminalCell[] leaving = _grid[_y];
            Array.Copy(_grid, _y + 1, _grid, _y, _bottom - _y);
            Array.Fill(leaving, TerminalCell.Space(_pen));
            _grid[_bottom] = leaving;
        }

        Mark(_y, _bottom);
        _x = 0;
        _wrapPending = false;
    }

    private void InsertChars(int n)
    {
        TerminalCell[] line = _grid[_y];
        n = Math.Min(n, Columns - _x);
        Array.Copy(line, _x, line, _x + n, Columns - _x - n);
        Array.Fill(line, TerminalCell.Space(_pen), _x, n);
        _dirty[_y] = true;
        _wrapPending = false;
    }

    private void DeleteChars(int n)
    {
        TerminalCell[] line = _grid[_y];
        n = Math.Min(n, Columns - _x);
        Array.Copy(line, _x + n, line, _x, Columns - _x - n);
        Array.Fill(line, TerminalCell.Space(_pen), Columns - n, n);
        _dirty[_y] = true;
        _wrapPending = false;
    }

    private void EraseChars(int n)
    {
        TerminalCell[] line = _grid[_y];
        Array.Fill(line, TerminalCell.Space(_pen), _x, Math.Min(n, Columns - _x));
        _dirty[_y] = true;
        _wrapPending = false;
    }

    private void EraseLine(int mode)
    {
        TerminalCell[] line = _grid[_y];
        (int from, int to) = mode switch
        {
            1 => (0, _x + 1),
            2 => (0, Columns),
            _ => (_x, Columns),
        };
        Array.Fill(line, TerminalCell.Space(_pen), from, to - from);
        _dirty[_y] = true;
        _wrapPending = false;
    }

    private void EraseDisplay(int mode)
    {
        switch (mode)
        {
            case 0:
                EraseLine(0);
                for (int r = _y + 1; r < Rows; r++)
                {
                    Array.Fill(_grid[r], TerminalCell.Space(_pen));
                }

                Mark(_y, Rows - 1);
                break;
            case 1:
                EraseLine(1);
                for (int r = 0; r < _y; r++)
                {
                    Array.Fill(_grid[r], TerminalCell.Space(_pen));
                }

                Mark(0, _y);
                break;
            case 2:
                foreach (TerminalCell[] line in _grid)
                {
                    Array.Fill(line, TerminalCell.Space(_pen));
                }

                MarkAll();
                break;
            case 3:
                _scrollback.Clear();
                break;
        }

        _wrapPending = false;
    }

    private void SelectGraphicRendition(ReadOnlySpan<int> p)
    {
        if (p.Length == 0)
        {
            _pen = CellColors.Plain;
            return;
        }

        uint fg = _pen.Foreground;
        uint bg = _pen.Background;
        CellAttributes a = _pen.Attributes;
        for (int i = 0; i < p.Length; i++)
        {
            int code = p[i];
            switch (code)
            {
                case 0:
                    fg = bg = CellColors.Default;
                    a = CellAttributes.None;
                    break;
                case 1: a |= CellAttributes.Bold; break;
                case 2: a |= CellAttributes.Faint; break;
                case 3: a |= CellAttributes.Italic; break;
                case 4: a |= CellAttributes.Underline; break;
                case 5 or 6: a |= CellAttributes.Blink; break;
                case 7: a |= CellAttributes.Inverse; break;
                case 8: a |= CellAttributes.Invisible; break;
                case 9: a |= CellAttributes.Strike; break;
                case 21 or 22: a &= ~(CellAttributes.Bold | CellAttributes.Faint); break;
                case 23: a &= ~CellAttributes.Italic; break;
                case 24: a &= ~CellAttributes.Underline; break;
                case 25: a &= ~CellAttributes.Blink; break;
                case 27: a &= ~CellAttributes.Inverse; break;
                case 28: a &= ~CellAttributes.Invisible; break;
                case 29: a &= ~CellAttributes.Strike; break;
                case >= 30 and <= 37: fg = CellColors.Palette(code - 30); break;
                case 39: fg = CellColors.Default; break;
                case >= 40 and <= 47: bg = CellColors.Palette(code - 40); break;
                case 49: bg = CellColors.Default; break;
                case >= 90 and <= 97: fg = CellColors.Palette(code - 90 + 8); break;
                case >= 100 and <= 107: bg = CellColors.Palette(code - 100 + 8); break;
                case 38 or 48:
                    // 38;5;N for a palette colour, 38;2;R;G;B for a true one.
                    uint color;
                    if (i + 2 < p.Length && p[i + 1] == 5)
                    {
                        color = CellColors.Palette(p[i + 2]);
                        i += 2;
                    }
                    else if (i + 4 < p.Length && p[i + 1] == 2)
                    {
                        color = CellColors.Rgb(p[i + 2], p[i + 3], p[i + 4]);
                        i += 4;
                    }
                    else
                    {
                        i = p.Length; // malformed: the rest cannot be read reliably
                        break;
                    }

                    if (code == 38)
                    {
                        fg = color;
                    }
                    else
                    {
                        bg = color;
                    }

                    break;
            }
        }

        _pen = new CellColors(fg, bg, a);
    }

    private void Reply(string text) => _replies.Add(Encoding.ASCII.GetBytes(text));

    private int NextTab(int x)
    {
        for (int c = x + 1; c < Columns; c++)
        {
            if (_tabStops[c])
            {
                return c;
            }
        }

        return Columns - 1;
    }

    /// <summary>Overwriting either half of a wide character erases the other half too, so no orphan is drawn.</summary>
    private void ClearWideAt(TerminalCell[] line, int x)
    {
        if (x < 0 || x >= Columns)
        {
            return;
        }

        if (line[x].Width == 2 && x + 1 < Columns)
        {
            line[x + 1] = TerminalCell.Space(_pen);
        }
        else if (line[x].IsSpacer && x > 0)
        {
            line[x - 1] = TerminalCell.Space(_pen);
        }
    }

    private static int Param(ReadOnlySpan<int> p, int index, int fallback) =>
        index < p.Length && p[index] != 0 ? p[index] : fallback;

    private void Mark(int from, int to)
    {
        for (int r = Math.Max(0, from); r <= Math.Min(to, Rows - 1); r++)
        {
            _dirty[r] = true;
        }
    }

    private void MarkAll() => Array.Fill(_dirty, true);

    private static TerminalCell[][] Blank(int columns, int rows)
    {
        var grid = new TerminalCell[rows][];
        for (int r = 0; r < rows; r++)
        {
            grid[r] = new TerminalCell[columns];
            Array.Fill(grid[r], TerminalCell.Space(CellColors.Plain));
        }

        return grid;
    }

    private static TerminalCell[][] Refit(TerminalCell[][] grid, int columns, int rows, int dropTop)
    {
        TerminalCell[][] fitted = Blank(columns, rows);
        for (int r = 0; r < rows && r + dropTop < grid.Length; r++)
        {
            TerminalCell[] from = grid[r + dropTop];
            Array.Copy(from, fitted[r], Math.Min(columns, from.Length));
            if (columns < from.Length && fitted[r][columns - 1].Width == 2)
            {
                fitted[r][columns - 1] = TerminalCell.Space(CellColors.Plain); // half a wide character is none
            }
        }

        return fitted;
    }

    private static bool[] TabStops(int columns)
    {
        var stops = new bool[columns];
        for (int c = 8; c < columns; c += 8)
        {
            stops[c] = true;
        }

        return stops;
    }

    /// <summary>DEC special graphics: the line-drawing set that <c>ESC ( 0</c> selects, as used by dialog, tmux and mc.</summary>
    private static int DecGraphics(int code) => code switch
    {
        '`' => '◆', 'a' => '▒', 'f' => '°', 'g' => '±', 'j' => '┘', 'k' => '┐', 'l' => '┌', 'm' => '└',
        'n' => '┼', 'o' => '⎺', 'p' => '⎻', 'q' => '─', 'r' => '⎼', 's' => '⎽', 't' => '├', 'u' => '┤',
        'v' => '┴', 'w' => '┬', 'x' => '│', 'y' => '≤', 'z' => '≥', '{' => 'π', '|' => '≠', '}' => '£', '~' => '·',
        _ => code,
    };
}
