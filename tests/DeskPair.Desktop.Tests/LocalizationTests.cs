using System.Text.RegularExpressions;
using DeskPair.Desktop.Localization;

namespace DeskPair.Desktop.Tests;

/// <summary>
/// The string table, held to the English source in every language the app is published in.
///
/// Modelled on the portal's tests of the same name. A key missing from one language shows in English
/// there, silently, and a placeholder count that differs from the English throws at run time in exactly
/// one language -- both are the kind of thing only a table-wide check catches before a release.
/// </summary>
[Collection("ProcessState")] // touches process-wide state (Strings.Language, Toasts.Current), so never alongside another test that does
public sealed class LocalizationTests : IDisposable
{
    private static readonly Regex Placeholder = new(@"\{(\d+)\}", RegexOptions.Compiled);

    /// <summary>Keys the XAML asks for (<c>{loc:Loc key}</c>) and the code asks for (<c>Strings.Get("key")</c>).</summary>
    private static readonly Regex XamlUse = new(@"\{loc:Loc\s+(?<key>[a-zA-Z0-9.]+)\}", RegexOptions.Compiled);
    private static readonly Regex CodeUse = new(@"Strings\.(?:Get|Format|Has)\(\s*""(?<key>[a-zA-Z0-9.]+)""", RegexOptions.Compiled);

    private readonly string _was = Strings.Language;

    public void Dispose() => Strings.Language = _was;

    public static IEnumerable<object[]> Translated =>
        AppLanguages.All.Where(l => l.Code != AppLanguages.En).Select(l => new object[] { l.Code });

    [Theory]
    [MemberData(nameof(Translated))]
    public void Every_language_in_the_registry_has_a_table(string code) =>
        Strings.Translation(code).ShouldNotBeNull($"{code} is in AppLanguages.All but has no Translations/ file registered");

    [Theory]
    [MemberData(nameof(Translated))]
    public void Every_key_is_translated(string code)
    {
        IReadOnlyDictionary<string, string> table = Strings.Translation(code)!;
        List<string> missing = Strings.All.Keys.Where(k => !table.TryGetValue(k, out string? s) || string.IsNullOrWhiteSpace(s)).ToList();
        missing.ShouldBeEmpty($"{code} is missing: {string.Join(", ", missing)}");
    }

    [Theory]
    [MemberData(nameof(Translated))]
    public void A_translation_takes_the_same_placeholders_as_the_English(string code)
    {
        IReadOnlyDictionary<string, string> table = Strings.Translation(code)!;
        var wrong = new List<string>();
        foreach ((string key, string english) in Strings.All)
        {
            if (!table.TryGetValue(key, out string? translated))
            {
                continue;
            }

            var expected = Placeholder.Matches(english).Select(m => m.Value).OrderBy(v => v).ToList();
            var actual = Placeholder.Matches(translated).Select(m => m.Value).OrderBy(v => v).ToList();
            if (!expected.SequenceEqual(actual))
            {
                wrong.Add($"{key} (English {string.Join(string.Empty, expected)}, {code} {string.Join(string.Empty, actual)})");
            }
        }

        wrong.ShouldBeEmpty(string.Join("; ", wrong));
    }

    [Theory]
    [MemberData(nameof(Translated))]
    public void A_translation_has_no_strings_the_English_has_lost(string code)
    {
        List<string> stale = Strings.Translation(code)!.Keys.Where(k => !Strings.All.ContainsKey(k)).ToList();
        stale.ShouldBeEmpty($"{code} carries keys the English no longer has: {string.Join(", ", stale)}");
    }

    [Theory]
    [MemberData(nameof(Translated))]
    public void A_translation_does_not_carry_markup(string code)
    {
        List<string> marked = Strings.Translation(code)!.Where(kv => kv.Value.Contains('<') && kv.Value.Contains('>')).Select(kv => kv.Key).ToList();
        marked.ShouldBeEmpty();
    }

    [Fact]
    public void Every_key_a_view_or_a_view_model_asks_for_exists()
    {
        string root = ProjectRoot();
        var asked = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(root, "*.axaml", SearchOption.AllDirectories))
        {
            foreach (Match m in XamlUse.Matches(File.ReadAllText(file)))
            {
                asked.Add(m.Groups["key"].Value);
            }
        }

        foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            foreach (Match m in CodeUse.Matches(File.ReadAllText(file)))
            {
                asked.Add(m.Groups["key"].Value);
            }
        }

        asked.Count.ShouldBeGreaterThan(200, "the scan found almost nothing, so it is looking in the wrong place");
        List<string> unknown = asked.Where(k => !Strings.All.ContainsKey(k)).ToList();
        unknown.ShouldBeEmpty($"asked for but not in the table: {string.Join(", ", unknown)}");
    }

    [Theory]
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("zh-Hans-SG", "zh-Hans")]
    [InlineData("zh-SG", "zh-Hans")]
    [InlineData("zh-TW", "zh-TW")]
    [InlineData("zh-Hant-HK", "zh-TW")]
    [InlineData("zh-HK", "zh-TW")]
    [InlineData("zh", "zh-TW")]
    [InlineData("pt-PT", "pt-BR")]
    [InlineData("pt", "pt-BR")]
    [InlineData("ja-JP", "ja")]
    [InlineData("de-AT", "de")]
    [InlineData("nl-NL", "en")]
    [InlineData("", "en")]
    public void The_operating_system_language_maps_the_way_the_website_maps_it(string culture, string expected) =>
        AppLanguages.Match(culture).ShouldBe(expected);

    [Fact]
    public void A_known_code_is_used_and_anything_else_follows_the_system()
    {
        Strings.Language = "zh-TW";
        Strings.Language.ShouldBe("zh-TW");
        Strings.Get("home.tab").ShouldBe("首頁");

        Strings.Language = "en";
        Strings.Get("home.tab").ShouldBe("Home");

        Strings.Language = "system";
        AppLanguages.IsKnown(Strings.Language).ShouldBeTrue("the resolved code is always one of ours");
        Strings.Get("no.such.key").ShouldBe("no.such.key");
    }

    private static string ProjectRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DeskPair.slnx")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir?.FullName ?? ".", "src", "DeskPair.Desktop");
    }
}
