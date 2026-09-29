using DeskPair.Core.Video;
using DeskPair.Protocol.Messages;

namespace DeskPair.Core.Tests;

/// <summary>What a viewer asks a host's display for, so the display matches its window and is shown 1:1.</summary>
public sealed class ResolutionFitTests
{
    private static Resolution Mode(int w, int h, double scale = 0) => new() { Width = w, Height = h, Scale = scale };

    private static DisplayInfo Listed(int w, int h, params Resolution[] modes)
    {
        var display = new DisplayInfo { Width = w, Height = h, Name = "DISPLAY1" };
        display.Modes.AddRange(modes);
        return display;
    }

    private static DisplayInfo AnySize(int w, int h, int minW = 640, int minH = 480, int maxW = 8192, int maxH = 8192, int step = 2) =>
        new()
        {
            Width = w,
            Height = h,
            Name = ":0",
            AnySize = new SizeRange { MinWidth = minW, MinHeight = minH, MaxWidth = maxW, MaxHeight = maxH, Step = step },
        };

    private static void ShouldSet(FitChoice choice, int w, int h, double scale = 0)
    {
        choice.Action.ShouldBe(FitAction.Set);
        (choice.Mode!.Width, choice.Mode.Height, choice.Mode.Scale).ShouldBe((w, h, scale));
    }

    [Fact]
    public void A_display_of_any_size_gets_the_window_exactly_on_even_pixels() =>
        ShouldSet(ResolutionFit.Choose(1234, 777, AnySize(1920, 1080)), 1234, 776);

    [Fact]
    public void Any_size_stays_within_the_range_the_host_gave() =>
        ShouldSet(ResolutionFit.Choose(5000, 300, AnySize(1920, 1080, maxW: 4096, maxH: 2304)), 4096, 480);

    [Fact]
    public void Any_size_keeps_to_a_coarser_step() =>
        ShouldSet(ResolutionFit.Choose(1235, 777, AnySize(1920, 1080, step: 8)), 1232, 776);

    [Fact]
    public void A_display_already_the_window_size_is_left_alone() =>
        ResolutionFit.Choose(1280, 720, AnySize(1280, 720)).ShouldBe(FitChoice.Keep);

    [Fact]
    public void Back_to_the_original_size_is_asked_as_the_original()
    {
        DisplayInfo display = AnySize(1280, 720);
        display.Original = Mode(1920, 1080);

        ResolutionFit.Choose(1920, 1080, display).ShouldBe(FitChoice.Restore);
    }

    [Fact]
    public void Of_listed_modes_the_largest_that_fits_whole_is_chosen()
    {
        DisplayInfo display = Listed(1920, 1080, Mode(1920, 1080), Mode(1680, 1050), Mode(1600, 900), Mode(1280, 1024), Mode(1280, 720));

        ShouldSet(ResolutionFit.Choose(1700, 1000, display), 1600, 900);
    }

    /// <summary>A mode about as large as the largest but shaped like the window leaves the smaller margin.</summary>
    [Fact]
    public void Between_modes_of_about_the_same_size_the_windows_shape_wins()
    {
        DisplayInfo display = Listed(2560, 1440, Mode(2560, 1440), Mode(1600, 1200), Mode(1760, 1080));

        ShouldSet(ResolutionFit.Choose(2000, 1200, display), 1760, 1080);
    }

    /// <summary>
    /// A Windows virtual display lists sizes 16 pixels apart. For a window that is 1920 wide, the size that fills its
    /// width wins over one a step narrower that happens to be a shade closer to the window's shape.
    /// </summary>
    [Fact]
    public void In_a_close_spaced_list_the_size_that_fills_a_side_wins()
    {
        DisplayInfo display = Listed(1600, 900, Mode(1920, 1040), Mode(1920, 992), Mode(1904, 992), Mode(1904, 976), Mode(1888, 1000), Mode(1600, 900));

        ShouldSet(ResolutionFit.Choose(1920, 1000, display), 1920, 992);
    }

    /// <summary>
    /// A Mac lists the same pixels at several scales; the one whose interface looks the size of the viewer's own is
    /// chosen, and between two equally near the larger interface, which is the readable one.
    /// </summary>
    [Theory]
    [InlineData(2.0, 2.0)]
    [InlineData(1.0, 1.0)]
    [InlineData(1.5, 2.0)]
    public void On_a_Mac_the_scale_nearest_the_viewers_is_chosen(double uiScale, double expected)
    {
        DisplayInfo display = Listed(5120, 2880, Mode(5120, 2880, 2), Mode(2880, 1800, 2), Mode(2880, 1800, 1), Mode(1440, 900, 1));
        display.Scale = 2;

        ShouldSet(ResolutionFit.Choose(3000, 1900, display, uiScale), 2880, 1800, expected);
    }

    [Fact]
    public void When_no_mode_fits_the_smallest_is_chosen()
    {
        DisplayInfo display = Listed(1920, 1080, Mode(1920, 1080), Mode(1024, 768), Mode(800, 600));

        ShouldSet(ResolutionFit.Choose(700, 500, display), 800, 600);
    }

    /// <summary>A physical monitor goes dark for a moment on every change: a few percent more picture is not worth it.</summary>
    [Fact]
    public void A_mode_barely_larger_than_the_current_one_is_not_worth_a_change()
    {
        DisplayInfo display = Listed(1600, 900, Mode(1920, 1080), Mode(1620, 912), Mode(1600, 900));

        ResolutionFit.Choose(1700, 1000, display).ShouldBe(FitChoice.Keep);
    }

    [Fact]
    public void A_display_larger_than_the_window_always_changes()
    {
        DisplayInfo display = Listed(1920, 1080, Mode(1920, 1080), Mode(1900, 1000));

        ShouldSet(ResolutionFit.Choose(1910, 1070, display), 1900, 1000);
    }

    [Fact]
    public void Returning_to_the_original_among_listed_modes_is_asked_as_the_original()
    {
        DisplayInfo display = Listed(1600, 900, Mode(1920, 1080), Mode(1600, 900));
        display.Original = Mode(1920, 1080);

        ResolutionFit.Choose(2000, 1200, display).ShouldBe(FitChoice.Restore);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(63, 800)]
    [InlineData(800, 40)]
    public void A_window_too_small_to_mean_anything_asks_for_nothing(int w, int h) =>
        ResolutionFit.Choose(w, h, AnySize(1920, 1080)).ShouldBe(FitChoice.Keep);

    [Fact]
    public void A_display_that_cannot_change_is_left_alone() =>
        ResolutionFit.Choose(1280, 720, Listed(1920, 1080)).ShouldBe(FitChoice.Keep);
}
