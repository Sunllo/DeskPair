using DeskPair.Platform.Linux.Wayland;
using static DeskPair.Platform.Linux.Wayland.Agent.MutterDisplayConfig;

namespace DeskPair.Platform.Linux.Tests;

/// <summary>
/// What mutter's <c>GetCurrentState</c> answer means for where each monitor is: sizes derived as mutter derives them
/// -- the current mode, turned for a monitor on its side, divided by the scale when the layout is logical.
/// </summary>
public class MutterDisplayConfigTests
{
    /// <summary>What the lab machine's GNOME 46 answered: two 800x600 virtual monitors side by side, laid out physically.</summary>
    [Fact]
    public void The_lab_machines_two_monitors_are_side_by_side()
    {
        var state = new State(
            [new PhysicalMonitor("Virtual-1", 800, 600), new PhysicalMonitor("Virtual-2", 800, 600)],
            [new LogicalMonitor(0, 0, 1.0, 0, true, ["Virtual-1"]), new LogicalMonitor(800, 0, 1.0, 0, false, ["Virtual-2"])],
            LayoutPhysical);

        Interpret(state).Monitors.ShouldBe([new LayoutMonitor("Virtual-1", 0, 0, 800, 600), new LayoutMonitor("Virtual-2", 800, 0, 800, 600)]);
    }

    [Fact]
    public void A_logical_layout_divides_by_the_scale_and_rounds_as_mutter_does()
    {
        var state = new State(
            [new PhysicalMonitor("eDP-1", 2560, 1600), new PhysicalMonitor("DP-1", 2560, 1600)],
            [new LogicalMonitor(0, 0, 2.0, 0, true, ["eDP-1"]), new LogicalMonitor(1280, 0, 1.5, 0, false, ["DP-1"])],
            LayoutLogical);

        Interpret(state).Monitors.ShouldBe([new LayoutMonitor("eDP-1", 0, 0, 1280, 800), new LayoutMonitor("DP-1", 1280, 0, 1707, 1067)]);
    }

    [Fact]
    public void A_physical_layout_keeps_the_modes_size_whatever_the_scale()
    {
        var state = new State([new PhysicalMonitor("eDP-1", 2560, 1600)], [new LogicalMonitor(0, 0, 2.0, 0, true, ["eDP-1"])], LayoutPhysical);

        Interpret(state).Monitors.ShouldBe([new LayoutMonitor("eDP-1", 0, 0, 2560, 1600)]);
    }

    [Theory]
    [InlineData(1u, 1080, 1920)]
    [InlineData(3u, 1080, 1920)]
    [InlineData(5u, 1080, 1920)]
    [InlineData(2u, 1920, 1080)]
    [InlineData(4u, 1920, 1080)]
    public void A_monitor_on_its_side_is_as_tall_as_its_mode_is_wide(uint transform, int width, int height)
    {
        var state = new State([new PhysicalMonitor("DP-1", 1920, 1080)], [new LogicalMonitor(0, 0, 1.0, transform, true, ["DP-1"])], LayoutLogical);

        Interpret(state).Monitors.ShouldBe([new LayoutMonitor("DP-1", 0, 0, width, height)]);
    }

    [Fact]
    public void Mirrored_monitors_share_one_place()
    {
        var state = new State(
            [new PhysicalMonitor("HDMI-1", 1920, 1080), new PhysicalMonitor("DP-1", 1920, 1080)],
            [new LogicalMonitor(0, 0, 1.0, 0, true, ["HDMI-1", "DP-1"])],
            LayoutLogical);

        Interpret(state).Monitors.ShouldBe([new LayoutMonitor("HDMI-1", 0, 0, 1920, 1080), new LayoutMonitor("DP-1", 0, 0, 1920, 1080)]);
    }

    [Fact]
    public void A_monitor_without_a_current_mode_or_a_sensible_scale_has_no_place()
    {
        var state = new State(
            [new PhysicalMonitor("DP-1", 0, 0), new PhysicalMonitor("DP-2", 1280, 1024)],
            [
                new LogicalMonitor(0, 0, 1.0, 0, true, ["DP-1"]),
                new LogicalMonitor(0, 0, 0.0, 0, false, ["DP-2"]),
                new LogicalMonitor(0, 0, 1.0, 0, false, ["DP-9"]),
            ],
            LayoutLogical);

        Interpret(state).Monitors.ShouldBeEmpty();
    }
}
