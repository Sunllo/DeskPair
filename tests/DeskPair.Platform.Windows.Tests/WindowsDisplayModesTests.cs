using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Windows.Capture;

namespace DeskPair.Platform.Windows.Tests;

/// <summary>
/// Read-only against the real display driver, except for the one test that switches and puts back, which
/// runs only when asked for by name: it changes the screen of whoever is using this machine.
/// </summary>
public class WindowsDisplayModesTests
{
    [Fact]
    public void The_current_mode_is_among_those_offered()
    {
        if (!InteractiveDesktop.IsAvailable)
        {
            return;
        }

        var displays = new WindowsDisplayEnumerator();
        var modes = new WindowsDisplayModes();
        foreach (DisplayDescriptor display in displays.GetDisplays())
        {
            IReadOnlyList<DisplayMode> offered = modes.GetModes(display);
            if (offered.Count == 0)
            {
                continue; // a remote desktop session, or a display with no driver-listed modes
            }

            offered.ShouldContain(m => m.Width == display.Width && m.Height == display.Height, $"{display.Name} is at {display.Width}x{display.Height}");
            offered.ShouldBe(offered.OrderByDescending(m => (long)m.Width * m.Height).ThenByDescending(m => m.Width).ToList(), "largest first");
            offered.Distinct().Count().ShouldBe(offered.Count, "no duplicates");
            offered.ShouldAllBe(m => m.Width >= 640 && m.Height >= 480);
        }
    }

    /// <summary>Asking for the mode a display is already in is a no-op that says yes, which is what the service relies on to restore.</summary>
    [Fact]
    public void Setting_the_current_mode_succeeds_without_changing_anything()
    {
        if (!InteractiveDesktop.IsAvailable)
        {
            return;
        }

        var displays = new WindowsDisplayEnumerator();
        var modes = new WindowsDisplayModes();
        DisplayDescriptor? primary = displays.GetDisplays().FirstOrDefault(d => d.IsPrimary);
        if (primary is not { } display || modes.GetModes(display).Count == 0)
        {
            return;
        }

        modes.TrySetMode(display, new DisplayMode(display.Width, display.Height), out string? failure).ShouldBeTrue(failure);

        DisplayDescriptor after = displays.GetDisplays().First(d => d.Name == display.Name);
        (after.Width, after.Height).ShouldBe((display.Width, display.Height));
    }

    [Fact]
    public void A_size_the_driver_never_listed_is_refused_before_anything_changes()
    {
        if (!InteractiveDesktop.IsAvailable)
        {
            return;
        }

        var displays = new WindowsDisplayEnumerator();
        var modes = new WindowsDisplayModes();
        DisplayDescriptor? primary = displays.GetDisplays().FirstOrDefault(d => d.IsPrimary);
        if (primary is not { } display)
        {
            return;
        }

        modes.TrySetMode(display, new DisplayMode(641, 481), out string? failure).ShouldBeFalse();
        failure.ShouldContain("641x481");
    }

    /// <summary>
    /// Really switches the primary display to its next-smaller mode and back. Off unless
    /// <c>SUNLLO_TEST_DISPLAY_MODES=1</c>: the screen of whoever is sitting here goes black for a moment.
    /// </summary>
    [Fact]
    public void Switching_to_another_mode_and_back_leaves_the_display_as_it_was()
    {
        if (!InteractiveDesktop.IsAvailable || Environment.GetEnvironmentVariable("SUNLLO_TEST_DISPLAY_MODES") is not ("1" or "true"))
        {
            return;
        }

        var displays = new WindowsDisplayEnumerator();
        var modes = new WindowsDisplayModes();
        DisplayDescriptor display = displays.GetDisplays().First(d => d.IsPrimary);
        IReadOnlyList<DisplayMode> offered = modes.GetModes(display);
        DisplayMode? smaller = offered.FirstOrDefault(m => (long)m.Width * m.Height < (long)display.Width * display.Height);
        if (smaller is not { } target)
        {
            return;
        }

        try
        {
            modes.TrySetMode(display, target, out string? failure).ShouldBeTrue(failure);
            DisplayDescriptor changed = displays.GetDisplays().First(d => d.Name == display.Name);
            (changed.Width, changed.Height).ShouldBe((target.Width, target.Height));
        }
        finally
        {
            modes.TrySetMode(display, new DisplayMode(display.Width, display.Height), out _);
        }

        DisplayDescriptor restored = displays.GetDisplays().First(d => d.Name == display.Name);
        (restored.Width, restored.Height).ShouldBe((display.Width, display.Height));
    }
}
