using DeskPair.Platform.Linux.Hosting;

namespace DeskPair.Platform.Linux.Tests;

/// <summary>
/// Reading who is at the screen out of loginctl, which is the fallback for a machine with systemd but no
/// libsystemd to link against.
///
/// The parse is separated from the process on purpose: every interesting case is a machine state nobody
/// has to hand -- a GDM greeter, a fast user switch, a seat with nothing on it -- and all of them are two
/// lines of captured text.
///
/// What these are really pinning is the greeter. Getting it wrong does not look like a failure: a login
/// screen misread as an ordinary session means the supervisor runs an engine as whoever the display
/// manager happens to be, against a desktop that is not theirs.
/// </summary>
public class LinuxSessionsTests
{
    [Fact]
    public void An_ordinary_signed_in_desktop()
    {
        ActiveSession? s = LinuxSessions.ParseSession("""
            Id=2
            User=1000
            Type=x11
            Class=user
            Display=:0
            """);

        s.ShouldNotBeNull();
        s!.Id.ShouldBe("2");
        s.Uid.ShouldBe(1000u);
        s.Kind.ShouldBe(SessionKind.X11);
        s.Role.ShouldBe(SessionRole.User);
        s.Display.ShouldBe(":0");
    }

    /// <summary>
    /// The login screen. It is a session like any other, belonging to the display manager's own account,
    /// and the only thing that says so is the class.
    /// </summary>
    [Fact]
    public void The_login_screen_is_recognised_by_its_class_not_its_user()
    {
        ActiveSession? s = LinuxSessions.ParseSession("""
            Id=c1
            User=125
            Type=wayland
            Class=greeter
            Display=
            """);

        s.ShouldNotBeNull();
        s!.Role.ShouldBe(SessionRole.Greeter);
        s.Uid.ShouldBe(125u, "the engine would have to run as the greeter's account, not root");
        s.Kind.ShouldBe(SessionKind.Wayland);
        s.Display.ShouldBeNull("a Wayland session has no X display, and an empty string is not a display");
    }

    /// <summary>
    /// The account names differ per display manager -- gdm, gdm-greeter, sddm, lightdm, greetd -- which is
    /// exactly why the name is not what is being read. Each of these is still a greeter.
    /// </summary>
    [Theory]
    [InlineData("125")]
    [InlineData("113")]
    [InlineData("997")]
    public void Any_display_managers_greeter_reads_the_same_way(string uid)
    {
        LinuxSessions.ParseSession($"Id=c1\nUser={uid}\nType=x11\nClass=greeter\nDisplay=:0")!
            .Role.ShouldBe(SessionRole.Greeter);
    }

    /// <summary>A lock screen is a user's own session, and must not be mistaken for a login screen.</summary>
    [Fact]
    public void A_lock_screen_is_not_a_login_screen()
    {
        LinuxSessions.ParseSession("Id=3\nUser=1000\nType=wayland\nClass=lock-screen\nDisplay=")!
            .Role.ShouldBe(SessionRole.LockScreen);
    }

    [Fact]
    public void A_text_console_says_so_rather_than_looking_like_a_desktop()
    {
        ActiveSession? s = LinuxSessions.ParseSession("Id=1\nUser=1000\nType=tty\nClass=user\nDisplay=");

        s!.Kind.ShouldBe(SessionKind.Tty);
        s.Display.ShouldBeNull();
    }

    /// <summary>
    /// A seat with nothing active on it: the machine has booted and no display manager has claimed the
    /// screen. Not an error, and not a session either.
    /// </summary>
    [Fact]
    public void Nothing_on_the_seat_is_an_answer()
    {
        LinuxSessions.ParseSession(null).ShouldBeNull();
        LinuxSessions.ParseSession("").ShouldBeNull();
        LinuxSessions.ParseSession("Id=\nUser=\n").ShouldBeNull();
    }

    /// <summary>Output that arrives without the fields being asked about must not become a half-session.</summary>
    [Fact]
    public void A_session_with_no_user_is_not_a_session()
    {
        LinuxSessions.ParseSession("Id=7\nType=x11\nClass=user").ShouldBeNull();
        LinuxSessions.ParseSession("User=1000\nType=x11\nClass=user").ShouldBeNull();
    }

    /// <summary>
    /// An unset property is the key with nothing after it, which is not the same as the key being missing
    /// and must not read as whatever the rest of the line happens to contain.
    /// </summary>
    [Fact]
    public void An_empty_property_reads_as_empty()
    {
        LinuxSessions.ParseProperty("Display=\nType=wayland", "Display").ShouldBe("");
        LinuxSessions.ParseProperty("Type=wayland", "Display").ShouldBeNull();
    }

    /// <summary>A key that is a prefix of another key must not answer for it.</summary>
    [Fact]
    public void A_longer_key_is_not_a_match_for_a_shorter_one()
    {
        LinuxSessions.ParseProperty("IdleHint=no\nId=5", "Id").ShouldBe("5");
        LinuxSessions.ParseProperty("Class=user", "Cla").ShouldBeNull();
    }

    /// <summary>Whatever the shell hands back, carriage returns and stray blank lines included.</summary>
    [Fact]
    public void Windows_style_line_endings_do_not_change_the_answer()
    {
        LinuxSessions.ParseSession("Id=2\r\nUser=1000\r\nType=x11\r\nClass=user\r\nDisplay=:0\r\n")!
            .Display.ShouldBe(":0");
    }

