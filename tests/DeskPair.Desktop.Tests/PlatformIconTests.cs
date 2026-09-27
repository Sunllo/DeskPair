using System.Xml.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using DeskPair.Desktop.Controls;

namespace DeskPair.Desktop.Tests;

/// <summary>
/// The icons are path data lifted out of SVG files, and path data that Avalonia cannot parse throws when the
/// resource dictionary loads — which is at startup, for the whole application. Parsing every one of them
/// here keeps that failure in the test run.
///
/// Parsing path data builds a platform geometry, so there has to be a platform: [AvaloniaTheory] supplies
/// one for the assembly, with no window and no GPU, which is all this needs.
/// </summary>
public class PlatformIconTests
{
    private static readonly Dictionary<string, string> Icons = LoadIcons();

    /// <summary>
    /// Parsed as XML rather than scanned for what looks like a geometry, because the file is XAML before it
    /// is anything else and a build does not always say so: the compiler only re-reads it when it has
    /// changed, and a comment containing a double hyphen once reached a published build through a test run
    /// that had only ever looked at the file with a regular expression.
    /// </summary>
    private static Dictionary<string, string> LoadIcons()
    {
        string here = AppContext.BaseDirectory;
        string? repo = here;
        while (repo is not null && !Directory.Exists(Path.Combine(repo, "src")))
        {
            repo = Path.GetDirectoryName(repo);
        }

        string path = Path.Combine(repo ?? here, "src", "DeskPair.Desktop", "Assets", "PlatformIcons.axaml");
        XDocument document = XDocument.Load(path);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        return document.Root!.Elements()
            .Where(e => e.Name.LocalName == "StreamGeometry")
            .ToDictionary(e => (string)e.Attribute(x + "Key")!, e => e.Value);
    }

    [AvaloniaTheory]
    [InlineData("icon.windows")]
    [InlineData("icon.macos")]
    [InlineData("icon.ios")]
    [InlineData("icon.android")]
    [InlineData("icon.linux")]
    [InlineData("icon.ubuntu")]
    [InlineData("icon.debian")]
    [InlineData("icon.redhat")]
    public void Every_icon_is_path_data_avalonia_can_draw(string key)
    {
        Icons.ShouldContainKey(key);
        Geometry geometry = StreamGeometry.Parse(Icons[key]);

        geometry.Bounds.Width.ShouldBeGreaterThan(0, $"{key} has no width");
        geometry.Bounds.Height.ShouldBeGreaterThan(0, $"{key} has no height");
    }

    [AvaloniaTheory]
    [InlineData("Windows", "icon.windows")]
    [InlineData("Microsoft Windows NT 10.0.26200.0", "icon.windows")]
    [InlineData("macOS", "icon.macos")]
    [InlineData("Darwin 23.5.0", "icon.macos")]
    [InlineData("iOS 17.4", "icon.ios")]
    [InlineData("iPhone", "icon.ios")]
    [InlineData("Android 14", "icon.android")]
    [InlineData("Linux", "icon.linux")]
    [InlineData("Ubuntu 24.04.1 LTS", "icon.ubuntu")]
    [InlineData("Debian GNU/Linux 12", "icon.debian")]
    [InlineData("Raspbian", "icon.debian")]
    [InlineData("Red Hat Enterprise Linux 9.4", "icon.redhat")]
    [InlineData("Fedora Linux 40", "icon.redhat")]
    [InlineData("Rocky Linux 9", "icon.redhat")]
    public void The_platform_string_picks_the_right_icon(string platform, string expected) =>
        PlatformIcons.KeyFor(platform).ShouldBe(expected);

    /// <summary>
    /// A distribution's own logo beats the penguin, which is why the order of the checks matters: every one
    /// of these contains "linux" as well.
    /// </summary>
    [AvaloniaFact]
    public void A_distribution_is_not_mistaken_for_plain_linux()
    {
        PlatformIcons.KeyFor("Ubuntu 24.04 (Linux 6.8)").ShouldBe("icon.ubuntu");
        PlatformIcons.KeyFor("Debian GNU/Linux").ShouldBe("icon.debian");
        PlatformIcons.KeyFor("Fedora Linux").ShouldBe("icon.redhat");
    }

    /// <summary>Nothing is better than the wrong logo, so an unknown platform simply has no icon.</summary>
    [AvaloniaTheory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("FreeBSD 14")]
    [InlineData("Unknown")]
    public void An_unrecognised_platform_has_no_icon(string? platform) =>
        PlatformIcons.KeyFor(platform).ShouldBeNull();

    /// <summary>
    /// The tray menu's icons are drawn from path data too, and a native menu shows no error when one fails
    /// to render: the entry simply appears without its picture.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(nameof(DeskPair.Desktop.Services.MenuIcons.Window))]
    [InlineData(nameof(DeskPair.Desktop.Services.MenuIcons.Settings))]
    [InlineData(nameof(DeskPair.Desktop.Services.MenuIcons.SignOut))]
    [InlineData(nameof(DeskPair.Desktop.Services.MenuIcons.Quit))]
    public void Every_menu_icon_is_path_data_avalonia_can_draw(string name)
    {
        string data = (string)typeof(DeskPair.Desktop.Services.MenuIcons)
            .GetField(name)!.GetValue(null)!;

        Geometry geometry = StreamGeometry.Parse(data);

        geometry.Bounds.Width.ShouldBeGreaterThan(0, $"{name} has no width");
        geometry.Bounds.Height.ShouldBeGreaterThan(0, $"{name} has no height");
    }

    /// <summary>Path data that cannot be drawn costs the entry its icon, never the tray.</summary>
    [AvaloniaFact]
    public void An_icon_that_cannot_be_drawn_is_simply_absent()
    {
        DeskPair.Desktop.Services.MenuIcons.Render("this is not path data", 0xFFFFFFFF).ShouldBeNull();
        DeskPair.Desktop.Services.MenuIcons.Render(string.Empty, 0xFFFFFFFF).ShouldBeNull();
    }

    [AvaloniaFact]
    public void Every_icon_the_mapper_can_name_exists()
    {
        string[] named =
        [
            .. new[]
            {
                "Windows", "macOS", "iOS", "Android", "Linux", "Ubuntu", "Debian", "Red Hat",
            }.Select(p => PlatformIcons.KeyFor(p)!),
        ];

        named.Distinct().Count().ShouldBe(named.Length, "two platforms share an icon");
        foreach (string key in named)
        {
            Icons.ShouldContainKey(key);
        }
    }
}
