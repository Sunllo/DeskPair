using Microsoft.Extensions.Logging.Abstractions;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Linux.Capture;

namespace DeskPair.Platform.Linux.Tests;

/// <summary>
/// What is asked of xrandr to teach an output a size, and to take it back. The commands themselves were run against a
/// real X server (Xorg with the dummy driver) when this was written; these hold their order and their arguments.
/// </summary>
public class X11ModeTeacherTests
{
    private static DisplayDescriptor Output(string name) => new(0, name, 0, 0, 1920, 1080, 1.0, FrameRotation.None, true, 0);

    private sealed class FakeXrandr
    {
        public List<string> Calls { get; } = [];

        /// <summary>Commands (by their first two words) that fail, with what xrandr said.</summary>
        public Dictionary<string, string> Failures { get; } = [];

        public string? Run(IReadOnlyList<string> arguments)
        {
            string call = string.Join(' ', arguments);
            Calls.Add(call);
            return Failures.FirstOrDefault(f => call.StartsWith(f.Key, StringComparison.Ordinal)).Value;
        }
    }

    [Fact]
    public async Task A_size_is_made_with_cvt_rb_timings_and_added_to_the_output()
    {
        var xrandr = new FakeXrandr();
        var teacher = new X11ModeTeacher(NullLogger.Instance, wayland: false, xrandr.Run);

        (await teacher.TeachAsync(Output("HDMI-1"), new DisplayMode(1234, 567), CancellationToken.None)).Succeeded.ShouldBeTrue();

        string timings = string.Join(' ', Cvt.Compute(1234, 567, reducedBlanking: true).Arguments());
        xrandr.Calls.ShouldBe([$"--newmode deskpair-1234x567 {timings}", "--addmode HDMI-1 deskpair-1234x567"]);
    }

    [Fact]
    public async Task A_size_already_made_is_only_added_to_another_output_and_never_twice_to_one()
    {
        var xrandr = new FakeXrandr();
        var teacher = new X11ModeTeacher(NullLogger.Instance, wayland: false, xrandr.Run);

        await teacher.TeachAsync(Output("HDMI-1"), new DisplayMode(1000, 600), CancellationToken.None);
        await teacher.TeachAsync(Output("HDMI-1"), new DisplayMode(1000, 600), CancellationToken.None);
        await teacher.TeachAsync(Output("DP-2"), new DisplayMode(1000, 600), CancellationToken.None);

        xrandr.Calls.Count(c => c.StartsWith("--newmode", StringComparison.Ordinal)).ShouldBe(1);
        xrandr.Calls.Where(c => c.StartsWith("--addmode", StringComparison.Ordinal)).ShouldBe(["--addmode HDMI-1 deskpair-1000x600", "--addmode DP-2 deskpair-1000x600"]);
    }

    /// <summary>Off every output first, then out of the server: a mode still on an output cannot be removed.</summary>
    [Fact]
    public async Task Forgetting_takes_each_size_off_its_outputs_before_removing_it()
    {
        var xrandr = new FakeXrandr();
        var teacher = new X11ModeTeacher(NullLogger.Instance, wayland: false, xrandr.Run);
        await teacher.TeachAsync(Output("HDMI-1"), new DisplayMode(1000, 600), CancellationToken.None);
        await teacher.TeachAsync(Output("DP-2"), new DisplayMode(1000, 600), CancellationToken.None);
        await teacher.TeachAsync(Output("DP-2"), new DisplayMode(1366, 768), CancellationToken.None);
        xrandr.Calls.Clear();

        await teacher.ForgetTaughtModesAsync(CancellationToken.None);

        xrandr.Calls.ShouldBe(
        [
            "--delmode HDMI-1 deskpair-1000x600",
            "--delmode DP-2 deskpair-1000x600",
            "--delmode DP-2 deskpair-1366x768",
            "--rmmode deskpair-1000x600",
            "--rmmode deskpair-1366x768",
        ]);

        xrandr.Calls.Clear();
        await teacher.ForgetTaughtModesAsync(CancellationToken.None);
        xrandr.Calls.ShouldBeEmpty("forgotten once is forgotten");
    }

    /// <summary>
    /// An engine that died leaves its modes in the server, and xrandr will not make one of the same name again. The
    /// name says it is ours: it is used, and taken away with the rest.
    /// </summary>
    [Fact]
    public async Task A_mode_left_by_an_earlier_engine_is_used_and_then_removed()
    {
        var xrandr = new FakeXrandr();
        xrandr.Failures["--newmode"] = "X Error of failed request:  BadName (named color or font does not exist)";
        var teacher = new X11ModeTeacher(NullLogger.Instance, wayland: false, xrandr.Run);

        (await teacher.TeachAsync(Output("HDMI-1"), new DisplayMode(1000, 600), CancellationToken.None)).Succeeded.ShouldBeTrue();
        await teacher.ForgetTaughtModesAsync(CancellationToken.None);

        xrandr.Calls.ShouldContain("--rmmode deskpair-1000x600");
    }

    /// <summary>A size the output refuses is refused to the viewer, and the mode made for it is not left behind.</summary>
    [Fact]
    public async Task A_size_the_output_will_not_take_is_refused_and_cleaned_up()
    {
        var xrandr = new FakeXrandr();
        xrandr.Failures["--addmode"] = "X Error of failed request:  BadMatch (invalid parameter attributes)";
        var teacher = new X11ModeTeacher(NullLogger.Instance, wayland: false, xrandr.Run);

        DisplayActionResult result = await teacher.TeachAsync(Output("HDMI-1"), new DisplayMode(1000, 600), CancellationToken.None);

        result.Succeeded.ShouldBeFalse();
        result.Failure.ShouldNotBeNull().ShouldContain("BadMatch");
        xrandr.Calls.Last().ShouldBe("--rmmode deskpair-1000x600");
        xrandr.Calls.Clear();
        await teacher.ForgetTaughtModesAsync(CancellationToken.None);
        xrandr.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("-display")]
    [InlineData("HDMI 1")]
    [InlineData("")]
    public void An_output_name_xrandr_would_read_as_something_else_is_not_taught(string name)
    {
        new X11ModeTeacher(NullLogger.Instance, wayland: false, _ => null).CanTeach(Output(name)).ShouldBeFalse();
    }

    [Fact]
    public async Task Nothing_is_taught_under_wayland_or_at_an_absurd_size()
    {
        var xrandr = new FakeXrandr();
        var wayland = new X11ModeTeacher(NullLogger.Instance, wayland: true, xrandr.Run);
        wayland.CanTeach(Output("HDMI-1")).ShouldBeFalse();
        (await wayland.TeachAsync(Output("HDMI-1"), new DisplayMode(1000, 600), CancellationToken.None)).Succeeded.ShouldBeFalse();

        var x11 = new X11ModeTeacher(NullLogger.Instance, wayland: false, xrandr.Run);
        (await x11.TeachAsync(Output("HDMI-1"), new DisplayMode(100000, 600), CancellationToken.None)).Succeeded.ShouldBeFalse();
        xrandr.Calls.ShouldBeEmpty();
    }
}
