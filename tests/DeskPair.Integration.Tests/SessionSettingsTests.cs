using DeskPair.Core.Config;
using DeskPair.Core.Session.Controller;
using DeskPair.Core.Session.Host;
using DeskPair.Core.Transport;
using DeskPair.Protocol.Messages;

namespace DeskPair.Integration.Tests;

/// <summary>What the settings screen sends: session options mid-call, and host settings saved while connected.</summary>
public class SessionSettingsTests
{
    private static async Task<ControllerSession> ConnectAsync(Testbed bed, HostRuntime host, string password, SessionOptions? options = null)
    {
        (ControllerSession session, _, PeerConnector connector) = bed.CreateController(options: options);
        await session.ConnectAsync(connector, host.Identity.Id, CancellationToken.None);
        (await session.LoginAsync(password, CancellationToken.None)).Success.ShouldBeTrue();
        return session;
    }

    [Fact]
    public async Task Quality_chosen_during_a_session_reaches_the_encoder()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true);
        ControllerSession session = await ConnectAsync(bed, host, passwords.TemporaryPassword);
        int connection = host.Sessions.Single().Context.ConnectionId;

        await Testbed.WaitUntilAsync(() => bed.Media!.Qos.TargetBitrateKbps(640, 360) > 0, "video running");

        await session.SetOptionsAsync(new SessionOptions
        {
            ImageQuality = ImageQuality.IqCustom,
            CustomBitrateKbps = 900,
            CustomFps = 15,
        });

        // Before this was wired up the host stored the options and ignored them for the rest of the session.
        await Testbed.WaitUntilAsync(() => bed.Media!.Qos.TargetBitrateKbps(640, 360) == 900, "the custom bitrate reached QoS");
        await Testbed.WaitUntilAsync(() => bed.Media!.Qos.Fps <= 15, "the custom frame rate reached QoS");

        // Waited for, not looked at once: the options can arrive before the first round trip is measured (on a busy
        // Windows runner, once). A viewer the new options had dropped from QoS would never get one.
        await Testbed.WaitUntilAsync(() => bed.Media!.Qos.LastRoundTrip(connection) is not null, "a round trip measured");

        await session.CloseAsync("done");
    }

    /// <summary>
    /// The desktop's toolbar sends a frame rate with whatever quality is chosen; the host caps its frame rate at it
    /// without the custom quality's fixed bitrate coming along.
    /// </summary>
    [Fact]
    public async Task A_frame_rate_chosen_during_a_session_reaches_the_encoder_with_any_quality()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true);
        ControllerSession session = await ConnectAsync(bed, host, passwords.TemporaryPassword);

        await Testbed.WaitUntilAsync(() => bed.Media!.Qos.TargetBitrateKbps(640, 360) > 0, "video running");

        // An odd bitrate, so the balanced table cannot land on it by chance.
        await session.SetOptionsAsync(new SessionOptions { ImageQuality = ImageQuality.IqBalanced, CustomFps = 15, CustomBitrateKbps = 777 });

        await Testbed.WaitUntilAsync(() => bed.Media!.Qos.Fps <= 15, "the frame rate reached QoS");
        bed.Media!.Qos.TargetBitrateKbps(640, 360).ShouldNotBe(777, "a bitrate is only fixed by the custom quality");

        await session.CloseAsync("done");
    }

    [Fact]
    public async Task The_desk_locks_after_the_session_only_when_the_viewer_asked()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true);

        ControllerSession plain = await ConnectAsync(bed, host, passwords.TemporaryPassword);
        await Testbed.WaitUntilAsync(() => host.Sessions.Count == 1, "session authorized");
        await plain.CloseAsync("done");
        await Testbed.WaitUntilAsync(() => host.Sessions.Count == 0, "session closed");
        bed.Injector!.LockCalls.ShouldBe(0);

        ControllerSession locking = await ConnectAsync(bed, host, passwords.TemporaryPassword,
            new SessionOptions { LockAfterSessionEnd = BoolOption.BoYes });
        await Testbed.WaitUntilAsync(() => host.Sessions.Count == 1, "second session authorized");
        await locking.CloseAsync("done");
        await Testbed.WaitUntilAsync(() => bed.Injector!.LockCalls == 1, "the desk was locked");
    }

    [Fact]
    public async Task Host_settings_saved_during_a_session_take_effect_without_a_restart()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true);
        ControllerSession session = await ConnectAsync(bed, host, passwords.TemporaryPassword);
        HostSession hostSession = host.Sessions.Single();
        hostSession.Context.Permissions.Has(Permission.PermKeyboard).ShouldBeTrue();

        // This is what the settings screen does over IPC: save the file, then the service applies the policy.
        var saved = new HostConfig { KeyboardEnabled = false, ClipboardEnabled = false };
        host.Policy = saved.ToPolicy();

        hostSession.Context.Permissions.Has(Permission.PermKeyboard).ShouldBeFalse();
        hostSession.Context.Permissions.Has(Permission.PermClipboard).ShouldBeFalse();
        hostSession.Context.Permissions.Has(Permission.PermAudio).ShouldBeTrue();

        host.Policy = new HostConfig().ToPolicy();
        hostSession.Context.Permissions.Has(Permission.PermKeyboard).ShouldBeTrue();

        await session.CloseAsync("done");
    }
}
