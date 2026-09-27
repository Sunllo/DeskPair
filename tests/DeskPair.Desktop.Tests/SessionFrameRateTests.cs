using Avalonia.Headless.XUnit;
using Microsoft.Extensions.Logging.Abstractions;
using DeskPair.Desktop.Services;
using DeskPair.Desktop.ViewModels;
using DeskPair.Protocol.Messages;

namespace DeskPair.Desktop.Tests;

/// <summary>
/// What a session tells the host about frame rate: the toolbar's choice with any quality, and a fixed bitrate only
/// with the custom quality -- a lower frame rate used to be reachable only through the custom quality, which also
/// fixed the bitrate, and only from the settings page, before connecting.
/// </summary>
public class SessionFrameRateTests
{
    private static RemoteSessionViewModel Session(DesktopConfig config) =>
        new("123456789", "me", config, NullLoggerFactory.Instance);

    [AvaloniaFact]
    public void The_frame_rate_goes_to_the_host_with_any_quality()
    {
        RemoteSessionViewModel vm = Session(new DesktopConfig());
        vm.FrameRate.ShouldBe(0, "automatic, unless the settings page's custom quality says otherwise");
        vm.BuildOptions().CustomFps.ShouldBe(0);

        vm.FrameRateIndex = vm.FrameRates.IndexOf("30");
        SessionOptions balanced = vm.BuildOptions();
        balanced.ImageQuality.ShouldBe(ImageQuality.IqBalanced);
        balanced.CustomFps.ShouldBe(30);
        balanced.CustomBitrateKbps.ShouldBe(0, "a frame rate does not fix the bitrate");

        vm.QualityIndex = 3;
        SessionOptions custom = vm.BuildOptions();
        custom.ImageQuality.ShouldBe(ImageQuality.IqCustom);
        custom.CustomBitrateKbps.ShouldBe(new DesktopConfig().CustomBitrateKbps);
        custom.CustomFps.ShouldBe(30);
    }

    /// <summary>The toolbar has the custom quality now, so a session that starts with it shows it rather than falling back.</summary>
    [AvaloniaFact]
    public void A_session_started_with_the_custom_quality_shows_it_and_its_frame_rate()
    {
        RemoteSessionViewModel vm = Session(new DesktopConfig { DefaultQuality = "custom", CustomFps = 20 });

        vm.QualityIndex.ShouldBe(3);
        vm.FrameRate.ShouldBe(20);
        vm.FrameRates[vm.FrameRateIndex].ShouldBe("20");
        vm.BuildOptions().CustomFps.ShouldBe(20);
    }
}
