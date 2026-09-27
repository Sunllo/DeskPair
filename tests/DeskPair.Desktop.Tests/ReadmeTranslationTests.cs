namespace DeskPair.Desktop.Tests;

/// <summary>
/// The README in ten languages. README.md, in English, is the source; the nine under docs/readme/ follow it, and a
/// translation that has been left behind is worse than none, because its instructions are the ones that fail. These
/// checks are what notice: every command in the English code blocks, word for word and in order, in each
/// translation; the same sections; and language links that reach all ten from every one.
/// </summary>
public class ReadmeTranslationTests
{
    private static readonly string[] Codes = ["zh-Hant", "zh-Hans", "ja", "ko", "de", "fr", "es", "pt-BR", "ru"];

    public static TheoryData<string> Translations => [.. Codes];

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DeskPair.slnx")))
        {
            dir = dir.Parent;
        }

        dir.ShouldNotBeNull("could not find the repository root from the test output directory");
        return dir!.FullName;
    }

    private static string English() => File.ReadAllText(Path.Combine(Root(), "README.md"));

    private static string Translation(string code) =>
        File.ReadAllText(Path.Combine(Root(), "docs", "readme", $"README.{code}.md"));

    private static IEnumerable<string> Lines(string markdown) => markdown.Split('\n').Select(l => l.TrimEnd('\r'));

    /// <summary>The commands inside the code blocks: a comment is dropped, whether on a line of its own or after one.</summary>
    private static List<string> Commands(string markdown)
    {
        var commands = new List<string>();
        bool inside = false;
        foreach (string line in Lines(markdown))
        {
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                inside = !inside;
                continue;
            }

            int comment = line.TrimStart().StartsWith('#') ? 0 : line.IndexOf(" #", StringComparison.Ordinal);
            string command = (comment >= 0 ? line[..comment] : line).Trim();
            if (inside && command.Length > 0)
            {
                commands.Add(command);
            }
        }

        return commands;
    }

    private static int Sections(string markdown) => Lines(markdown).Count(l => l.StartsWith("## ", StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(Translations))]
    public void A_translation_gives_the_same_commands_in_the_same_order(string code)
    {
        List<string> english = Commands(English());
        english.ShouldNotBeEmpty();

        Commands(Translation(code)).ShouldBe(english, $"README.{code}.md has fallen behind README.md's commands");
    }

    [Theory]
    [MemberData(nameof(Translations))]
    public void A_translation_has_every_section_and_points_back_to_the_english(string code)
    {
        string text = Translation(code);

        Sections(text).ShouldBe(Sections(English()), $"README.{code}.md has a different number of sections");
        text.ShouldContain("(../../README.md)");
    }

    [Theory]
    [MemberData(nameof(Translations))]
    public void Every_readme_links_every_other_language(string code)
    {
        English().ShouldContain($"(docs/readme/README.{code}.md)");

        string text = Translation(code);
        foreach (string other in Codes.Where(c => c != code))
        {
            text.ShouldContain($"(README.{other}.md)", customMessage: $"README.{code}.md does not link {other}");
        }
    }

    [Fact]
    public void Every_translation_on_disk_is_one_of_the_ten()
    {
        string[] onDisk = Directory.GetFiles(Path.Combine(Root(), "docs", "readme"), "README.*.md")
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray()!;

        onDisk.ShouldBe(Codes.Select(c => $"README.{c}.md").Order(StringComparer.Ordinal).ToArray());
    }
}
