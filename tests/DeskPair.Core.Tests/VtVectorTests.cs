using System.Text;
using System.Text.Json;
using DeskPair.Core.Terminal;

namespace DeskPair.Core.Tests;

/// <summary>
/// The shared vectors in <c>tests/fixtures/vt</c>, the same files the Kotlin parser is held to. Each is fed
/// whole, a byte at a time and in seeded random pieces, and all three must agree: a pty and the network cut
/// the stream anywhere, including inside a UTF-8 character and inside an escape sequence.
/// </summary>
public class VtVectorTests
{
    public static TheoryData<string> Vectors()
    {
        var data = new TheoryData<string>();
        foreach (string file in Directory.GetFiles(FixtureDirectory, "*.json").Order(StringComparer.Ordinal))
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
            foreach (JsonElement v in doc.RootElement.EnumerateArray())
            {
                data.Add($"{Path.GetFileName(file)}: {v.GetProperty("name").GetString()}");
            }
        }

        return data;
    }

    private static string FixtureDirectory => Path.Combine(AppContext.BaseDirectory, "fixtures", "vt");

    [Theory]
    [MemberData(nameof(Vectors))]
    public void The_screen_matches_the_vector_however_the_bytes_are_cut(string vector)
    {
        JsonElement v = Find(vector);
        byte[] input = v.TryGetProperty("inputHex", out JsonElement hex)
            ? Convert.FromHexString(hex.GetString()!)
            : Encoding.UTF8.GetBytes(v.GetProperty("input").GetString()!);
        int columns = v.GetProperty("columns").GetInt32();
        int rows = v.GetProperty("rows").GetInt32();

        TerminalScreen whole = new(columns, rows);
        whole.Feed(input);
        Check(whole, v, "fed whole");

        TerminalScreen single = new(columns, rows);
        foreach (byte b in input)
        {
            single.Feed([b]);
        }

        Check(single, v, "fed a byte at a time");

        var random = new Random(input.Length * 7919 + columns);
        TerminalScreen pieces = new(columns, rows);
        for (int at = 0; at < input.Length;)
        {
            int n = Math.Min(random.Next(1, 5), input.Length - at);
            pieces.Feed(input.AsSpan(at, n));
            at += n;
        }

        Check(pieces, v, "fed in random pieces");
    }

    private static void Check(TerminalScreen screen, JsonElement v, string how)
    {
        string[] lines = [.. v.GetProperty("lines").EnumerateArray().Select(l => l.GetString()!)];
        for (int r = 0; r < screen.Rows; r++)
        {
            screen.RowText(r).ShouldBe(r < lines.Length ? lines[r] : string.Empty, $"row {r}, {how}");
        }

        int[] cursor = [.. v.GetProperty("cursor").EnumerateArray().Select(c => c.GetInt32())];
        (screen.CursorColumn, screen.CursorRow).ShouldBe((cursor[0], cursor[1]), $"cursor, {how}");

        if (v.TryGetProperty("scrollback", out JsonElement scrollback))
        {
            screen.Scrollback.Select(Text).ShouldBe(scrollback.EnumerateArray().Select(s => s.GetString()!), $"scrollback, {how}");
        }

        if (v.TryGetProperty("replies", out JsonElement replies))
        {
            screen.TakeReplies().Select(r => Encoding.ASCII.GetString(r)).ShouldBe(replies.EnumerateArray().Select(s => s.GetString()!), $"replies, {how}");
        }

        if (v.TryGetProperty("title", out JsonElement title))
        {
            screen.Title.ShouldBe(title.GetString(), $"title, {how}");
        }

        if (v.TryGetProperty("modes", out JsonElement modes))
        {
            foreach (JsonProperty mode in modes.EnumerateObject())
            {
                bool actual = mode.Name switch
                {
                    "alternateScreen" => screen.OnAlternateScreen,
                    "bracketedPaste" => screen.BracketedPaste,
                    "applicationCursorKeys" => screen.ApplicationCursorKeys,
                    "cursorVisible" => screen.CursorVisible,
                    _ => throw new InvalidOperationException("unknown mode in the vector: " + mode.Name),
                };
                actual.ShouldBe(mode.Value.GetBoolean(), $"{mode.Name}, {how}");
            }
        }

        if (v.TryGetProperty("cells", out JsonElement cells))
        {
            foreach (JsonElement c in cells.EnumerateArray())
            {
                TerminalCell cell = screen.Row(c.GetProperty("row").GetInt32())[c.GetProperty("column").GetInt32()];
                string where = $"cell {c.GetProperty("row").GetInt32()},{c.GetProperty("column").GetInt32()}, {how}";
                Color(cell.Colors.Foreground).ShouldBe(c.GetProperty("foreground").GetString(), "foreground of " + where);
                Color(cell.Colors.Background).ShouldBe(c.GetProperty("background").GetString(), "background of " + where);
                Attributes(cell.Colors.Attributes).ShouldBe(c.GetProperty("attributes").EnumerateArray().Select(a => a.GetString()!).Order(StringComparer.Ordinal), "attributes of " + where);
            }
        }
    }

    private static string Text(TerminalCell[] line) => string.Concat(line.Select(c => c.Text)).TrimEnd(' ');

    private static string Color(uint color) =>
        color == CellColors.Default ? "default"
        : CellColors.TryPalette(color, out int index) ? $"palette:{index}"
        : CellColors.TryRgb(color, out byte r, out byte g, out byte b) ? $"rgb:{r:x2}{g:x2}{b:x2}"
        : "?";

    private static IEnumerable<string> Attributes(CellAttributes a) =>
        Enum.GetValues<CellAttributes>().Where(f => f != CellAttributes.None && a.HasFlag(f)).Select(f => f.ToString().ToLowerInvariant()).Order(StringComparer.Ordinal);

    private static JsonElement Find(string vector)
    {
        int colon = vector.IndexOf(": ", StringComparison.Ordinal);
        string file = vector[..colon];
        string name = vector[(colon + 2)..];
        JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureDirectory, file)));
        return doc.RootElement.EnumerateArray().Single(v => v.GetProperty("name").GetString() == name).Clone();
    }
}
