using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using DeskPair.Core.Terminal;
using DeskPair.Desktop.Localization;

namespace DeskPair.Desktop.Controls;

/// <summary>
/// Draws a <see cref="TerminalScreen"/> and turns keys into the bytes a terminal sends.
///
/// Text is drawn as glyph runs with an explicit advance per cell, never through a text layout: a layout
/// measures each glyph by its own width and kerns pairs, and a grid drawn that way drifts out of its
/// columns within a line -- the difference between a terminal that looks right and one that looks drunk.
/// A wide character is given two cells' advance; a glyph the monospace face lacks (CJK, symbols) comes
/// from the system's fallback for that character, still at the grid's advance.
///
/// Plain typing arrives through <see cref="OnTextInput"/>, which is what an input method commits to;
/// special keys and Ctrl/Alt combinations are translated in <see cref="OnKeyDown"/> by the same table the
/// phones use (<see cref="TerminalKeys"/>). Every key is marked handled, so Tab does not move the focus.
/// </summary>
public sealed class TerminalControl : Control
{
    private static readonly string[] Families = OperatingSystem.IsWindows()
        ? ["Cascadia Mono", "Consolas", "Lucida Console"]
        : OperatingSystem.IsMacOS()
            ? ["SF Mono", "Menlo", "Monaco"]
            : ["DejaVu Sans Mono", "Noto Sans Mono", "Liberation Mono", "Ubuntu Mono"];

    private readonly Dictionary<(int Rune, bool Bold), (IGlyphTypeface Face, ushort Glyph)> _glyphs = [];
    private readonly List<GlyphRun> _lastFrame = [];
    private IGlyphTypeface _regular = null!;
    private IGlyphTypeface _bold = null!;
    private double _cellWidth;
    private double _cellHeight;
    private double _baseline;
    private double _fontSize = 14;
    private TerminalScreen? _screen;
    private int _scrollOffset;
    private (long Line, int Column)? _selectionStart;
    private (long Line, int Column)? _selectionEnd;
    private bool _selecting;
    private (string Text, long At) _suppressText;
    private (int Columns, int Rows) _grid;