    /// <summary>
    /// Asking a machine that cannot answer.
    ///
    /// A container, a distribution without systemd, a developer running the tests on Windows: none of
    /// them has libsystemd or loginctl, and every one of them has to come back with "no session" rather
    /// than a DllNotFoundException out of the middle of a supervisor loop. This is the only test here
    /// that touches the real machine, and what it asserts is that touching it is safe.
    /// </summary>
    [Fact]
    public void Asking_a_machine_that_cannot_answer_returns_nothing()
    {
        ActiveSession? answer = LinuxSessions.Active("seat-that-does-not-exist");

        answer.ShouldBeNull();
    }

    /// <summary>
    /// /proc/PID/environ, which is where DISPLAY has to be found because logind does not report it.
    ///
    /// Taken from a real GNOME 24.04 desktop: the user's session carries DISPLAY=:0 with its cookie under
    /// the runtime directory rather than in the home directory, which is GDM's arrangement and is the
    /// shape the daemon will meet.
    /// </summary>
    [Fact]
    public void A_desktops_environment_is_read_out_of_the_nul_separated_block()
    {
        byte[] raw = System.Text.Encoding.UTF8.GetBytes(
            "LANG=en_GB.UTF-8\0DISPLAY=:0\0XAUTHORITY=/run/user/1000/gdm/Xauthority\0XDG_RUNTIME_DIR=/run/user/1000\0");

        Dictionary<string, string> env = LinuxSessions.ParseEnviron(raw);

        env["DISPLAY"].ShouldBe(":0");
        env["XAUTHORITY"].ShouldBe("/run/user/1000/gdm/Xauthority");
        env["XDG_RUNTIME_DIR"].ShouldBe("/run/user/1000");
    }

    /// <summary>
    /// A value may contain '=' -- the session bus address always does -- so only the first one separates.
    /// Splitting on every one turns an address into a name.
    /// </summary>
    [Fact]
    public void A_value_containing_an_equals_sign_survives()
    {
        Dictionary<string, string> env = LinuxSessions.ParseEnviron(
            System.Text.Encoding.UTF8.GetBytes("DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/1000/bus\0"));

        env["DBUS_SESSION_BUS_ADDRESS"].ShouldBe("unix:path=/run/user/1000/bus");
    }

    /// <summary>A kernel thread's environ is empty, and a torn read is not a variable.</summary>
    [Fact]
    public void Nothing_and_nonsense_both_read_as_no_variables()
    {
        LinuxSessions.ParseEnviron([]).ShouldBeEmpty();
        LinuxSessions.ParseEnviron(System.Text.Encoding.UTF8.GetBytes("\0\0")).ShouldBeEmpty();
        LinuxSessions.ParseEnviron(System.Text.Encoding.UTF8.GetBytes("NOTAVARIABLE\0=novalue\0")).ShouldBeEmpty();
    }

    /// <summary>logind's record of a session, as /run/systemd/sessions/2 holds it: the leader is the session's beginning.</summary>
    [Fact]
    public void The_leader_is_read_from_loginds_record()
    {
        const string Record = "# This is private data. Do not parse.\nUID=1000\nUSER=alice\nACTIVE=1\nIS_DISPLAY=1\nSTATE=active\n" +
                              "REMOTE=0\nLEADER=2314\nTYPE=wayland\nCLASS=user\nSEAT=seat0\nVTNR=2\n";

        LinuxSessions.ParseLeader(Record).ShouldBe(2314);
        LinuxSessions.ParseLeader("UID=1000\nLEADER=\n").ShouldBeNull();
        LinuxSessions.ParseLeader("UID=1000\nLEADER=-4\n").ShouldBeNull();
        LinuxSessions.ParseLeader("UID=1000\n").ShouldBeNull();
    }

    /// <summary>A command's name can hold spaces and parentheses, so the fields are counted from the last ')'.</summary>
    [Fact]
    public void A_processs_start_is_the_22nd_field_of_its_stat()
    {
        const string Stat = "2451 (gnome-shell) S 2314 2451 2451 0 -1 4194560 105838 1030 104 0 3017 845 0 0 20 0 17 0 7312 " +
                            "5044965376 70912 18446744073709551615 1 1 0 0 0 0 0 16781312 82170 0 0 0 17 1 0 0 0 0 0\n";

        LinuxSessions.ParseStartTicks(Stat).ShouldBe(7312UL);
        LinuxSessions.ParseStartTicks(Stat.Replace("(gnome-shell)", "(a (b) c)")).ShouldBe(7312UL);
        LinuxSessions.ParseStartTicks("2451 (gnome-shell) S 2314").ShouldBeNull();
        LinuxSessions.ParseStartTicks("no parentheses at all").ShouldBeNull();
    }

    /// <summary>
    /// Asking about a user on a machine with no /proc to read. The supervisor calls this before it has
    /// given up root, so an exception here would take the daemon down rather than one session with it.
    /// </summary>
    [Fact]
    public void Looking_for_a_desktop_where_there_is_no_proc_returns_nothing()
    {
        if (OperatingSystem.IsLinux())
        {
            return; // there is a /proc here, and what it holds is the machine's business, not a test's
        }

        LinuxSessions.EnvironmentOf(1000).ShouldBeNull();
    }

    /// <summary>
    /// A type logind grows later, or one this does not know, is not a reason to refuse the session: it is
    /// a reason to say the capturer cannot be chosen from it.
    /// </summary>
    [Fact]
    public void An_unknown_type_is_reported_rather_than_guessed()
    {
        LinuxSessions.ParseSession("Id=2\nUser=1000\nType=mir\nClass=user\nDisplay=")!
            .Kind.ShouldBe(SessionKind.Unknown);
    }
}
