using DeskPair.Desktop.Localization;
using DeskPair.Desktop.ViewModels;
using DeskPair.Protocol.Messages;

namespace DeskPair.Desktop.Tests;

/// <summary>The resolution drop-down's items and which one is lit, from what the host advertised.</summary>
[Collection("ProcessState")] // reads the string table, which the language tests move
public sealed class ResolutionChoicesTests
{
    /// <summary>Whatever language the process is in: the label is what the table says, not a fixed word.</summary>
    private static string Original => Strings.Get("session.resolution.original");

    private static DisplayInfo Display(int w, int h, double scale = 0, Resolution? original = null, params (int W, int H, double S)[] modes)
    {
        var d = new DisplayInfo { Width = w, Height = h, Scale = scale, Name = "DISPLAY1", Original = original };
        d.Modes.AddRange(modes.Select(m => new Resolution { Width = m.W, Height = m.H, Scale = m.S }));
        return d;
    }

    [Fact]
    public void Original_leads_and_the_current_mode_is_selected()
    {
        DisplayInfo display = Display(1280, 720, modes: [(1920, 1080, 0), (1280, 720, 0), (1024, 768, 0)]);

        (IReadOnlyList<string> labels, int selected) = ResolutionChoices.Build(display);

        labels.ShouldBe([Original, "1920×1080", "1280×720", "1024×768"]);
        selected.ShouldBe(2);
        ResolutionChoices.ModeAt(display, 2)!.Width.ShouldBe(1280);
        ResolutionChoices.ModeAt(display, 0).ShouldBeNull("item 0 asks for the original");
    }

    /// <summary>Once somebody has changed it, the host names the original, and a display back at it is "original", not a list entry.</summary>
    [Fact]
    public void A_display_at_its_original_size_reads_as_original()
    {
        var original = new Resolution { Width = 1920, Height = 1080 };
        DisplayInfo display = Display(1920, 1080, original: original, modes: [(1920, 1080, 0), (1280, 720, 0)]);

        (IReadOnlyList<string> labels, int selected) = ResolutionChoices.Build(display);

        labels[0].ShouldBe($"{Original} (1920×1080)");
        selected.ShouldBe(0);
    }

    /// <summary>A Retina mode is shown the way macOS shows it: in points, with the scale; the same pixels at 1x are a different item.</summary>
    [Fact]
    public void HiDPI_modes_are_labelled_in_points_and_told_apart_by_scale()
    {
        DisplayInfo display = Display(3840, 2160, scale: 2, modes: [(3840, 2160, 2), (3840, 2160, 1), (2560, 1440, 2)]);

        (IReadOnlyList<string> labels, int selected) = ResolutionChoices.Build(display);

        labels.ShouldBe([Original, "1920×1080 (2x)", "3840×2160", "1280×720 (2x)"]);
        selected.ShouldBe(1, "the current mode is the 2x one");
    }

    [Fact]
    public void A_host_without_modes_offers_only_original()
    {
        (IReadOnlyList<string> labels, int selected) = ResolutionChoices.Build(Display(1920, 1080));

        labels.ShouldBe([Original]);
        selected.ShouldBe(0);
    }
}
