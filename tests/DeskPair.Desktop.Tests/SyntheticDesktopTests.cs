using DeskPair.Desktop.Engine;
using DeskPair.Platform.Abstractions.Hosting;

namespace DeskPair.Desktop.Tests;

/// <summary>
/// The synthetic desktop has to be asked for.
///
/// It used to be what happened when nothing else worked: no DISPLAY, no screen-recording consent, no
/// libX11 -- and the engine served a moving picture of a screen belonging to no computer. The connection
/// succeeded and the viewer saw something, so the failure had no symptom except a warning in a log nobody
/// was reading. A host started outside a desktop session lands there every time, which is precisely the
/// arrangement the unattended work depends on.
///
/// These are cheap, and that is the point: the property is one boolean, and the cost of getting it wrong
/// is a remote machine that looks connected and shows the wrong screen.
/// </summary>
public class SyntheticDesktopTests
{
    [Fact]
    public void Nobody_gets_it_by_default()
    {
        PlatformServices.SyntheticAllowed(["--server"]).ShouldBeFalse();
    }

    [Fact]
    public void The_flag_asks_for_it()
    {
        PlatformServices.SyntheticAllowed(["--server", "--synthetic"]).ShouldBeTrue();
    }

    /// <summary>The harness sets an environment variable rather than passing argv it does not own.</summary>
    [Fact]
    public void The_variable_asks_for_it_too()
    {
        string? before = Environment.GetEnvironmentVariable(PlatformServices.SyntheticVariable);
        try
        {
            Environment.SetEnvironmentVariable(PlatformServices.SyntheticVariable, "1");
            PlatformServices.SyntheticAllowed([]).ShouldBeTrue();

            // "0" is how a wrapper script turns an inherited setting off, and it must mean off.
            Environment.SetEnvironmentVariable(PlatformServices.SyntheticVariable, "0");
            PlatformServices.SyntheticAllowed([]).ShouldBeFalse();

            Environment.SetEnvironmentVariable(PlatformServices.SyntheticVariable, "");
            PlatformServices.SyntheticAllowed([]).ShouldBeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable(PlatformServices.SyntheticVariable, before);
        }
    }

    /// <summary>
    /// The refusal carries the machine state, because the machine is the thing that cannot be reached to
    /// ask. A reason without it is another round trip.
    /// </summary>
    [Fact]
    public void The_refusal_says_what_was_found_as_well_as_what_was_wanted()
    {
        var e = new HostPlatformUnavailableException("there is no desktop this process can capture", "DISPLAY=(unset) euid=0");

        e.Reason.ShouldBe("there is no desktop this process can capture");
        e.Diagnostics.ShouldBe("DISPLAY=(unset) euid=0");
        e.Message.ShouldContain("DISPLAY=(unset)");
        e.Message.ShouldContain("no desktop this process can capture");
    }

    [Fact]
    public void A_refusal_with_nothing_to_report_is_still_readable()
    {
        new HostPlatformUnavailableException("there is no native host for this operating system", "")
            .Message.ShouldBe("there is no native host for this operating system");
    }
}
