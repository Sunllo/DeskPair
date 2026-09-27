using Microsoft.Extensions.Logging.Abstractions;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Linux.Capture;

namespace DeskPair.Platform.Linux.Tests;

/// <summary>Read-only against a live X session; nothing here changes the screen.</summary>
public class X11DisplayModesTests
{
    [Fact]
    public void The_current_mode_is_among_those_offered()
    {
        if (!X11Session.IsAvailable || X11Session.IsWayland)
        {
            return;
        }

        using var displays = new X11DisplayEnumerator(NullLogger<X11DisplayEnumerator>.Instance);
        using var modes = new X11DisplayModes(NullLogger<X11DisplayModes>.Instance);
        foreach (DisplayDescriptor display in displays.GetDisplays())
        {
            IReadOnlyList<DisplayMode> offered = modes.GetModes(display);
            if (offered.Count == 0)
            {
                continue; // the root-window fallback, or an output whose modes are all tiny (Xvfb)
            }

            offered.ShouldContain(m => m.Width == display.Width && m.Height == display.Height, $"{display.Name} is at {display.Width}x{display.Height}");
            offered.ShouldBe(offered.OrderByDescending(m => (long)m.Width * m.Height).ThenByDescending(m => m.Width).ToList(), "largest first");
            offered.Distinct().Count().ShouldBe(offered.Count, "no duplicates");
        }
    }

    /// <summary>Under Wayland the RandR extension is Xwayland's fiction, so there is nothing to offer and a request says why.</summary>
    [Fact]
    public void Wayland_offers_nothing_and_refuses_with_a_reason()
    {
        using var modes = new X11DisplayModes(NullLogger<X11DisplayModes>.Instance, wayland: true);
        var display = new DisplayDescriptor(0, "Virtual-1", 0, 0, 1920, 1080, 1.0, FrameRotation.None, true, 0);

        modes.GetModes(display).ShouldBeEmpty();
        modes.TrySetMode(display, new DisplayMode(1280, 720), out string? failure).ShouldBeFalse();
        failure.ShouldContain("Wayland");
    }

    /// <summary>An output name that looks like an option never reaches the command line.</summary>
    [Fact]
    public void A_name_that_looks_like_an_option_is_refused()
    {
        using var modes = new X11DisplayModes(NullLogger<X11DisplayModes>.Instance, wayland: false);
        var display = new DisplayDescriptor(0, "--verbose", 0, 0, 1920, 1080, 1.0, FrameRotation.None, true, 0);

        modes.TrySetMode(display, new DisplayMode(1280, 720), out string? failure).ShouldBeFalse();
        failure.ShouldContain("output name");
    }
}
