using System.Globalization;

namespace DeskPair.Platform.Linux.Capture;

/// <summary>
/// One display timing: what <c>xrandr --newmode</c> takes after the mode's name.
/// </summary>
/// <param name="ClockKHz">Pixel clock, in kHz, a whole number of 250 kHz steps as CVT requires.</param>
/// <param name="HDisplay">Visible pixels per line.</param>
/// <param name="HSyncStart">Pixel at which horizontal sync starts.</param>
/// <param name="HSyncEnd">Pixel at which horizontal sync ends.</param>
/// <param name="HTotal">Pixels per line, blanking included.</param>
/// <param name="VDisplay">Visible lines.</param>
/// <param name="VSyncStart">Line at which vertical sync starts.</param>
/// <param name="VSyncEnd">Line at which vertical sync ends.</param>
/// <param name="VTotal">Lines per frame, blanking included.</param>
/// <param name="ReducedBlanking">CVT-RB timings (positive horizontal sync), rather than standard ones.</param>
internal readonly record struct Modeline(
    int ClockKHz,
    int HDisplay, int HSyncStart, int HSyncEnd, int HTotal,
    int VDisplay, int VSyncStart, int VSyncEnd, int VTotal,
    bool ReducedBlanking)
{
    /// <summary>The line as <c>cvt</c> prints it after the name: clock in MHz, then the eight timings and the sync polarities.</summary>
    public override string ToString() => string.Join(' ', Arguments());

    /// <summary>The arguments of <c>xrandr --newmode NAME ...</c>, invariant culture: a comma for a decimal point is a different number.</summary>
    public IReadOnlyList<string> Arguments() =>
    [
        (ClockKHz / 1000.0).ToString("F2", CultureInfo.InvariantCulture),
        .. new[] { HDisplay, HSyncStart, HSyncEnd, HTotal, VDisplay, VSyncStart, VSyncEnd, VTotal }.Select(v => v.ToString(CultureInfo.InvariantCulture)),
        ReducedBlanking ? "+hsync" : "-hsync",
        ReducedBlanking ? "-vsync" : "+vsync",
    ];
}

/// <summary>
/// VESA Coordinated Video Timings, as the <c>cvt</c> tool computes them (libxcvt), so that a size taught to an
/// output has the timings every X user would have typed for it.
///
/// Done here rather than by running <c>cvt</c>: it is one more program that may not be installed, and the
/// answer can be tested on a machine with no X at all -- against the tool's own output, which is what the tests
/// hold it to. The arithmetic keeps libxcvt's mix of float and integer truncation on purpose: done in double
/// throughout, a clock lands a 250 kHz step away for some sizes.
///
/// One departure, deliberate: CVT works in widths that are a multiple of 8, and <c>cvt</c> rounds a width up to
/// one. Here the timings are those of the rounded width, but the active width stays the one asked for -- the
/// difference becomes blanking -- so a viewer asking for 1234 wide gets 1234, not 1240.
/// </summary>
internal static class Cvt
{
    private const int HGranularity = 8;
    private const int MinVPorch = 3;
    private const int MinVBackPorch = 6;
    private const int ClockStep = 250;

    // Standard blanking.
    private const double MinVSyncBackPorch = 550.0;
    private const int HSyncPercentage = 8;
    private const int MPrime = 600 * 128 / 256;
    private const int CPrime = (40 - 20) * 128 / 256 + 20;

    // Reduced blanking (version 1).
    private const double RbMinVBlank = 460.0;
    private const int RbHSync = 32;
    private const int RbHBlank = 160;
    private const int RbVFrontPorch = 3;

    public static Modeline Compute(int width, int height, double refresh = 60.0, bool reducedBlanking = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 8);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 8);

        int hdisplay = (width + HGranularity - 1) / HGranularity * HGranularity;
        int vdisplay = height;
        float vfieldRate = (float)refresh;
        int vsync = VSyncWidth(hdisplay, vdisplay);

        float hperiod;
        int vtotal, htotal, hsyncStart, hsyncEnd;
        if (!reducedBlanking)
        {
            hperiod = (float)(1000000.0 / vfieldRate - MinVSyncBackPorch) / (vdisplay + MinVPorch);
            int syncAndBackPorch = (int)(MinVSyncBackPorch / hperiod) + 1 < vsync + MinVPorch
                ? vsync + MinVPorch
                : (int)(MinVSyncBackPorch / hperiod) + 1;
            vtotal = vdisplay + syncAndBackPorch + MinVPorch;

            float hblankPercentage = (float)(CPrime - MPrime * hperiod / 1000.0);
            if (hblankPercentage < 20)
            {
                hblankPercentage = 20;
            }

            int hblank = (int)(hdisplay * hblankPercentage / (100.0 - hblankPercentage));
            hblank -= hblank % (2 * HGranularity);
            htotal = hdisplay + hblank;
            hsyncEnd = hdisplay + hblank / 2;
            hsyncStart = hsyncEnd - htotal * HSyncPercentage / 100;

            // libxcvt's rounding, kept: up to the next multiple of 8, and a whole 8 further when it already is one.
            hsyncStart += HGranularity - hsyncStart % HGranularity;
        }
        else
        {
            hperiod = (float)(1000000.0 / vfieldRate - RbMinVBlank) / vdisplay;
            int vbiLines = (int)((float)RbMinVBlank / hperiod + 1);
            vbiLines = Math.Max(vbiLines, RbVFrontPorch + vsync + MinVBackPorch);
            vtotal = vdisplay + vbiLines;
            htotal = hdisplay + RbHBlank;
            hsyncEnd = hdisplay + RbHBlank / 2;
            hsyncStart = hsyncEnd - RbHSync;
        }

        int clock = (int)(htotal * 1000.0 / hperiod);
        clock -= clock % ClockStep;

        int vsyncStart = vdisplay + (reducedBlanking ? RbVFrontPorch : MinVPorch);
        return new Modeline(clock, width, hsyncStart, hsyncEnd, htotal, vdisplay, vsyncStart, vsyncStart + vsync, vtotal, reducedBlanking);
    }

    /// <summary>The vertical sync width CVT uses to say which aspect ratio this is: 4:3, 16:9, 16:10, 5:4, 15:9, or none of them.</summary>
    private static int VSyncWidth(int hdisplay, int vdisplay)
    {
        if (vdisplay % 3 == 0 && vdisplay * 4 / 3 == hdisplay)
        {
            return 4;
        }

        if (vdisplay % 9 == 0 && vdisplay * 16 / 9 == hdisplay)
        {
            return 5;
        }

        if (vdisplay % 10 == 0 && vdisplay * 16 / 10 == hdisplay)
        {
            return 6;
        }

        if ((vdisplay % 4 == 0 && vdisplay * 5 / 4 == hdisplay) || (vdisplay % 9 == 0 && vdisplay * 15 / 9 == hdisplay))
        {
            return 7;
        }

        return 10;
    }
}
