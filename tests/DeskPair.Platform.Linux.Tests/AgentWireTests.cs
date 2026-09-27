using System.Buffers.Binary;
using DeskPair.Platform.Linux.Wayland;
using DeskPair.Platform.Linux.Wayland.Agent;

namespace DeskPair.Platform.Linux.Tests;

/// <summary>
/// The messages between the unattended engine and the session agent survive the trip, and one that was not written
/// by this code is refused rather than half read: the agent runs as the signed-in user, so the engine reads it as it
/// would read anybody.
/// </summary>
public class AgentWireTests
{
    private static readonly PortalStream Left = new(62, "0", 0, 0, 800, 600, true, 1, null);
    private static readonly PortalStream Right = new(58, "1", 800, 0, 800, 600, true, 1, "map-7");

    [Fact]
    public void Hello_round_trips()
    {
        byte[] buffer = new byte[AgentWire.MaxMessage];
        int n = AgentWire.WriteHello(buffer, new AgentHello(1000, 4242, "ubuntu:GNOME", "0.3.1+abc"));

        AgentWire.TryReadHeader(buffer.AsSpan(0, n), out byte kind, out _, out uint id).ShouldBeTrue();
        kind.ShouldBe(AgentWire.KindHello);
        id.ShouldBe(0u);
        AgentWire.TryReadHello(buffer.AsSpan(0, n), out AgentHello hello).ShouldBeTrue();
        hello.ShouldBe(new AgentHello(1000, 4242, "ubuntu:GNOME", "0.3.1+abc"));
    }

    [Fact]
    public void Open_carries_the_token_and_whether_to_offer_it()
    {
        byte[] buffer = new byte[AgentWire.MaxMessage];
        int n = AgentWire.WriteOpen(buffer, 7, "3f1c6a84-3f1b-4c7e-9a3a-0c2d6c1a9e11", offerRestoreToken: true);
        AgentWire.TryReadHeader(buffer.AsSpan(0, n), out _, out _, out uint id).ShouldBeTrue();
        id.ShouldBe(7u);
        AgentWire.TryReadOpen(buffer.AsSpan(0, n), out string? token, out bool offer).ShouldBeTrue();
        (token, offer).ShouldBe(("3f1c6a84-3f1b-4c7e-9a3a-0c2d6c1a9e11", true));

        n = AgentWire.WriteOpen(buffer, 8, null, offerRestoreToken: false);
        AgentWire.TryReadOpen(buffer.AsSpan(0, n), out token, out offer).ShouldBeTrue();
        (token, offer).ShouldBe(((string?)null, false));
    }

    [Fact]
    public void Opened_round_trips_every_stream()
    {
        byte[] buffer = new byte[AgentWire.MaxMessage];
        var sent = new AgentOpened(3, true, false, TimeSpan.FromMilliseconds(9), "next-token", [Left, Right, new PortalStream(70, null, 0, 0, 1280, 800, false, 1, null)]);
        int n = AgentWire.WriteOpened(buffer, 7, sent);

        AgentWire.TryReadOpened(buffer.AsSpan(0, n), out AgentOpened read).ShouldBeTrue();
        read.Devices.ShouldBe(3u);
        read.RemoteControl.ShouldBeTrue();
        read.Clipboard.ShouldBeFalse();
        read.StartTook.ShouldBe(TimeSpan.FromMilliseconds(9));
        read.Token.ShouldBe("next-token");
        read.Streams.ShouldBe(sent.Streams);
    }

