using DeskPair.Desktop.Services;

namespace DeskPair.Desktop.Tests;

/// <summary>
/// That a setting somebody changed is still changed after a restart.
///
/// There were no tests for this file at all, and it had a bug that every one of its users would have
/// noticed and nobody would have reported as a bug: turning off a setting that defaults to on did nothing
/// beyond the current run.
/// </summary>
public class DesktopConfigTests
{
    private static DesktopConfig RoundTrip(DesktopConfig config) =>
        DesktopConfig.FromJson(config.ToJson());

    [Fact]
    public void Turning_off_a_setting_that_defaults_to_on_survives_a_restart()
    {
        // The bug this exists for: the serializer omitted any value equal to default(T), "default" for a
        // bool is false, and loading overlaid the stored file onto a fresh instance where these were true.
        // Every one of them came back on by itself.
        var off = new DesktopConfig
        {
            CloseToTray = false,
            UdpMedia = false,
            FitToWindow = false,
            LosslessRefinement = false,
            RecordAudio = false,
        };

        DesktopConfig read = RoundTrip(off);

        read.CloseToTray.ShouldBeFalse();
        read.UdpMedia.ShouldBeFalse();
        read.FitToWindow.ShouldBeFalse();
        read.LosslessRefinement.ShouldBeFalse();
        read.RecordAudio.ShouldBeFalse();
    }

    [Fact]
    public void Turning_on_a_setting_that_defaults_to_off_survives_a_restart()
    {
        DesktopConfig read = RoundTrip(new DesktopConfig { ForceRelay = true, AudioEnabled = true });

        read.ForceRelay.ShouldBeTrue();
        read.AudioEnabled.ShouldBeTrue();
    }

    [Fact]
    public void The_keys_are_the_property_names()
    {
        // No naming policy is set, and FromJson overlays by exact key. A file using camelCase would be
        // read as if it said nothing at all, so the casing is worth pinning rather than assuming.
        new DesktopConfig().ToJson().ShouldContain("\"CloseToTray\"");
    }

    [Fact]
    public void A_file_written_before_a_setting_existed_keeps_that_setting_s_default()
    {
        // The reason FromJson overlays rather than deserialises: an old file must not read as "off" for
        // every flag added since.
        DesktopConfig read = DesktopConfig.FromJson("""{ "CloseToTray": false }""");

        read.CloseToTray.ShouldBeFalse();
        read.UdpMedia.ShouldBeTrue();
        read.FitToWindow.ShouldBeTrue();
        read.RecentLimit.ShouldBe(new DesktopConfig().RecentLimit);
    }

    [Fact]
    public void A_hand_edited_file_with_nulls_does_not_produce_null_strings()
    {
        DesktopConfig read = DesktopConfig.FromJson("""{ "RendezvousServer": null, "PortalServer": null }""");

        read.RendezvousServer.ShouldBe(string.Empty);
        read.PortalServer.ShouldBe(string.Empty);
    }

    [Fact]
    public void Something_that_is_not_an_object_reads_as_the_defaults()
    {
        DesktopConfig.FromJson("[]").CloseToTray.ShouldBeTrue();
        DesktopConfig.FromJson("\"nonsense\"").CloseToTray.ShouldBeTrue();
    }

    [Fact]
    public void The_recent_list_round_trips_with_what_each_peer_was_running()
    {
        DesktopConfig read = RoundTrip(new DesktopConfig().WithRecent("123456789", "Office PC", "windows"));

        RecentPeer peer = read.Recent.ShouldHaveSingleItem();
        peer.Id.ShouldBe("123456789");
        peer.Name.ShouldBe("Office PC");
        peer.Platform.ShouldBe("windows");
    }

    [Fact]
    public void A_peer_that_reports_no_platform_keeps_the_one_it_last_gave()
    {
        // So a logo does not come and go between connections.
        DesktopConfig first = new DesktopConfig().WithRecent("123456789", "Office PC", "windows");
        DesktopConfig again = RoundTrip(first).WithRecent("123456789", "Office PC");

        again.Recent.ShouldHaveSingleItem().Platform.ShouldBe("windows");
    }

    /// <summary>A resolution chosen for a remote display comes back after a restart; choosing "original" forgets it.</summary>
    [Fact]
    public void A_chosen_resolution_is_remembered_per_display_and_forgotten_on_original()
    {
        DesktopConfig config = new DesktopConfig()
            .WithPeerResolution("123456789", "DISPLAY1", new PeerResolution("", "", 1920, 1080))
            .WithPeerResolution("123456789", "DISPLAY2", new PeerResolution("", "", 1280, 720, 2));

        DesktopConfig read = RoundTrip(config);
        read.ResolutionFor("123456789", "DISPLAY1").ShouldBe(new PeerResolution("123456789", "DISPLAY1", 1920, 1080));
        read.ResolutionFor("123456789", "DISPLAY2")!.Scale.ShouldBe(2);
        read.ResolutionFor("987654321", "DISPLAY1").ShouldBeNull();

        DesktopConfig replaced = read.WithPeerResolution("123456789", "DISPLAY1", new PeerResolution("", "", 1600, 900));
        replaced.PeerResolutions.Count(r => r.Display == "DISPLAY1").ShouldBe(1, "one entry per display");
        replaced.ResolutionFor("123456789", "DISPLAY1")!.Width.ShouldBe(1600);

        DesktopConfig forgotten = replaced.WithPeerResolution("123456789", "DISPLAY1", null);
        forgotten.ResolutionFor("123456789", "DISPLAY1").ShouldBeNull();
        forgotten.ResolutionFor("123456789", "DISPLAY2").ShouldNotBeNull("the other display keeps its choice");
    }
}
