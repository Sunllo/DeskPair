using DeskPair.Platform.Linux.Hosting;

namespace DeskPair.Platform.Linux.Tests;

/// <summary>
/// The line a Linux host prints when it refuses to start.
///
/// It is the whole remote diagnosis: a machine that cannot be reached cannot be asked what DISPLAY is set
/// to, so whatever is not in this line costs a trip to the desk. Two values earn their place by telling
/// apart failures that otherwise read identically -- euid separates "no session" from "the daemon has no
/// session", and XAUTHORITY's readability separates a correct-looking path from one this user cannot open.
/// </summary>
public class LinuxSessionDiagnosticsTests
{
    private static string Describe(Dictionary<string, string?> env, uint euid = 1000, params string[] readable) =>
        LinuxSessionDiagnostics.Describe(
            name => env.TryGetValue(name, out string? v) ? v : null,
            euid,
            readable.Contains);

    [Fact]
    public void An_ordinary_X11_desktop_reads_as_one()
    {
        string line = Describe(
            new() { ["DISPLAY"] = ":0", ["XDG_SESSION_TYPE"] = "x11", ["XAUTHORITY"] = "/home/alice/.Xauthority", ["XDG_RUNTIME_DIR"] = "/run/user/1000" },
            readable: "/home/alice/.Xauthority");

        line.ShouldContain("DISPLAY=:0");
        line.ShouldContain("XDG_SESSION_TYPE=x11");
        line.ShouldContain("XAUTHORITY=/home/alice/.Xauthority (readable)");
        line.ShouldContain("euid=1000");
    }

    /// <summary>
    /// The root daemon's failure. It looks exactly like a headless server's -- no DISPLAY, nothing to
    /// capture -- and the only thing that tells them apart is who is asking.
    /// </summary>
    [Fact]
    public void A_root_process_with_no_session_says_which_it_is()
    {
        string line = Describe([], euid: 0);

        line.ShouldContain("DISPLAY=(unset)");
        line.ShouldContain("WAYLAND_DISPLAY=(unset)");
        line.ShouldContain("euid=0");
    }

    /// <summary>
    /// The one that has cost the most time: a path that is set, is correct, names a file that exists, and
    /// belongs to somebody else. Printing the path alone says nothing is wrong.
    /// </summary>
    [Fact]
    public void An_unreadable_xauthority_is_called_out_rather_than_just_printed()
    {
        string line = Describe(
            new() { ["DISPLAY"] = ":0", ["XAUTHORITY"] = "/run/user/1000/gdm/Xauthority" },
            euid: 0);

        line.ShouldContain("XAUTHORITY=/run/user/1000/gdm/Xauthority (NOT readable)");
    }

    [Fact]
    public void A_wayland_session_is_visible_without_asking_anything_else()
    {
        string line = Describe(new()
        {
            ["WAYLAND_DISPLAY"] = "wayland-0",
            ["XDG_SESSION_TYPE"] = "wayland",
            ["XDG_CURRENT_DESKTOP"] = "GNOME",
        });

        line.ShouldContain("WAYLAND_DISPLAY=wayland-0");
        line.ShouldContain("XDG_CURRENT_DESKTOP=GNOME");
        line.ShouldContain("DISPLAY=(unset)");
    }

    /// <summary>An empty value is as good as unset, and must not read as a value that is present.</summary>
    [Fact]
    public void An_empty_value_reads_as_unset()
    {
        Describe(new() { ["DISPLAY"] = "" }).ShouldContain("DISPLAY=(unset)");
    }
}
