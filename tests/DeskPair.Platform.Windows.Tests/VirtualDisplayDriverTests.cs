using System.Runtime.InteropServices;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Windows.Capture;
using DeskPair.Platform.Windows.Native;

namespace DeskPair.Platform.Windows.Tests;

/// <summary>
/// The parts of the virtual display driver that can be checked without installing it: the settings file it is
/// told what to do with, the structures handed to Windows, and what is recorded about an install. The rest
/// needs an administrator and a machine to add displays to (docs/unattended-windows.md has the steps).
/// </summary>
public class VirtualDisplayDriverTests
{
    /// <summary>
    /// The driver's reader is order-sensitive: a size is taken at <c>height</c> from the <c>width</c> before it, and a
    /// mode at <c>refresh_rate</c>. A document that is valid XML but in another order would give it no modes.
    /// </summary>
    [Fact]
    public void Each_size_is_written_width_then_height_then_refresh_rate()
    {
        XDocument settings = VirtualDisplayDriver.Settings(2, null, []);

        List<XElement> sizes = [.. settings.Root!.Element("resolutions")!.Elements("resolution")];
        sizes.Count.ShouldBe(VirtualDisplayDriver.CommonSizes.Length);
        foreach (XElement size in sizes)
        {
            size.Elements().Select(e => e.Name.LocalName).ShouldBe(["width", "height", "refresh_rate"]);
        }

        settings.Root.Element("monitors")!.Element("count")!.Value.ShouldBe("2");
    }

    [Fact]
    public void The_size_asked_for_comes_first_then_what_was_taught_and_nothing_twice()
    {
        XDocument settings = VirtualDisplayDriver.Settings(1, new DisplayMode(1000, 600), [new DisplayMode(2560, 1440), new DisplayMode(1234, 567), new DisplayMode(1000, 600)]);

        List<(int, int)> sizes = [.. settings.Root!.Element("resolutions")!.Elements("resolution")
            .Select(r => ((int)r.Element("width")!, (int)r.Element("height")!))];
        sizes[0].ShouldBe((1000, 600), "the first is what the driver calls the monitor's preferred mode");
        sizes[1].ShouldBe((2560, 1440));
        sizes[2].ShouldBe((1234, 567));
        sizes.Distinct().Count().ShouldBe(sizes.Count);
        sizes.ShouldContain((1920, 1080));
    }

    /// <summary>Zero reads as one to the driver: no displays is no device, never a file saying none.</summary>
    [Fact]
    public void A_file_never_asks_for_no_monitors()
    {
        VirtualDisplayDriver.Settings(0, null, []).Root!.Element("monitors")!.Element("count")!.Value.ShouldBe("1");
    }

    /// <summary>The driver logs into its settings folder, as LocalService, without limit, if anything turns it on.</summary>
    [Fact]
    public void Logging_is_off_and_every_option_is_spelled_out()
    {
        XElement options = VirtualDisplayDriver.Settings(1, null, []).Root!.Element("options")!;

        options.Element("logging")!.Value.ShouldBe("false");
        options.Element("debuglogging")!.Value.ShouldBe("false");
        options.Elements().Select(e => e.Name.LocalName).ShouldBe(
            ["CustomEdid", "PreventSpoof", "EdidCeaOverride", "HardwareCursor", "SDR10bit", "HDRPlus", "logging", "debuglogging"],
            "the names the 24.12.24 driver looks up");
    }

    [Fact]
    public void An_install_is_recorded_and_read_back()
    {
        string data = Path.Combine(Path.GetTempPath(), "deskpair-vdd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(VirtualDisplayDriver.ConfigDirectory(data));
        try
        {
            VirtualDisplayDriver.Installed(data).ShouldBeNull();

            var installed = new VirtualDisplayDriver.Installation("oem42.inf", @"ROOT\DISPLAY\0003");
            VirtualDisplayDriver.RecordInstallation(data, installed);

            VirtualDisplayDriver.Installed(data).ShouldBe(installed);
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
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
    public void The_display_device_record_is_840_bytes()
    {
        Marshal.SizeOf<User32.DISPLAY_DEVICEW>().ShouldBe(840);
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
}