    [Fact]
    public void A_failure_keeps_its_reason_and_an_unknown_one_reads_as_failed()
    {
        byte[] buffer = new byte[AgentWire.MaxMessage];
        int n = AgentWire.WriteFailed(buffer, 3, PortalFailure.Refused, "The person at the machine said no.");
        AgentWire.TryReadFailed(buffer.AsSpan(0, n), out PortalFailure reason, out string text).ShouldBeTrue();
        (reason, text).ShouldBe((PortalFailure.Refused, "The person at the machine said no."));

        buffer[AgentWire.HeaderSize] = 200;
        AgentWire.TryReadFailed(buffer.AsSpan(0, n), out reason, out _).ShouldBeTrue();
        reason.ShouldBe(PortalFailure.Failed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Every_kind_of_input_round_trips(int kind)
    {
        byte[] buffer = new byte[64];
        var e = new PortalSession.InputEvent((PortalSession.InputKind)kind, 1, -3, 410.5, 310.25);
        int n = AgentWire.WriteInput(buffer, e);

        AgentWire.TryReadInput(buffer.AsSpan(0, n), out PortalSession.InputEvent read).ShouldBeTrue();
        read.ShouldBe(e);
    }

    [Fact]
    public void Input_of_no_known_kind_or_with_no_real_position_is_refused()
    {
        byte[] buffer = new byte[64];
        int n = AgentWire.WriteInput(buffer, new PortalSession.InputEvent(PortalSession.InputKind.MotionAbsolute, 55, 0, double.NaN, 0));
        AgentWire.TryReadInput(buffer.AsSpan(0, n), out _).ShouldBeFalse("a NaN position is not a place on a screen");

        n = AgentWire.WriteInput(buffer, new PortalSession.InputEvent(PortalSession.InputKind.Keycode, 1, 30, 0, 0));
        buffer[AgentWire.HeaderSize] = 99;
        AgentWire.TryReadInput(buffer.AsSpan(0, n), out _).ShouldBeFalse();
    }

    [Fact]
    public void A_short_a_long_or_a_foreign_message_is_refused()
    {
        byte[] buffer = new byte[AgentWire.MaxMessage];
        int n = AgentWire.WriteOpened(buffer, 7, new AgentOpened(3, true, false, TimeSpan.Zero, null, [Left, Right]));

        AgentWire.TryReadOpened(buffer.AsSpan(0, n - 1), out _).ShouldBeFalse("cut short");
        AgentWire.TryReadOpened(buffer.AsSpan(0, n + 1), out _).ShouldBeFalse("with something after it");

        byte version = buffer[0];
        buffer[0] = (byte)(version + 1);
        AgentWire.TryReadHeader(buffer.AsSpan(0, n), out _, out _, out _).ShouldBeFalse("another version");
        buffer[0] = version;
    }

    [Fact]
    public void A_list_of_more_streams_than_anyone_has_is_not_believed()
    {
        byte[] buffer = new byte[AgentWire.MaxMessage];
        int n = AgentWire.WriteOpened(buffer, 7, new AgentOpened(3, true, false, TimeSpan.Zero, null, []));
        buffer[n - 1] = AgentWire.MaxStreams + 1;

        AgentWire.TryReadOpened(buffer.AsSpan(0, n), out _).ShouldBeFalse();
    }

    [Fact]
    public void A_string_longer_than_the_limit_is_cut_when_written_and_refused_when_read()
    {
        byte[] buffer = new byte[AgentWire.MaxMessage];
        int n = AgentWire.WriteClosed(buffer, new string('中', AgentWire.MaxString));
        AgentWire.TryReadClosed(buffer.AsSpan(0, n), out string reason).ShouldBeTrue();
        System.Text.Encoding.UTF8.GetByteCount(reason).ShouldBeLessThanOrEqualTo(AgentWire.MaxString);

        // A length field claiming more than the limit, with the bytes to back it: still no.
        byte[] forged = new byte[AgentWire.HeaderSize + 2 + AgentWire.MaxString + 1];
        AgentWire.WriteClosed(forged, string.Empty);
        BinaryPrimitives.WriteUInt16LittleEndian(forged.AsSpan(AgentWire.HeaderSize), AgentWire.MaxString + 1);
        AgentWire.TryReadClosed(forged, out _).ShouldBeFalse();
    }

    [Fact]
    public void A_layout_round_trips_every_monitor()
    {
        byte[] buffer = new byte[AgentWire.MaxMessage];
        var sent = new DesktopLayout([new LayoutMonitor("Virtual-1", 0, 0, 800, 600), new LayoutMonitor("HDMI-2", -1920, 120, 1920, 1080)]);
        int n = AgentWire.WriteLayout(buffer, sent);

        AgentWire.TryReadHeader(buffer.AsSpan(0, n), out byte kind, out _, out uint id).ShouldBeTrue();
        (kind, id).ShouldBe((AgentWire.KindLayout, 0u));
        AgentWire.TryReadLayout(buffer.AsSpan(0, n), out DesktopLayout read).ShouldBeTrue();
        read.Monitors.ShouldBe(sent.Monitors);

        n = AgentWire.WriteLayout(buffer, new DesktopLayout([]));
        AgentWire.TryReadLayout(buffer.AsSpan(0, n), out read).ShouldBeTrue();
        read.Monitors.ShouldBeEmpty();
    }

    /// <summary>The agent is the user's; a layout that no desktop has is refused whole rather than used to place clicks.</summary>
    [Theory]
    [InlineData("", 0, 0, 800, 600)]
    [InlineData("Virtual-1", 0, 0, 0, 600)]
    [InlineData("Virtual-1", 0, 0, 800, -1)]
    [InlineData("Virtual-1", 0, 0, AgentWire.MaxLayoutExtent + 1, 600)]
    [InlineData("Virtual-1", int.MinValue, 0, 800, 600)]
    [InlineData("Virtual-1", 0, AgentWire.MaxLayoutExtent + 1, 800, 600)]
    public void A_monitor_no_desktop_has_is_not_believed(string connector, int x, int y, int width, int height)
    {
        byte[] buffer = new byte[AgentWire.MaxMessage];
        int n = AgentWire.WriteLayout(buffer, new DesktopLayout([new LayoutMonitor("Virtual-2", 800, 0, 800, 600), new LayoutMonitor(connector, x, y, width, height)]));

        AgentWire.TryReadLayout(buffer.AsSpan(0, n), out _).ShouldBeFalse();
    }

    [Fact]
    public void A_layout_of_more_monitors_than_anyone_has_or_with_more_after_it_is_not_believed()
    {
        byte[] buffer = new byte[AgentWire.MaxMessage];
        int n = AgentWire.WriteLayout(buffer, new DesktopLayout([]));
        buffer[n - 1] = AgentWire.MaxMonitors + 1;
        AgentWire.TryReadLayout(buffer.AsSpan(0, n), out _).ShouldBeFalse();

        n = AgentWire.WriteLayout(buffer, new DesktopLayout([new LayoutMonitor("Virtual-1", 0, 0, 800, 600)]));
        AgentWire.TryReadLayout(buffer.AsSpan(0, n + 1), out _).ShouldBeFalse();
        AgentWire.TryReadLayout(buffer.AsSpan(0, n - 1), out _).ShouldBeFalse();
    }
}
