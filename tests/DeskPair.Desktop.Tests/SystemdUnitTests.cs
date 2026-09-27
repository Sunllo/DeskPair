using DeskPair.Desktop.Engine.LinuxService;

namespace DeskPair.Desktop.Tests;

/// <summary>
/// Every directive in the daemon's unit that was learned the hard way, pinned.
///
/// The unit is generated, so this is the only place its decisions can be checked on a machine without
/// systemd -- which is every developer machine. Each test names the failure the directive prevents.
/// </summary>
public class SystemdUnitTests
{
    private static Dictionary<string, List<string>> Parse(string unit)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (string raw in unit.Split('\n'))
        {
            string line = raw.Trim();
            int eq = line.IndexOf('=', StringComparison.Ordinal);
            if (line.Length == 0 || line[0] == '[' || eq < 0)
            {
                continue;
            }

            string key = line[..eq];
            if (!result.TryGetValue(key, out List<string>? values))
            {
                result[key] = values = [];
            }

            values.Add(line[(eq + 1)..]);
        }

        return result;
    }

    private static Dictionary<string, List<string>> Unit() => Parse(SystemdUnit.Daemon("/opt/sunllo/deskpair/DeskPair"));

    [Fact]
    public void It_runs_the_executable_directly_with_the_service_role()
    {
        Unit()["ExecStart"].ShouldBe(["/opt/sunllo/deskpair/DeskPair --service"]);
        SystemdUnit.Daemon("/opt/x/DeskPair").ShouldNotContain("/bin/sh");
    }

    /// <summary>A path with a space is one argument, or ExecStart starts "/opt/Sunllo" and fails at boot.</summary>
    [Fact]
    public void A_path_with_a_space_is_quoted()
    {
        Parse(SystemdUnit.Daemon("/opt/Sunllo DeskPair/DeskPair"))["ExecStart"]
            .ShouldBe(["\"/opt/Sunllo DeskPair/DeskPair\" --service"]);
    }

    /// <summary>
    /// The two lines from the network servers' hardening block that break a desktop daemon are absent:
    /// ProtectHome hides ~/.Xauthority on every display manager but GDM, and PrivateTmp hides the X socket.
    /// </summary>
    [Fact]
    public void The_server_hardening_that_breaks_a_desktop_daemon_is_not_applied()
    {
        Dictionary<string, List<string>> unit = Unit();

        unit.ShouldNotContainKey("ProtectHome");
        unit.ShouldNotContainKey("PrivateTmp");
        unit["ProtectSystem"].ShouldBe(["full"]);
    }

    /// <summary>
    /// on-failure, never always: the engine exits 0 on purpose when the secrets are not its to read, and
    /// Restart=always turns that into a restart every five seconds for ever.
    /// </summary>
    [Fact]
    public void A_clean_exit_is_not_restarted()
    {
        Unit()["Restart"].ShouldBe(["on-failure"]);
    }

    [Fact]
    public void It_gives_up_after_five_failures_rather_than_pinning_a_core()
    {
        Dictionary<string, List<string>> unit = Unit();

        unit["StartLimitBurst"].ShouldBe(["5"]);
        unit["StartLimitIntervalSec"].ShouldBe(["300"]);
    }

    /// <summary>The socket directory is systemd's to create and clean, and to keep across a restart.</summary>
    [Fact]
    public void The_runtime_directory_is_deskpair_and_survives_a_restart()
    {
        Dictionary<string, List<string>> unit = Unit();

        unit["RuntimeDirectory"].ShouldBe(["deskpair"]);
        unit["RuntimeDirectoryMode"].ShouldBe(["0755"]);
        unit["RuntimeDirectoryPreserve"].ShouldBe(["restart"]);
    }

    /// <summary>It must come up on a machine whose display manager did not, so it is not tied to graphical.target.</summary>
    [Fact]
    public void It_is_wanted_by_multi_user_not_graphical()
    {
        Dictionary<string, List<string>> unit = Unit();

        unit["WantedBy"].ShouldBe(["multi-user.target"]);
        unit["After"].Single().ShouldContain("systemd-logind.service");
        unit["After"].Single().ShouldNotContain("display-manager");
    }

    [Fact]
    public void It_is_root_without_no_new_privileges_and_logs_to_the_journal()
    {
        Dictionary<string, List<string>> unit = Unit();

        unit["User"].ShouldBe(["root"]);

        // Measured on the verification machine: with NoNewPrivileges=true the engine's setresuid to its
        // own account fails with EPERM, and the daemon restarts it every five seconds. The bisect over
        // every hardening directive in the unit named this one alone.
        unit.ShouldNotContainKey("NoNewPrivileges");
        unit["SyslogIdentifier"].ShouldBe(["deskpair"]);
        unit.ShouldNotContainKey("StandardOutput");
    }

    [Fact]
    public void The_unit_is_named_as_the_installer_expects()
    {
        SystemdUnit.Name.ShouldBe("sunllo-deskpair.service");
    }
}
