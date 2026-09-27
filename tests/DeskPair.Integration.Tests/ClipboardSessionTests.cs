using DeskPair.Core.Session.Controller;
using DeskPair.Core.Session.Host;
using DeskPair.Core.Testing;
using DeskPair.Core.Transport;
using DeskPair.Protocol.Messages;

namespace DeskPair.Integration.Tests;

public class ClipboardSessionTests
{
    [Fact]
    public async Task Clipboard_syncs_both_ways_without_echo()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true);
        bed.HostClipboard!.SetText("from host, before connect");
        var controllerClipboard = new FakeClipboard();
        (ControllerSession session, _, PeerConnector connector) = bed.CreateController(clipboard: controllerClipboard);
        await session.ConnectAsync(connector, host.Identity.Id, CancellationToken.None);
        (await session.LoginAsync(passwords.TemporaryPassword, CancellationToken.None)).Success.ShouldBeTrue();

        // Snapshot on subscribe brings the controller up to date.
        await Testbed.WaitUntilAsync(() => controllerClipboard.Text == "from host, before connect", "initial clipboard");

        bed.HostClipboard.SetText("host copied");
        await Testbed.WaitUntilAsync(() => controllerClipboard.Text == "host copied", "host -> controller");

        controllerClipboard.SetText("controller copied");
        await Testbed.WaitUntilAsync(() => bed.HostClipboard.Text == "controller copied", "controller -> host");

        // Give any echo a chance to bounce; neither side should have written again.
        await Task.Delay(500);
        controllerClipboard.Writes.ShouldBe(2);
        bed.HostClipboard.Writes.ShouldBe(1);
    }

    [Fact]
    public async Task Clipboard_is_not_synced_without_permission()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(new HostPolicy { ClipboardEnabled = false }, media: true);
        var controllerClipboard = new FakeClipboard();
        (ControllerSession session, TestCallbacks cb, PeerConnector connector) = bed.CreateController(clipboard: controllerClipboard);
        await session.ConnectAsync(connector, host.Identity.Id, CancellationToken.None);
        (await session.LoginAsync(passwords.TemporaryPassword, CancellationToken.None)).Success.ShouldBeTrue();
        await Testbed.WaitUntilAsync(() => cb.Permissions.Any(p => p.Permission == Permission.PermClipboard && !p.Enabled), "clipboard permission off");

        bed.HostClipboard!.SetText("secret");
        controllerClipboard.SetText("mine");
        await session.SendPingAsync();
        await Testbed.WaitUntilAsync(() => cb.LastRtt is not null, "round trip");
        await Task.Delay(300);
        controllerClipboard.Text.ShouldBe("mine");
        bed.HostClipboard.Text.ShouldBe("secret");
        bed.HostClipboard.Writes.ShouldBe(0);
    }
}
