using Avalonia;
using DeskPair.Desktop.Controls;

namespace DeskPair.Desktop.Tests;

/// <summary>
/// Where the remote picture is drawn, in the screen's pixels. At 150 % a layout unit is one and a half pixels, and
/// the picture used to be laid out one unit per remote pixel: even 1:1 reached the screen stretched and blurred.
/// </summary>
public sealed class PictureLayoutTests
{
    /// <summary>The screen pixels a rectangle in layout units covers.</summary>
    private static (double X, double Y, double W, double H) Pixels(Rect r, double scaling) =>
        (Math.Round(r.X * scaling, 6), Math.Round(r.Y * scaling, 6), Math.Round(r.Width * scaling, 6), Math.Round(r.Height * scaling, 6));

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    public void Below_200_percent_one_remote_pixel_is_one_screen_pixel(double scaling)
    {
        PictureLayout layout = PictureLayout.Compute(new Size(4000, 3000), scaling, 1920, 1080, fit: false);

        Pixels(layout.Destination, scaling).ShouldBe((0, 0, 1920, 1080));
        layout.ScreenPixelsPerRemotePixel.ShouldBe(1);
        layout.IsWhole.ShouldBeTrue("drawn as it is, without smoothing");
    }

    /// <summary>From 200 % a remote pixel takes two: the size it had before on those screens, now doubled exactly.</summary>
    [Theory]
    [InlineData(2.0, 2)]
    [InlineData(2.5, 2)]
    [InlineData(3.0, 3)]
    public void From_200_percent_a_remote_pixel_takes_a_whole_number_of_screen_pixels(double scaling, int factor)
    {
        PictureLayout layout = PictureLayout.Compute(new Size(4000, 3000), scaling, 1280, 720, fit: false);

        Pixels(layout.Destination, scaling).ShouldBe((0, 0, 1280 * factor, 720 * factor));
        layout.IsWhole.ShouldBeTrue();
        PictureLayout.NaturalSize(scaling, 1280, 720).Width.ShouldBe(1280.0 * factor / scaling, 1e-9);
    }

    [Fact]
    public void Fit_never_enlarges_and_centres_on_a_pixel_boundary()
    {
        // A 1280x720 window at 150 % is 1920x1080 pixels: a 1600x900 picture fits and stays 1:1, centred.
        PictureLayout layout = PictureLayout.Compute(new Size(1280, 720), 1.5, 1600, 900, fit: true);

        Pixels(layout.Destination, 1.5).ShouldBe((160, 90, 1600, 900));
        layout.IsWhole.ShouldBeTrue();
    }

    /// <summary>An odd margin is split with the extra pixel on the far side, never by putting the picture between pixels.</summary>
    [Fact]
    public void An_odd_margin_does_not_put_the_picture_between_pixels()
    {
        PictureLayout layout = PictureLayout.Compute(new Size(1001 / 1.25, 700 / 1.25), 1.25, 800, 600, fit: true);

        (double x, double y, _, _) = Pixels(layout.Destination, 1.25);
        x.ShouldBe(100);
        y.ShouldBe(50);
    }

    [Fact]
    public void Fit_shrinks_a_picture_larger_than_the_window()
    {
        PictureLayout layout = PictureLayout.Compute(new Size(1280, 720), 1.5, 3840, 2160, fit: true);

        Pixels(layout.Destination, 1.5).ShouldBe((0, 0, 1920, 1080));
        layout.ScreenPixelsPerRemotePixel.ShouldBe(0.5);
        layout.IsWhole.ShouldBeFalse("shrunk pictures are smoothed");
    }

    /// <summary>
    /// The case the window-sized remote display depends on: layout rounding leaves a window of exactly 1920 pixels
    /// a hair short, and the picture must still be drawn whole rather than shrunk by 0.0001 and smoothed.
    /// </summary>
    [Fact]
    public void A_picture_exactly_the_window_size_is_drawn_whole()
    {
        double scaling = 1.25;
        var bounds = new Size(1920 / scaling - 1e-9, 1080 / scaling + 1e-9);

        PictureLayout layout = PictureLayout.Compute(bounds, scaling, 1920, 1080, fit: true);

        layout.IsWhole.ShouldBeTrue();
        Pixels(layout.Destination, scaling).ShouldBe((0, 0, 1920, 1080));
        PictureLayout.RemoteSizeFilling(bounds, scaling).ShouldBe((1920, 1080));
    }

    [Theory]
    [InlineData(1.0, 1600, 900)]
    [InlineData(1.5, 2400, 1350)]
    [InlineData(2.0, 1600, 900)]
    public void The_remote_size_filling_a_window_is_its_pixels_over_the_natural_factor(double scaling, int width, int height) =>
        PictureLayout.RemoteSizeFilling(new Size(1600, 900), scaling).ShouldBe((width, height));

    [Fact]
    public void Nothing_to_draw_gives_an_empty_layout()
    {
        PictureLayout.Compute(new Size(800, 600), 1.5, 0, 0, fit: true).Destination.ShouldBe(default);
        PictureLayout.Compute(new Size(0, 0), 1.5, 800, 600, fit: true).Destination.ShouldBe(default);
    }

    [Fact]
    public void An_unusable_scaling_counts_as_one() =>
        PictureLayout.Compute(new Size(1000, 1000), double.NaN, 640, 480, fit: false).Destination.ShouldBe(new Rect(0, 0, 640, 480));
}