    public TerminalControl()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Ibeam);
        LoadFont();

        var copy = new MenuItem { Header = Strings.Get("terminal.copy") };
        copy.Click += async (_, _) => await CopyAsync();
        var paste = new MenuItem { Header = Strings.Get("terminal.pasteMenu") };
        paste.Click += async (_, _) => await PasteAsync();
        ContextMenu = new ContextMenu { ItemsSource = new[] { copy, paste } };
    }

    /// <summary>Bytes the user typed, for the shell.</summary>
    public event Action<byte[]>? Input;

    /// <summary>The grid that fits the control changed; the owner resizes the screen and tells the host.</summary>
    public event Action<int, int>? GridSizeChanged;

    /// <summary>Text the user asked to paste; the owner decides whether to ask first.</summary>
    public event Action<string>? PasteRequested;

    public TerminalScreen? Screen
    {
        get => _screen;
        set
        {
            _screen = value;
            _scrollOffset = 0;
            ClearSelection();
            InvalidateVisual();
        }
    }

    public double TerminalFontSize
    {
        get => _fontSize;
        set
        {
            _fontSize = Math.Clamp(value, 8, 40);
            LoadFont();
            OnGridMaybeChanged();
            InvalidateVisual();
        }
    }

    /// <summary>Called by the owner when the screen changed; returns the view to the bottom only when asked.</summary>
    public void Refresh() => InvalidateVisual();

    // ---- fonts ----

    private void LoadFont()
    {
        _glyphs.Clear();
        _regular = Face(FontWeight.Normal);
        _bold = Face(FontWeight.Bold);
        FontMetrics m = _regular.Metrics;
        double scale = _fontSize / m.DesignEmHeight;
        ushort em = _regular.GetGlyph('M');
        _cellWidth = Math.Max(1, _regular.GetGlyphAdvance(em) * scale);
        _cellHeight = Math.Ceiling((m.Descent - m.Ascent + m.LineGap) * scale);
        _baseline = Math.Round(-m.Ascent * scale);
    }

    /// <summary>The first installed family of the list; the font manager answers with its default for a name it lacks, so the name is checked.</summary>
    private static IGlyphTypeface Face(FontWeight weight)
    {
        foreach (string family in Families)
        {
            if (FontManager.Current.TryGetGlyphTypeface(new Typeface(family, FontStyle.Normal, weight), out IGlyphTypeface? face)
                && string.Equals(face.FamilyName, family, StringComparison.OrdinalIgnoreCase))
            {
                return face;
            }
        }

        return FontManager.Current.TryGetGlyphTypeface(new Typeface(FontFamily.Default, FontStyle.Normal, weight), out IGlyphTypeface? fallback)
            ? fallback
            : throw new InvalidOperationException("No font to draw a terminal with.");
    }

    private (IGlyphTypeface Face, ushort Glyph) GlyphFor(int rune, bool bold)
    {
        if (_glyphs.TryGetValue((rune, bold), out var hit))
        {
            return hit;
        }

        IGlyphTypeface face = bold ? _bold : _regular;
        (IGlyphTypeface, ushort) result;
        if (face.TryGetGlyph((uint)rune, out ushort glyph) && glyph != 0)
        {
            result = (face, glyph);
        }
        else if (FontManager.Current.TryMatchCharacter(rune, FontStyle.Normal, bold ? FontWeight.Bold : FontWeight.Normal, FontStretch.Normal, null, null, out Typeface match)
            && FontManager.Current.TryGetGlyphTypeface(match, out IGlyphTypeface? other)
            && other.TryGetGlyph((uint)rune, out ushort otherGlyph))
        {
            result = (other, otherGlyph);
        }
        else
        {
            result = (face, face.GetGlyph('?'));
        }

        _glyphs[(rune, bold)] = result;
        return result;
    }

    // ---- layout ----

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        OnGridMaybeChanged();
    }

    private void OnGridMaybeChanged()
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        var grid = (Math.Max(2, (int)(Bounds.Width / _cellWidth)), Math.Max(1, (int)(Bounds.Height / _cellHeight)));
        if (grid != _grid)
        {
            _grid = grid;
            GridSizeChanged?.Invoke(grid.Item1, grid.Item2);
        }
    }

    // ---- drawing ----

    /// <summary>The line shown in view row <paramref name="row"/>: from the scrollback while scrolled back, else the screen.</summary>
    private TerminalCell[]? LineAt(int row, out long absolute)
    {
        TerminalScreen s = _screen!;
        int back = s.Scrollback.Count;
        absolute = back - _scrollOffset + row;
        if (absolute < 0)
        {
            return null;
        }

        if (absolute < back)
        {
            return s.Scrollback[(int)absolute];
        }

        long onScreen = absolute - back;
        return onScreen < s.Rows ? s.Row((int)onScreen) : null;
    }

    public override void Render(DrawingContext context)
    {
        foreach (GlyphRun old in _lastFrame)
        {
            (old as IDisposable)?.Dispose();
        }

        _lastFrame.Clear();
        context.FillRectangle(new SolidColorBrush(TerminalPalette.Background), new Rect(Bounds.Size));
        if (_screen is null)
        {
            return;
        }

        TerminalScreen s = _screen;
        int rows = Math.Min(s.Rows, (int)Math.Ceiling(Bounds.Height / _cellHeight));
        for (int row = 0; row < rows; row++)
        {
            TerminalCell[]? line = LineAt(row, out long absolute);
            if (line is not null)
            {
                DrawLine(context, line, absolute, row * _cellHeight);
            }
        }

        if (_scrollOffset == 0 && s.CursorVisible)
        {
            DrawCursor(context, s);
        }
    }

    private void DrawLine(DrawingContext context, TerminalCell[] line, long absolute, double y)
    {
        int columns = line.Length;

        // Backgrounds first, merged across cells of the same colour.
        int start = 0;
        Color? current = null;
        for (int x = 0; x <= columns; x++)
        {
            Color? bg = x < columns ? CellBackground(line[x], absolute, x) : null;
            if (bg != current)
            {
                if (current is { } fill && fill != TerminalPalette.Background)
                {
                    context.FillRectangle(new SolidColorBrush(fill), new Rect(start * _cellWidth, y, (x - start) * _cellWidth, _cellHeight));
                }

                current = bg;
                start = x;
            }
        }

        // Then text, one glyph run per stretch of the same face and colour.
        var glyphs = new List<GlyphInfo>();
        var chars = new StringBuilder();
        IGlyphTypeface? face = null;
        Color colour = default;
        int runStart = 0;
        for (int x = 0; x < columns; x++)
        {
            TerminalCell cell = line[x];
            if (cell.IsSpacer)
            {
                continue;
            }

            (Color fg, _) = Colours(cell, absolute, x);
            bool blank = cell.Rune is 0 or ' ';
            (IGlyphTypeface Face, ushort Glyph) g = blank ? (face ?? _regular, (ushort)0) : GlyphFor(cell.Rune, cell.Colors.Attributes.HasFlag(CellAttributes.Bold));
            if (glyphs.Count > 0 && (blank || g.Face != face || fg != colour))
            {
                Flush();
            }

            if (blank)
            {
                continue;
            }

            if (glyphs.Count == 0)
            {
                face = g.Face;
                colour = fg;
                runStart = x;
            }

            // One glyph per cell, at the grid's advance; a surrogate pair is two chars in one cluster.
            glyphs.Add(new GlyphInfo(g.Glyph, chars.Length, cell.Width * _cellWidth));
            chars.Append(cell.Text);

            DrawDecorations(context, cell, fg, x, y);
        }

        Flush();

        void Flush()
        {
            if (glyphs.Count == 0 || face is null)
            {
                return;
            }

            var run = new GlyphRun(face, _fontSize, chars.ToString().AsMemory(), glyphs.ToArray(), new Point(runStart * _cellWidth, y + _baseline));
            context.DrawGlyphRun(new SolidColorBrush(colour), run);
            _lastFrame.Add(run);
            glyphs.Clear();
            chars.Clear();
        }
    }

    private void DrawDecorations(DrawingContext context, TerminalCell cell, Color fg, int x, double y)
    {
        CellAttributes a = cell.Colors.Attributes;
        if (!a.HasFlag(CellAttributes.Underline) && !a.HasFlag(CellAttributes.Strike))
        {
            return;
        }

        var pen = new Pen(new SolidColorBrush(fg), 1);
        double left = x * _cellWidth;
        double right = left + (cell.Width * _cellWidth);
        if (a.HasFlag(CellAttributes.Underline))
        {
            double uy = y + _baseline + 1.5;
            context.DrawLine(pen, new Point(left, uy), new Point(right, uy));
        }

        if (a.HasFlag(CellAttributes.Strike))
        {
            double sy = y + (_cellHeight / 2);
            context.DrawLine(pen, new Point(left, sy), new Point(right, sy));
        }
    }

    private void DrawCursor(DrawingContext context, TerminalScreen s)
    {
        TerminalCell cell = s.Row(s.CursorRow)[s.CursorColumn];
        int width = cell.Width == 2 ? 2 : 1;
        var rect = new Rect(s.CursorColumn * _cellWidth, s.CursorRow * _cellHeight, width * _cellWidth, _cellHeight);
        if (!IsFocused)
        {
            context.DrawRectangle(null, new Pen(new SolidColorBrush(TerminalPalette.Cursor), 1), rect.Deflate(0.5));
            return;
        }

        context.FillRectangle(new SolidColorBrush(TerminalPalette.Cursor), rect);
        if (cell.Rune is not (0 or ' ') && !cell.IsSpacer)
        {
            (IGlyphTypeface face, ushort glyph) = GlyphFor(cell.Rune, cell.Colors.Attributes.HasFlag(CellAttributes.Bold));
            var run = new GlyphRun(face, _fontSize, cell.Text.AsMemory(), [new GlyphInfo(glyph, 0, width * _cellWidth)], new Point(rect.X, rect.Y + _baseline));
            context.DrawGlyphRun(new SolidColorBrush(TerminalPalette.Background), run);
            _lastFrame.Add(run);
        }
    }

    private Color CellBackground(TerminalCell cell, long absolute, int x) => Colours(cell, absolute, x).Background;

    private (Color Foreground, Color Background) Colours(TerminalCell cell, long absolute, int x)
    {
        (Color fg, Color bg) = TerminalPalette.Resolve(cell.Colors);
        return IsSelected(absolute, x) ? (TerminalPalette.Foreground, TerminalPalette.Selection) : (fg, bg);
    }

    // ---- selection and clipboard ----

    private bool IsSelected(long line, int column)
    {
        if (_selectionStart is not { } a || _selectionEnd is not { } b || a == b)
        {
            return false;
        }

        ((long L, int C) first, (long L, int C) last) = Compare(a, b) <= 0 ? (a, b) : (b, a);
        var here = (line, column);
        return Compare(here, first) >= 0 && Compare(here, last) < 0;

        static int Compare((long L, int C) p, (long L, int C) q) => p.L != q.L ? p.L.CompareTo(q.L) : p.C.CompareTo(q.C);
    }

    private (long Line, int Column) CellAt(Point p)
    {
        int row = Math.Clamp((int)(p.Y / _cellHeight), 0, Math.Max(0, (_screen?.Rows ?? 1) - 1));
        int column = Math.Clamp((int)Math.Round(p.X / _cellWidth), 0, _screen?.Columns ?? 0);
        return ((_screen?.Scrollback.Count ?? 0) - _scrollOffset + row, column);
    }

    private void ClearSelection()
    {
        _selectionStart = null;
        _selectionEnd = null;
    }

    /// <summary>The selected text, rows joined by newlines, trailing blanks of each row dropped.</summary>
    public string SelectedText()
    {
        if (_screen is null || _selectionStart is not { } a || _selectionEnd is not { } b || a == b)
        {
            return string.Empty;
        }

        TerminalScreen s = _screen;
        ((long L, int C) first, (long L, int C) last) = (a.Line, a.Column).CompareTo((b.Line, b.Column)) <= 0 ? (a, b) : (b, a);
        var text = new StringBuilder();
        for (long line = first.L; line <= last.L; line++)
        {
            TerminalCell[]? cells = line < s.Scrollback.Count ? s.Scrollback[(int)line] : line - s.Scrollback.Count < s.Rows ? s.Row((int)(line - s.Scrollback.Count)) : null;
            if (cells is null)
            {
                continue;
            }

            int from = line == first.L ? first.C : 0;
            int to = line == last.L ? Math.Min(last.C, cells.Length) : cells.Length;
            var row = new StringBuilder();
            for (int x = from; x < to; x++)
            {
                row.Append(cells[x].Text);
            }

            text.Append(row.ToString().TrimEnd(' '));
            if (line < last.L)
            {
                text.Append('\n');
            }
        }

        return text.ToString();
    }

    private async Task CopyAsync()
    {
        string text = SelectedText();
        if (text.Length > 0 && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    private async Task PasteAsync()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard && await clipboard.TryGetTextAsync() is { Length: > 0 } text)
        {
            PasteRequested?.Invoke(text);
        }
    }

    // ---- input ----

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _selectionStart = _selectionEnd = CellAt(e.GetPosition(this));
            _selecting = true;
            e.Pointer.Capture(this);
            InvalidateVisual();
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_selecting)
        {
            _selectionEnd = CellAt(e.GetPosition(this));
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _selecting = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_screen is null)
        {
            return;
        }

        int lines = (int)Math.Round(e.Delta.Y * 3);
        if (_screen.OnAlternateScreen)
        {
            // vim, less and htop have no scrollback to show; what the wheel means there is the arrow keys.
            TerminalKey key = lines > 0 ? TerminalKey.Up : TerminalKey.Down;
            for (int i = 0; i < Math.Abs(lines); i++)
            {
                Input?.Invoke(TerminalKeys.Key(key, TerminalModifiers.None, _screen.ApplicationCursorKeys));
            }
        }
        else
        {
            _scrollOffset = Math.Clamp(_scrollOffset + lines, 0, _screen.Scrollback.Count);
            InvalidateVisual();
        }

        e.Handled = true;
    }

    protected override void OnGotFocus(GotFocusEventArgs e)
    {
        base.OnGotFocus(e);
        InvalidateVisual();
    }

    protected override void OnLostFocus(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        InvalidateVisual();
    }

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_screen is null)
        {
            return;
        }

        KeyModifiers k = e.KeyModifiers;
        bool ctrl = k.HasFlag(KeyModifiers.Control);
        bool alt = k.HasFlag(KeyModifiers.Alt);
        bool shift = k.HasFlag(KeyModifiers.Shift);
        bool meta = k.HasFlag(KeyModifiers.Meta);

        // Copy and paste. Ctrl+C itself must stay SIGINT, so the terminal convention: with Shift, or Cmd on a Mac.
        if ((ctrl && shift && e.Key == Key.C) || (meta && e.Key == Key.C) || (ctrl && e.Key == Key.Insert))
        {
            e.Handled = true;
            await CopyAsync();
            return;
        }

        if ((ctrl && shift && e.Key == Key.V) || (meta && e.Key == Key.V) || (shift && e.Key == Key.Insert))
        {
            e.Handled = true;
            await PasteAsync();
            return;
        }

        if (shift && e.Key is Key.PageUp or Key.PageDown)
        {
            int page = Math.Max(1, _screen.Rows - 1);
            _scrollOffset = Math.Clamp(_scrollOffset + (e.Key == Key.PageUp ? page : -page), 0, _screen.Scrollback.Count);
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        var mods = (shift ? TerminalModifiers.Shift : 0) | (alt ? TerminalModifiers.Alt : 0) | (ctrl ? TerminalModifiers.Control : 0);
        if (Special(e.Key) is { } special)
        {
            Send(TerminalKeys.Key(special, mods, _screen.ApplicationCursorKeys));
            e.Handled = true;
            return;
        }

        // Ctrl or Alt with a character, but not both: Ctrl+Alt is AltGr on European layouts, which types a
        // character and is left to text input.
        if (ctrl != alt && CharacterOf(e) is { } ch)
        {
            Send(TerminalKeys.Text(ch, ctrl ? TerminalModifiers.Control : TerminalModifiers.Alt));

            // Some platforms also deliver the character as text input; that copy is not a second keystroke.
            _suppressText = (ch, Environment.TickCount64);
            e.Handled = true;
        }
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        string? text = e.Text;
        e.Handled = true;
        if (string.IsNullOrEmpty(text) || _screen is null)
        {
            return;
        }

        if (text == _suppressText.Text && Environment.TickCount64 - _suppressText.At < 200)
        {
            _suppressText = default;
            return;
        }

        // Controls come from key handling; a platform that also sends them as text would type them twice.
        string printable = new(text.Where(c => c >= 0x20 && c != 0x7F).ToArray());
        if (printable.Length > 0)
        {
            Send(Encoding.UTF8.GetBytes(printable));
        }
    }

    private void Send(byte[] bytes)
    {
        if (_scrollOffset != 0 || _selectionStart is not null)
        {
            _scrollOffset = 0;
            ClearSelection();
            InvalidateVisual();
        }

        Input?.Invoke(bytes);
    }

    private static TerminalKey? Special(Key key) => key switch
    {
        Key.Enter => TerminalKey.Enter,
        Key.Tab => TerminalKey.Tab,
        Key.Back => TerminalKey.Backspace,
        Key.Escape => TerminalKey.Escape,
        Key.Up => TerminalKey.Up,
        Key.Down => TerminalKey.Down,
        Key.Left => TerminalKey.Left,
        Key.Right => TerminalKey.Right,
        Key.Home => TerminalKey.Home,
        Key.End => TerminalKey.End,
        Key.PageUp => TerminalKey.PageUp,
        Key.PageDown => TerminalKey.PageDown,
        Key.Insert => TerminalKey.Insert,
        Key.Delete => TerminalKey.Delete,
        >= Key.F1 and <= Key.F12 => TerminalKey.F1 + (key - Key.F1),
        _ => null,
    };

    /// <summary>The character a key stands for, for Ctrl and Alt combinations: the layout's symbol when the platform gives one.</summary>
    private static string? CharacterOf(KeyEventArgs e)
    {
        if (e.KeySymbol is { Length: 1 } symbol && symbol[0] >= 0x20)
        {
            return symbol.ToLowerInvariant();
        }

        return e.Key switch
        {
            >= Key.A and <= Key.Z => ((char)('a' + (e.Key - Key.A))).ToString(),
            >= Key.D0 and <= Key.D9 => ((char)('0' + (e.Key - Key.D0))).ToString(),
            Key.Space => " ",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.OemPipe or Key.OemBackslash => "\\",
            Key.OemMinus => "-",
            Key.OemQuestion => "/",
            _ => null,
        };
    }
}
