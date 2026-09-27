using DeskPair.Platform.Abstractions.Input;
using DeskPair.Platform.Linux.Input;
using DeskPair.Platform.Linux.Native;
using DeskPair.Platform.Linux.Wayland;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeskPair.Platform.Linux.Tests;

/// <summary>
/// A click on the hardware's picture of one monitor, placed on the whole desktop the daemon's virtual pointer is spread
/// over. The last test runs the whole way: the placement, the injector's axis values, and mutter's scaling of them
/// back to the desktop, for every pixel of a monitor -- the chain that put a click on the lock screen of the first of
/// two monitors onto the second.
/// </summary>
public class DesktopLayoutTests
{
    private static readonly DesktopLayout SideBySide = new(
    [
        new LayoutMonitor("Virtual-1", 0, 0, 800, 600),
        new LayoutMonitor("Virtual-2", 800, 0, 800, 600),
    ]);

    private static readonly VirtualScreenRect Picture = new(0, 0, 800, 600);

    [Fact]
    public void The_scanouts_monitor_is_found_by_the_kernels_numbers_whatever_the_compositor_calls_its_type()
    {
        var layout = new DesktopLayout(
        [
            new LayoutMonitor("HDMI-1", 0, 0, 1920, 1080),
            new LayoutMonitor("Virtual-2", 1920, 0, 800, 600),
            new LayoutMonitor("DP-3", 2720, 0, 1280, 1024),
        ]);

        layout.Find(11, 1)?.Connector.ShouldBe("HDMI-1", "the kernel's HDMI-A-1 is mutter's HDMI-1");
        layout.Find(15, 2)?.Connector.ShouldBe("Virtual-2");
        layout.Find(10, 3)?.Connector.ShouldBe("DP-3");
        layout.Find(15, 1).ShouldBeNull("Virtual-1 is not on this desktop");
        layout.Find(11, 2).ShouldBeNull();
    }

    [Theory]
    [InlineData("HDMI-A-1", 11u, 1u, true)]
    [InlineData("hdmi-1", 11u, 1u, true)]
    [InlineData("eDP-1", 14u, 1u, true)]
    [InlineData("Virtual-12", 15u, 1u, false)]
    [InlineData("Virtual-1", 15u, 12u, false)]
    [InlineData("Virtual-", 15u, 0u, false)]
    [InlineData("-1", 15u, 1u, false)]
    [InlineData("Virtual-+1", 15u, 1u, false)]
    [InlineData("DP-1", 15u, 1u, false)]
    public void A_connectors_name_is_its_type_and_number(string name, uint type, uint typeId, bool same) =>
        DesktopLayout.IsConnector(name, type, typeId).ShouldBe(same);

    [Fact]
    public void A_point_on_the_second_monitor_is_placed_past_the_first()
    {
        LayoutMonitor second = SideBySide.Find(15, 2)!.Value;

        SideBySide.Place(second, 10, 20, Picture).ShouldBe((810, 20, new VirtualScreenRect(0, 0, 1600, 600)));
        SideBySide.Place(SideBySide.Find(15, 1)!.Value, 775, 15, Picture).ShouldBe((775, 15, new VirtualScreenRect(0, 0, 1600, 600)),
            "the first monitor's top bar, which the whole-desktop pointer used to put on the second");
    }

    [Fact]
    public void A_monitor_below_another_is_placed_by_its_top()
    {
        var stacked = new DesktopLayout([new LayoutMonitor("DP-1", 0, 0, 800, 600), new LayoutMonitor("DP-2", 0, 600, 1024, 768)]);

        stacked.Place(stacked.Monitors[1], 5, 7, new VirtualScreenRect(0, 0, 1024, 768)).ShouldBe((5, 607, new VirtualScreenRect(0, 0, 1024, 1368)));
    }

    /// <summary>The desktop is measured in the picture's pixels, so a monitor scaled by two keeps all of its own.</summary>
    [Fact]
    public void A_scaled_monitor_keeps_every_pixel_of_its_picture()
    {
        var mixed = new DesktopLayout([new LayoutMonitor("eDP-1", 0, 0, 1280, 800), new LayoutMonitor("DP-1", 1280, 0, 1920, 1080)]);

        mixed.Place(mixed.Monitors[0], 2559, 1599, new VirtualScreenRect(0, 0, 2560, 1600))
            .ShouldBe((2559, 1599, new VirtualScreenRect(0, 0, 6400, 2160)));
        mixed.Place(mixed.Monitors[1], 0, 0, new VirtualScreenRect(0, 0, 1920, 1080))
            .ShouldBe((1280, 0, new VirtualScreenRect(0, 0, 3200, 1080)));
    }

    [Fact]
    public void A_point_off_the_picture_stays_on_its_monitor()
    {
        LayoutMonitor first = SideBySide.Monitors[0];

        SideBySide.Place(first, 900, -5, Picture).ShouldBe((799, 0, new VirtualScreenRect(0, 0, 1600, 600)));
    }

    /// <summary>A monitor on its side whose scanout is not: mapping it by proportion would put every click somewhere else.</summary>
    [Fact]
    public void A_picture_of_another_shape_is_not_placed()
    {
        var portrait = new DesktopLayout([new LayoutMonitor("DP-1", 0, 0, 1080, 1920)]);

        portrait.Place(portrait.Monitors[0], 10, 10, new VirtualScreenRect(0, 0, 1920, 1080)).ShouldBeNull();
        new DesktopLayout([]).Place(SideBySide.Monitors[0], 10, 10, Picture).ShouldBeNull();
    }

    /// <summary>
    /// mutter scales the virtual pointer's axes as libinput does -- (value - min) x extent / (max - min + 1) -- onto the
    /// extent of all its monitors. Every pixel of the second monitor's picture, and the first's, has to come back to
    /// itself on the desktop, the edges included.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Every_pixel_of_the_picture_lands_on_itself_as_mutter_scales_the_axes(uint monitor)
    {
        var axes = new List<(ushort Code, int Value)>();
        var injector = new UinputInputInjector(1, 2, 3, NullLogger.Instance, (_, type, code, value) =>
        {
            if (type == Uinput.EvAbs)
            {
                axes.Add((code, value));
            }
        });
        LayoutMonitor target = SideBySide.Find(15, monitor)!.Value;

        for (int x = 0; x < 800; x++)
        {
            int y = x * 599 / 799;
            (int X, int Y, VirtualScreenRect Desktop) placed = SideBySide.Place(target, x, y, Picture)!.Value;
            axes.Clear();
            injector.InjectMouse(new MouseInput(MouseAction.Move, MouseButtons.None, placed.X, placed.Y, 0), placed.Desktop);

            int onDesktopX = (int)Math.Floor(axes.Single(a => a.Code == Uinput.AbsX).Value * 1600.0 / (Uinput.AbsMax + 1));
            int onDesktopY = (int)Math.Floor(axes.Single(a => a.Code == Uinput.AbsY).Value * 600.0 / (Uinput.AbsMax + 1));
            (onDesktopX, onDesktopY).ShouldBe((target.X + x, y), $"picture pixel {x},{y}");
        }
    }
}
