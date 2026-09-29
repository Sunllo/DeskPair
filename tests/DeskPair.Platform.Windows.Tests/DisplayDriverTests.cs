using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Windows.Capture;
using DeskPair.Platform.Windows.Native;

namespace DeskPair.Platform.Windows.Tests;

/// <summary>
/// The parts of DeskPair's display driver that can be checked without installing it: that the engine speaks the
/// protocol the driver was built with (native/idd/src/Control.h), the structures handed to Windows, the sizes a display is
/// plugged in with, and what is recorded about an install. The rest needs an administrator and a machine to add displays
/// to (docs/unattended-windows.md H has the steps).
/// </summary>
public partial class DisplayDriverTests
{
    private static string Header()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DeskPair.slnx")))
        {
            dir = dir.Parent;
        }

        dir.ShouldNotBeNull("could not find the repository root from the test output directory");
        return File.ReadAllText(Path.Combine(dir!.FullName, "native", "idd", "src", "Control.h"));
    }

    private static long Number(string text) =>
        text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.Parse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : long.Parse(text, CultureInfo.InvariantCulture);

    [Fact]
    public void The_limits_and_the_interface_are_the_drivers()
    {
        string header = Header();
        long Define(string name) => Number(Regex.Match(header, $@"#define {name} (\S+)").Groups[1].Value);

        Define("DESKPAIR_DISPLAY_PROTOCOL").ShouldBe(IddControl.Protocol);
        Define("DESKPAIR_DISPLAY_SLOTS").ShouldBe(IddControl.Slots);
        Define("DESKPAIR_DISPLAY_MAX_MODES").ShouldBe(IddControl.MaxModes);
        Define("DESKPAIR_DISPLAY_MIN_SIDE").ShouldBe(IddControl.MinSide);
        Define("DESKPAIR_DISPLAY_MAX_SIDE").ShouldBe(IddControl.MaxSide);

        Match guid = Regex.Match(header, @"DEFINE_GUID\(GUID_DEVINTERFACE_DESKPAIR_DISPLAY,\s*([^)]*)\)");
        guid.Success.ShouldBeTrue();
        long[] parts = [.. guid.Groups[1].Value.Split(',').Select(p => Number(p.Trim()))];
        parts.Length.ShouldBe(11);
        new Guid(
            (uint)parts[0], (ushort)parts[1], (ushort)parts[2],
            (byte)parts[3], (byte)parts[4], (byte)parts[5], (byte)parts[6], (byte)parts[7], (byte)parts[8], (byte)parts[9], (byte)parts[10])
            .ShouldBe(IddControl.Interface);
    }

    /// <summary>CTL_CODE(FILE_DEVICE_UNKNOWN, 0x900 + function, METHOD_BUFFERED, access), worked out from the header.</summary>
    [Fact]
    public void Every_control_code_is_the_drivers()
    {
        MatchCollection codes = IoctlDefinition().Matches(Header());
        codes.Count.ShouldBe(5);
        foreach (Match code in codes)
        {
            uint function = uint.Parse(code.Groups[2].Value, CultureInfo.InvariantCulture);
            uint access = code.Groups[3].Value == "FILE_READ_ACCESS" ? 1u : 2u;
            uint expected = (0x22u << 16) | (access << 14) | ((0x900 + function) << 2);
            FieldInfo? mirrored = typeof(IddControl).GetField(code.Groups[1].Value, BindingFlags.Public | BindingFlags.Static);
            mirrored.ShouldNotBeNull($"{code.Groups[1].Value} has no counterpart in IddControl");
            ((uint)mirrored!.GetValue(null)!).ShouldBe(expected, code.Groups[1].Value);
        }
    }

    /// <summary>Every structure field by field: same names, same order, four bytes each, nothing between them.</summary>
    [Fact]
    public void Every_structure_is_laid_out_as_the_driver_reads_it()
    {
        MatchCollection structures = StructDefinition().Matches(Header());
        structures.Count.ShouldBe(7);
        foreach (Match structure in structures)
        {
            string name = structure.Groups[1].Value;
            string[] fields = [.. FieldDefinition().Matches(structure.Groups[2].Value).Select(f => f.Groups[1].Value)];
            Type? mirrored = typeof(IddControl).GetNestedType(name, BindingFlags.Public);
            mirrored.ShouldNotBeNull($"{name} has no counterpart in IddControl");
            Marshal.SizeOf(mirrored!).ShouldBe(fields.Length * 4, name);
            for (int i = 0; i < fields.Length; i++)
            {
                Marshal.OffsetOf(mirrored!, fields[i]).ShouldBe(i * 4, $"{name}.{fields[i]}");
            }
        }
    }

    [GeneratedRegex(@"#define (IOCTL_\w+) DESKPAIR_DISPLAY_IOCTL\((\d+), (FILE_READ_ACCESS|FILE_WRITE_ACCESS)\)")]
    private static partial Regex IoctlDefinition();

    [GeneratedRegex(@"typedef struct (\w+)\s*\{([^}]*)\}", RegexOptions.Singleline)]
    private static partial Regex StructDefinition();

    [GeneratedRegex(@"^\s*(?:UINT32|INT32) (\w+);", RegexOptions.Multiline)]
    private static partial Regex FieldDefinition();

    /// <summary>Handed to Windows by pointer: a size off by one field and QueryDisplayConfig writes past the array.</summary>
    [Fact]
    public unsafe void The_display_configuration_records_have_the_layout_windows_expects()
    {
        sizeof(User32.DISPLAYCONFIG_PATH_INFO).ShouldBe(72);
        Marshal.OffsetOf<User32.DISPLAYCONFIG_PATH_INFO>(nameof(User32.DISPLAYCONFIG_PATH_INFO.targetInfo)).ShouldBe(20);
        Marshal.OffsetOf<User32.DISPLAYCONFIG_PATH_INFO>(nameof(User32.DISPLAYCONFIG_PATH_INFO.flags)).ShouldBe(68);
        sizeof(User32.DISPLAYCONFIG_MODE_INFO).ShouldBe(64);
        sizeof(User32.DISPLAYCONFIG_SOURCE_DEVICE_NAME).ShouldBe(84);
    }

    [Fact]
    public void The_display_device_record_is_840_bytes()
    {
        Marshal.SizeOf<User32.DISPLAY_DEVICEW>().ShouldBe(840);
    }

    /// <summary>Handed to setupapi by pointer: a field out of place is a device node nobody can find again.</summary>
    [Fact]
    public unsafe void The_device_record_has_the_layout_windows_expects()
    {
        if (!Environment.Is64BitProcess)
        {
            return;
        }

        sizeof(DeviceSetup.SP_DEVINFO_DATA).ShouldBe(32);
        Marshal.OffsetOf<DeviceSetup.SP_DEVINFO_DATA>(nameof(DeviceSetup.SP_DEVINFO_DATA.ClassGuid)).ShouldBe(4);
        Marshal.OffsetOf<DeviceSetup.SP_DEVINFO_DATA>(nameof(DeviceSetup.SP_DEVINFO_DATA.DevInst)).ShouldBe(20);
        Marshal.OffsetOf<DeviceSetup.SP_DEVINFO_DATA>(nameof(DeviceSetup.SP_DEVINFO_DATA.Reserved)).ShouldBe(24);
    }

    [Fact]
    public void An_install_is_recorded_and_read_back()
    {
        string data = Path.Combine(Path.GetTempPath(), "deskpair-idd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DisplayDriver.RecordDirectory(data));
        try
        {
            DisplayDriver.Installed(data).ShouldBeNull();

            var installed = new DisplayDriver.Installation("oem42.inf", @"ROOT\DISPLAY\0003", new Version(0, 4, 8, 0));
            DisplayDriver.RecordInstallation(data, installed);

            DisplayDriver.Installed(data).ShouldBe(installed);
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    [Fact]
    public void A_packages_version_is_read_from_its_inf()
    {
        string package = Path.Combine(Path.GetTempPath(), "deskpair-idd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(package);
        try
        {
            DisplayDriver.PackageVersion(package).ShouldBeNull("no package at all");

            File.WriteAllText(Path.Combine(package, "DeskPairDisplay.inf"), "[Version]\r\nClass=Display\r\nDriverVer=09/29/2026,0.4.8.2\r\n");
            DisplayDriver.PackageVersion(package).ShouldBe(new Version(0, 4, 8, 2));

            File.WriteAllText(Path.Combine(package, "DeskPairDisplay.inf"), "[Version]\r\nDriverVer=$DRIVERVER$\r\n");
            DisplayDriver.PackageVersion(package).ShouldBeNull("the source INF, before the build fills it in");
        }
        finally
        {
            Directory.Delete(package, recursive: true);
        }
    }

    /// <summary>What a viewer is told when this engine cannot add displays, before anything is tried.</summary>
    [Fact]
    public void Without_administrator_rights_it_says_the_service_is_needed()
    {
        if (Environment.IsPrivilegedProcess)
        {
            return; // an elevated test run has the rights; the other answer is the driver's
        }

        using var displays = new WindowsVirtualDisplays(Path.GetTempPath(), NullLogger.Instance);

        displays.UnavailableReason.ShouldNotBeNull().ShouldContain("service");
        displays.Count.ShouldBe(0);
    }

    // ---- the sizes a display is plugged in with ----

    private static readonly DisplayMode Screen = new(1920, 1080);
    private static readonly DisplayMode WorkArea = new(1920, 1032);

    [Fact]
    public void The_size_asked_for_comes_first_then_the_others_named_and_nothing_twice()
    {
        List<DisplayMode> sizes = VirtualDisplaySizes.For(Screen, [WorkArea, new DisplayMode(2560, 1440), WorkArea]);

        sizes[0].ShouldBe(Screen, "what the display is plugged in at: index 0");
        sizes[1].ShouldBe(WorkArea);
        sizes[2].ShouldBe(new DisplayMode(2560, 1440));
        sizes.Distinct().Count().ShouldBe(sizes.Count);
        sizes.Count.ShouldBe(IddControl.MaxModes, "as many as a display may have");
    }

    [Fact]
    public void Every_size_is_even_and_in_range()
    {
        List<DisplayMode> sizes = VirtualDisplaySizes.For(new DisplayMode(1537, 863), [new DisplayMode(100, 50), new DisplayMode(9000, 5000)]);

        sizes[0].ShouldBe(new DisplayMode(1536, 862), "an encoder works in pairs of pixels");
        sizes[1].ShouldBe(new DisplayMode(IddControl.MinSide, IddControl.MinSide));
        sizes[2].ShouldBe(new DisplayMode(VirtualDisplaySizes.MaxSide, VirtualDisplaySizes.MaxSide));
        sizes.ShouldAllBe(s => s.Width % 2 == 0 && s.Height % 2 == 0);
        sizes.ShouldAllBe(s => s.Width >= IddControl.MinSide && s.Height >= IddControl.MinSide && s.Width <= VirtualDisplaySizes.MaxSide && s.Height <= VirtualDisplaySizes.MaxSide);
    }

    [Fact]
    public void The_common_sizes_are_there_for_the_menu()
    {
        List<DisplayMode> sizes = VirtualDisplaySizes.For(Screen, [WorkArea]);

        foreach ((int width, int height) in VirtualDisplaySizes.CommonSizes)
        {
            sizes.ShouldContain(new DisplayMode(width, height));
        }
    }

    /// <summary>No window of the viewer's is larger than the largest size it named, so nothing larger is made up.</summary>
    [Fact]
    public void Nothing_is_made_up_larger_than_the_largest_size_named()
    {
        List<DisplayMode> sizes = VirtualDisplaySizes.For(Screen, [WorkArea]);
        HashSet<(int, int)> common = [.. VirtualDisplaySizes.CommonSizes];

        sizes.Where(s => !common.Contains((s.Width, s.Height))).ShouldAllBe(s => s.Width <= 1920 && s.Height <= 1080);
    }

    /// <summary>
    /// The window a viewer maximises on a 1920x1080 screen loses a taskbar, a title bar and a toolbar -- how tall depends on
    /// the screen's scale -- and perhaps a border: for any such window there is a size less than a step smaller.
    /// </summary>
    [Fact]
    public void A_maximised_window_is_filled_within_a_step()
    {
        List<DisplayMode> sizes = VirtualDisplaySizes.For(Screen, [WorkArea]);

        for (int width = 1920 - 32; width <= 1920; width += 2)
        {
            for (int height = 1032 - 150; height <= 1032; height++)
            {
                DisplayMode best = sizes.Where(s => s.Width <= width && s.Height <= height).MaxBy(s => (long)s.Width * s.Height);
                (width - best.Width).ShouldBeLessThan(VirtualDisplaySizes.Step, $"{width}x{height}");
                (height - best.Height).ShouldBeLessThan(VirtualDisplaySizes.Step, $"{width}x{height}");
            }
        }
    }

    /// <summary>
    /// A viewer with a laptop at 200 % (2880x1800) and a 1920x1080 screen beside it, asking from the laptop: the display
    /// may end up in a window maximised on either, whose title bar and toolbar take twice as many pixels on the laptop.
    /// </summary>
    [Theory]
    [InlineData(2880, 1752, 150)]
    [InlineData(1920, 1032, 100)]
    public void A_window_maximised_on_any_of_the_viewers_screens_is_filled_within_a_step(int workWidth, int workHeight, int chrome)
    {
        List<DisplayMode> sizes = VirtualDisplaySizes.For(
            new DisplayMode(2880, 1800), [new(2880, 1800), new(2880, 1752), new(1920, 1080), new(1920, 1032)]);

        for (int width = workWidth - 16; width <= workWidth; width += 2)
        {
            for (int height = workHeight - chrome; height <= workHeight; height++)
            {
                DisplayMode best = sizes.Where(s => s.Width <= width && s.Height <= height).MaxBy(s => (long)s.Width * s.Height);
                (width - best.Width).ShouldBeLessThan(VirtualDisplaySizes.Step, $"{width}x{height}");
                (height - best.Height).ShouldBeLessThan(VirtualDisplaySizes.Step, $"{width}x{height}");
            }
        }
    }

    /// <summary>The window the display was asked for, dragged a little larger or smaller either way.</summary>
    [Fact]
    public void A_window_dragged_a_little_from_the_size_asked_for_is_filled_within_a_step()
    {
        List<DisplayMode> sizes = VirtualDisplaySizes.For(new DisplayMode(1600, 900), [new(1920, 1080), new(1920, 1032)]);

        for (int width = 1600 - 64; width <= 1600 + 64; width += 2)
        {
            for (int height = 900 - 64; height <= 900 + 64; height += 2)
            {
                DisplayMode best = sizes.Where(s => s.Width <= width && s.Height <= height).MaxBy(s => (long)s.Width * s.Height);
                (width - best.Width).ShouldBeLessThan(VirtualDisplaySizes.Step, $"{width}x{height}");
                (height - best.Height).ShouldBeLessThan(VirtualDisplaySizes.Step, $"{width}x{height}");
            }
        }
    }

    [Fact]
    public void However_many_are_named_a_display_gets_no_more_than_it_may_have()
    {
        List<DisplayMode> named = [.. Enumerable.Range(0, 300).Select(i => new DisplayMode(800 + 2 * i, 600))];

        List<DisplayMode> sizes = VirtualDisplaySizes.For(Screen, named);

        sizes.Count.ShouldBe(IddControl.MaxModes);
        sizes[0].ShouldBe(Screen);
    }

    /// <summary>A thin display is the one that fills slowest: the loop that makes up its sizes has to end all the same.</summary>
    [Fact]
    public void A_display_with_little_room_to_make_sizes_up_in_still_gets_its_list()
    {
        List<DisplayMode> sizes = VirtualDisplaySizes.For(new DisplayMode(4096, 320), []);

        sizes[0].ShouldBe(new DisplayMode(4096, 320));
        sizes.Count.ShouldBeGreaterThan(VirtualDisplaySizes.CommonSizes.Length);
        sizes.Count.ShouldBeLessThanOrEqualTo(IddControl.MaxModes);
    }
}
