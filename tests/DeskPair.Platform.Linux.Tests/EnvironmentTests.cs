using DeskPair.Platform.Linux.Hosting;
using Xunit.Abstractions;

namespace DeskPair.Platform.Linux.Tests;

/// <summary>
/// The gate against a green run that proved nothing. Everything else in this assembly skips where the
/// capability is missing, and a skipped test reports as passed, so these two exist to make the difference
/// visible: one always prints what was found, the other turns the skips into failures when the machine is
/// supposed to have a display.
/// </summary>
public class EnvironmentTests
{
    private readonly ITestOutputHelper _out;

    public EnvironmentTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void What_this_machine_offers()
    {
        _out.WriteLine(X11Session.Why);
        _out.WriteLine($"available={X11Session.IsAvailable} capture={X11Session.CanCapture} inject={X11Session.CanInject} cursor={X11Session.HasCursor} wayland={X11Session.IsWayland}");
    }

    [Fact]
    public void When_an_x11_session_is_required_it_has_to_be_there()
    {
        if (!X11Session.Required)
        {
            return;
        }

        X11Session.IsAvailable.ShouldBeTrue(X11Session.Why);
        X11Session.CanCapture.ShouldBeTrue(X11Session.Why);
        X11Session.CanInject.ShouldBeTrue(X11Session.Why);
        X11Session.HasCursor.ShouldBeTrue(X11Session.Why);
    }
}

/// <summary>Pure functions pulled out of the platform surface; these run on every OS, with or without a display.</summary>
public class LinuxPlatformInfoTests
{
    [Theory]
    [InlineData("PRETTY_NAME=\"Ubuntu 24.04.2 LTS\"", "Ubuntu 24.04.2 LTS")]
    [InlineData("PRETTY_NAME=Debian", "Debian")]
    public void The_pretty_name_is_read_quoted_or_bare(string line, string expected)
    {
        LinuxPlatformInfo.ParsePrettyName([line]).ShouldBe(expected);
    }

    [Fact]
    public void A_file_without_a_pretty_name_falls_back()
    {
        LinuxPlatformInfo.ParsePrettyName(["NAME=Ubuntu", "VERSION_ID=\"24.04\""]).ShouldBe("Linux");
        LinuxPlatformInfo.ParsePrettyName([]).ShouldBe("Linux");
    }

    [Theory]
    [InlineData("wayland", null, true)]
    [InlineData("Wayland", null, true)]
    [InlineData("x11", "wayland-0", true)]   // the variable wins; a Wayland socket means Wayland
    [InlineData("x11", null, false)]
    [InlineData("tty", "", false)]
    [InlineData(null, null, false)]
    public void Wayland_is_decided_by_either_signal(string? sessionType, string? waylandDisplay, bool expected)
    {
        LinuxPlatformInfo.IsWaylandFrom(sessionType, waylandDisplay).ShouldBe(expected);
    }
}

/// <summary>
/// X11 has no "tell me when it changed" for the root window, so every captured frame is compared with the
/// last. Pulled out of the capture loop, the comparison is testable without a display — and on a live desktop
/// it could never be tested honestly anyway, because a screen with a clock on it is never still.
/// </summary>
public class FrameDiffTests
{
    [Fact]
    public void Identical_buffers_are_not_a_change()
    {
        byte[] a = [1, 2, 3, 4];
        byte[] b = [1, 2, 3, 4];
        Capture.X11ScreenCapturer.Changed(a, b).ShouldBeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    public void One_differing_byte_anywhere_is_a_change(int index)
    {
        byte[] a = [1, 2, 3, 4];
        byte[] b = [1, 2, 3, 4];
        b[index] ^= 0xFF;
        Capture.X11ScreenCapturer.Changed(a, b).ShouldBeTrue();
    }

    [Fact]
    public void A_different_length_is_a_change()
    {
        Capture.X11ScreenCapturer.Changed([1, 2, 3], [1, 2, 3, 4]).ShouldBeTrue();
    }

    [Fact]
    public void Two_empty_buffers_are_not_a_change()
    {
        Capture.X11ScreenCapturer.Changed([], []).ShouldBeFalse();
    }
}
