using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DeskPair.Desktop.Tests;

/// <summary>
/// The Windows installer in ten languages. WiX brings its own words for the wizard; ours -- the box that opens DeskPair
/// at the end, the refusal to replace a newer version -- are in packaging/windows/Localization, one file per culture
/// the project builds. tools/package.ps1 makes every culture but English into a transform of the English package, so a
/// culture without our file, or one missing a string, would still build: with the string's name where its words
/// should be, in front of whoever reads that language. These notice first.
/// </summary>
public partial class InstallerLocalizationTests
{
    private static readonly XNamespace Wxl = "http://wixtoolset.org/schemas/v4/wxl";

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

    private static string Folder() => Path.Combine(Root(), "packaging", "windows");

    /// <summary>The cultures the project builds by default, which is what package.ps1 builds.</summary>
    private static string[] Cultures()
    {
        string project = File.ReadAllText(Path.Combine(Folder(), "DeskPair.wixproj"));
        Match cultures = CulturesProperty().Match(project);
        cultures.Success.ShouldBeTrue("DeskPair.wixproj names the cultures it builds");
        return cultures.Groups[1].Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static Dictionary<string, string> Strings(string culture)
    {
        XDocument document = XDocument.Load(Path.Combine(Folder(), "Localization", $"{culture}.wxl"));
        document.Root!.Attribute("Culture")?.Value.ShouldBe(culture, $"{culture}.wxl is for the culture its name says");
        return document.Root.Elements(Wxl + "String").ToDictionary(s => s.Attribute("Id")!.Value, s => s.Attribute("Value")!.Value);
    }

    /// <summary>English is the package the rest are transforms of, and the language Windows Installer falls back to.</summary>
    [Fact]
    public void English_comes_first_and_the_app_languages_follow()
    {
        Cultures().ShouldBe(["en-US", "zh-TW", "zh-CN", "ja-JP", "ko-KR", "de-DE", "fr-FR", "es-ES", "pt-BR", "ru-RU"]);
    }

    [Fact]
    public void Every_culture_has_our_strings_and_no_others()
    {
        Dictionary<string, string> english = Strings("en-US");
        english.ShouldNotBeEmpty();

        foreach (string culture in Cultures())
        {
            Dictionary<string, string> strings = Strings(culture);

            // WiX's own strings may be overridden, as Japanese does for the font whose name WiX gives is too long.
            strings.Keys.Where(k => !k.StartsWith("Advanced_Font_", StringComparison.Ordinal))
                .ShouldBe(english.Keys, ignoreOrder: true, $"{culture}.wxl has the same strings as en-US.wxl");
            if (culture != "en-US")
            {
                strings.Where(s => english.ContainsKey(s.Key) && s.Value == english[s.Key]).Select(s => s.Key)
                    .ShouldBeEmpty($"{culture}.wxl leaves these in English");
            }
        }
    }

    /// <summary>A string the package uses that no file defines fails the build only for the culture that lacks it.</summary>
    [Fact]
    public void Every_string_the_package_uses_is_ours_or_wixs()
    {
        string package = File.ReadAllText(Path.Combine(Folder(), "Package.wxs"));
        Dictionary<string, string> ours = Strings("en-US");

        foreach (Match used in LocalizedString().Matches(package))
        {
            string id = used.Groups[1].Value;
            (ours.ContainsKey(id) || id.StartsWith("Advanced_Font_", StringComparison.Ordinal))
                .ShouldBeTrue($"Package.wxs uses !(loc.{id}), which en-US.wxl does not define and WiX does not bring");
        }
    }

    [GeneratedRegex(@"<Cultures[^>]*>([^<]+)</Cultures>")]
    private static partial Regex CulturesProperty();

    [GeneratedRegex(@"!\(loc\.([A-Za-z0-9_]+)\)")]
    private static partial Regex LocalizedString();
}
