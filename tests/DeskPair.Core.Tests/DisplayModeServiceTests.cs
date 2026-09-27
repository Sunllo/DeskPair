using DeskPair.Core.Services;
using DeskPair.Core.Testing;
using DeskPair.Platform.Abstractions.Capture;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace DeskPair.Core.Tests;

/// <summary>
/// A viewer may put the host's screen in another mode; what matters is that the screen goes back to how
/// its owner left it, whoever changed it and however many times.
/// </summary>
public class DisplayModeServiceTests
{
    private static (DisplayModeService Service, FakeDisplayEnumerator Displays, FakeDisplayModes Modes) Create(params DisplayMode[] modes)
    {
        var displays = new FakeDisplayEnumerator(1920, 1080);
        var switcher = new FakeDisplayModes(displays, modes.Length > 0 ? modes : [new DisplayMode(1920, 1080), new DisplayMode(1280, 720), new DisplayMode(1024, 768)]);
        var service = new DisplayModeService(displays, switcher, new FakeTimeProvider(), NullLogger.Instance);
        return (service, displays, switcher);
    }

    [Fact]
    public async Task A_mode_the_display_advertises_is_applied_and_the_original_remembered()
    {
        (DisplayModeService service, FakeDisplayEnumerator displays, FakeDisplayModes modes) = Create();

        (await service.ChangeAsync(0, new DisplayMode(1280, 720), CancellationToken.None)).ShouldBeNull();

        displays.Displays[0].Width.ShouldBe(1280);
        service.OriginalFor("FAKE0").ShouldBe(new DisplayMode(1920, 1080, 1.0));
        service.HasChanges.ShouldBeTrue();
        modes.Calls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task The_original_is_recorded_once_however_many_changes_follow()
    {
        (DisplayModeService service, _, _) = Create();

        await service.ChangeAsync(0, new DisplayMode(1280, 720), CancellationToken.None);
        await service.ChangeAsync(0, new DisplayMode(1024, 768), CancellationToken.None);

        service.OriginalFor("FAKE0").ShouldBe(new DisplayMode(1920, 1080, 1.0), "the mode before anybody connected, not the one before the last change");
    }

    [Fact]
    public async Task A_size_the_display_did_not_advertise_is_refused_and_nothing_is_recorded()
    {
        (DisplayModeService service, _, FakeDisplayModes modes) = Create();

        string? refused = await service.ChangeAsync(0, new DisplayMode(1234, 777), CancellationToken.None);

        refused.ShouldNotBeNull();
        refused.ShouldContain("1234x777");
        modes.Calls.ShouldBeEmpty();
        service.HasChanges.ShouldBeFalse();
    }

    [Fact]
    public async Task A_platform_refusal_is_reported_and_leaves_no_original_behind()
    {
        (DisplayModeService service, FakeDisplayEnumerator displays, FakeDisplayModes modes) = Create();
        modes.Refuse = "the monitor cannot show that";

        (await service.ChangeAsync(0, new DisplayMode(1280, 720), CancellationToken.None)).ShouldBe("the monitor cannot show that");

        displays.Displays[0].Width.ShouldBe(1920);
        service.HasChanges.ShouldBeFalse();
    }

    [Fact]
    public async Task Restoring_puts_every_display_back_and_forgets_it()
    {
        (DisplayModeService service, FakeDisplayEnumerator displays, _) = Create();
        await service.ChangeAsync(0, new DisplayMode(1024, 768), CancellationToken.None);

        await service.RestoreAllAsync();

        displays.Displays[0].Width.ShouldBe(1920);
        displays.Displays[0].Height.ShouldBe(1080);
        service.HasChanges.ShouldBeFalse();
        service.OriginalFor("FAKE0").ShouldBeNull();
    }

    [Fact]
    public async Task Asking_for_the_original_restores_it_and_asking_with_nothing_changed_is_a_no_op()
    {
        (DisplayModeService service, FakeDisplayEnumerator displays, FakeDisplayModes modes) = Create();

        (await service.ChangeAsync(0, null, CancellationToken.None)).ShouldBeNull("nothing to restore is not a failure");
        modes.Calls.ShouldBeEmpty();

        await service.ChangeAsync(0, new DisplayMode(1280, 720), CancellationToken.None);
        (await service.ChangeAsync(0, null, CancellationToken.None)).ShouldBeNull();

        displays.Displays[0].Width.ShouldBe(1920);
        service.HasChanges.ShouldBeFalse();
    }

    [Fact]
    public async Task Choosing_the_original_size_by_hand_also_forgets_the_change()
    {
        (DisplayModeService service, _, _) = Create();
        await service.ChangeAsync(0, new DisplayMode(1280, 720), CancellationToken.None);

        await service.ChangeAsync(0, new DisplayMode(1920, 1080), CancellationToken.None);

        service.HasChanges.ShouldBeFalse();
    }

    [Fact]
    public async Task Without_a_switcher_every_request_is_refused_and_no_modes_are_advertised()
    {
        var displays = new FakeDisplayEnumerator();
        var service = new DisplayModeService(displays, null, new FakeTimeProvider(), NullLogger.Instance);

        service.ModesFor(displays.Displays[0]).ShouldBeEmpty();
        (await service.ChangeAsync(0, new DisplayMode(320, 180), CancellationToken.None)).ShouldContain("cannot change");
    }

    [Fact]
    public async Task A_display_that_is_not_there_is_refused()
    {
        (DisplayModeService service, _, _) = Create();
        (await service.ChangeAsync(5, new DisplayMode(1280, 720), CancellationToken.None)).ShouldContain("not there");
    }
}
