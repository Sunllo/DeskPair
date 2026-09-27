using System.Globalization;
using DeskPair.Platform.Linux.Capture;

namespace DeskPair.Platform.Linux.Tests;

/// <summary>
/// The timings a size is taught with, held to the <c>cvt</c> tool itself: every expected line below is what
/// <c>cvt</c> (libxcvt, Ubuntu 24.04) printed for the same size, the name left out.
/// </summary>
public class CvtTests
{
    [Theory]
    [InlineData(1920, 1080, 60, "173.00 1920 2048 2248 2576 1080 1083 1088 1120 -hsync +vsync")]
    [InlineData(1280, 720, 60, "74.50 1280 1344 1472 1664 720 723 728 748 -hsync +vsync")]
    [InlineData(800, 600, 60, "38.25 800 832 912 1024 600 603 607 624 -hsync +vsync")]
    [InlineData(1024, 768, 60, "63.50 1024 1072 1176 1328 768 771 775 798 -hsync +vsync")]
    [InlineData(2560, 1440, 60, "312.25 2560 2752 3024 3488 1440 1443 1448 1493 -hsync +vsync")]
    [InlineData(3840, 2160, 60, "712.75 3840 4160 4576 5312 2160 2163 2168 2237 -hsync +vsync")]
    [InlineData(1000, 600, 60, "47.50 1000 1040 1136 1272 600 603 613 624 -hsync +vsync")]
    [InlineData(2560, 1080, 60, "230.00 2560 2720 2992 3424 1080 1083 1093 1120 -hsync +vsync")]
    [InlineData(1280, 1024, 60, "109.00 1280 1368 1496 1712 1024 1027 1034 1063 -hsync +vsync")]
    [InlineData(1680, 1050, 60, "146.25 1680 1784 1960 2240 1050 1053 1059 1089 -hsync +vsync")]
    [InlineData(1920, 1080, 75, "220.75 1920 2064 2264 2608 1080 1083 1088 1130 -hsync +vsync")]
    [InlineData(640, 480, 50, "19.75 640 664 720 800 480 483 487 497 -hsync +vsync")]
    public void Standard_blanking_matches_cvt(int width, int height, int refresh, string expected)
    {
        Cvt.Compute(width, height, refresh).ToString().ShouldBe(expected);
    }

    [Theory]
    [InlineData(1920, 1080, "138.50 1920 1968 2000 2080 1080 1083 1088 1111 +hsync -vsync")]
    [InlineData(2560, 1440, "241.50 2560 2608 2640 2720 1440 1443 1448 1481 +hsync -vsync")]
    [InlineData(1280, 720, "63.75 1280 1328 1360 1440 720 723 728 741 +hsync -vsync")]
    [InlineData(1000, 600, "42.75 1000 1048 1080 1160 600 603 613 619 +hsync -vsync")]
    public void Reduced_blanking_matches_cvt_r(int width, int height, string expected)
    {
        Cvt.Compute(width, height, 60, reducedBlanking: true).ToString().ShouldBe(expected);
    }

    /// <summary>
    /// cvt rounds a width up to a multiple of 8 (1366 becomes 1368, 1234 becomes 1240) and prints that. The timings
    /// here are the same as its, but the active width stays the one asked for: a viewer wanting 1366 gets 1366.
    /// </summary>
    [Theory]
    [InlineData(1366, 768, "85.25 1366 1440 1576 1784 768 771 781 798 -hsync +vsync")]
    [InlineData(1234, 567, "55.50 1234 1288 1408 1576 567 570 580 590 -hsync +vsync")]
    public void A_width_cvt_would_round_is_kept_and_the_rest_is_blanking(int width, int height, string expected)
    {
        Cvt.Compute(width, height).ToString().ShouldBe(expected);
    }

    /// <summary>A comma for a decimal point is another number to xrandr.</summary>
    [Fact]
    public void The_clock_is_written_with_a_point_whatever_the_culture()
    {
        CultureInfo before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Cvt.Compute(1920, 1080).Arguments()[0].ShouldBe("173.00");
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }
}
