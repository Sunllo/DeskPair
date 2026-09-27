using System.Text;
using DeskPair.Core.Terminal;

namespace DeskPair.Core.Tests;

/// <summary>What the shared vectors cannot express: resizing, the scrollback bound, redraw bookkeeping, events.</summary>
public class TerminalScreenTests
{
    /// <summary>A window dragged shorter must keep the line being typed on in view, not cut it off the bottom.</summary>
    [Fact]
    public void Shrinking_keeps_the_cursor_line_and_moves_the_rest_to_scrollback()
    {
        var screen = new TerminalScreen(10, 4);
        screen.Feed("1\r\n2\r\n3\r\n$ ls");

        screen.Resize(10, 2);

        screen.RowText(0).ShouldBe("3");
        screen.RowText(1).ShouldBe("$ ls");
        (screen.CursorColumn, screen.CursorRow).ShouldBe((4, 1));
        screen.Scrollback.Select(l => string.Concat(l.Select(c => c.Text)).TrimEnd()).ShouldBe(["1", "2"]);
    }

    [Fact]
    public void Narrowing_cuts_rows_and_never_leaves_half_a_wide_character()
    {
        var screen = new TerminalScreen(10, 2);
        screen.Feed("ab中cd");

        screen.Resize(3, 2);

        screen.RowText(0).ShouldBe("ab");
        screen.CursorColumn.ShouldBe(2);

        screen.Resize(10, 3);
        screen.Rows.ShouldBe(3);
        screen.RowText(0).ShouldBe("ab");
        screen.Feed("\u001b[3;1Hz");
        screen.RowText(2).ShouldBe("z", "the new rows are usable");
    }

    [Fact]
    public void Scrollback_is_bounded()
    {
        var screen = new TerminalScreen(5, 2);
        var input = new StringBuilder();
        for (int i = 0; i < TerminalScreen.ScrollbackLines + 50; i++)
        {
            input.Append(i).Append("\r\n");
        }

        screen.Feed(input.ToString());

        screen.Scrollback.Count.ShouldBe(TerminalScreen.ScrollbackLines);
        string.Concat(screen.Scrollback[^1].Select(c => c.Text)).TrimEnd().ShouldBe((TerminalScreen.ScrollbackLines + 48).ToString()[..5]);
    }

    [Fact]
    public void The_alternate_screen_adds_nothing_to_the_scrollback()
    {
        var screen = new TerminalScreen(5, 2);
        screen.Feed("\u001b[?1049h1\r\n2\r\n3\r\n4\u001b[?1049l");

        screen.Scrollback.ShouldBeEmpty();
    }

    [Fact]
    public void Only_changed_rows_are_reported_for_redraw()
    {
        var screen = new TerminalScreen(10, 5);
        screen.TakeDirtyRows().Count.ShouldBe(5, "everything is new at first");
        screen.TakeDirtyRows().ShouldBeEmpty();

        screen.Feed("\u001b[3;1Hx");

        screen.TakeDirtyRows().ShouldBe([2]);
    }

    [Fact]
    public void Bell_and_title_are_raised_as_events()
    {
        var screen = new TerminalScreen(10, 1);
        int bells = 0;
        string? title = null;
        screen.Bell += () => bells++;
        screen.TitleChanged += t => title = t;

        screen.Feed("\u0007\u001b]2;vim\u0007");

        bells.ShouldBe(1);
        title.ShouldBe("vim");
    }

    [Fact]
    public void Replies_are_taken_once()
    {
        var screen = new TerminalScreen(10, 1);
        screen.Feed("\u001b[6n");

        screen.TakeReplies().Count.ShouldBe(1);
        screen.TakeReplies().ShouldBeEmpty();
    }

    [Fact]
    public void Full_reset_clears_everything()
    {
        var screen = new TerminalScreen(10, 2);
        screen.Feed("\u001b[?2004h\u001b[1;31mabc\u001bc");

        screen.RowText(0).ShouldBeEmpty();
        screen.BracketedPaste.ShouldBeFalse();
        (screen.CursorColumn, screen.CursorRow).ShouldBe((0, 0));
        screen.Feed("x");
        screen.Row(0)[0].Colors.ShouldBe(CellColors.Plain);
    }
}
