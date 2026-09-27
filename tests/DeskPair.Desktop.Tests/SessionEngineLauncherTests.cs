using DeskPair.Desktop.Engine.LinuxService;

namespace DeskPair.Desktop.Tests;

/// <summary>
/// The daemon starts the engine as another account, and the engine's command line is public:
/// <c>/proc/&lt;pid&gt;/cmdline</c> is readable by every user on the machine. The IPC token used to be
/// in it, which made "any local account can drive the engine" a one-liner.
/// </summary>
public class SessionEngineLauncherTests
{
    [Fact]
    public void The_engine_command_line_carries_no_token()
    {
        string[] arguments = SessionEngineLauncher.EngineArguments("/var/lib/deskpair", (999, 999), 3, 4, 5, 6);

        arguments.ShouldNotContain(a => a.Contains("token", StringComparison.OrdinalIgnoreCase));
        arguments.ShouldContain("--ipc-system");
        arguments.ShouldContain("--server");
        string joined = string.Join(' ', arguments);
        joined.ShouldContain("--drop-uid 999 --drop-gid 999");
        joined.ShouldContain("--supervisor-fd 3");
        joined.ShouldContain("--keyboard-fd 4 --pointer-fd 5 --relative-fd 6");
    }

    /// <summary>A machine with no /dev/uinput hands the engine no devices; -1 is what "none" looks like on the command line.</summary>
    [Fact]
    public void Missing_input_devices_are_passed_as_minus_one()
    {
        string joined = string.Join(' ', SessionEngineLauncher.EngineArguments("/var/lib/deskpair", (999, 999), 3, -1, -1, -1));

        joined.ShouldContain("--supervisor-fd 3");
        joined.ShouldContain("--keyboard-fd -1 --pointer-fd -1 --relative-fd -1");
    }

    [Fact]
    public void The_agent_command_line_names_its_socket_and_whom_to_become_and_nothing_else()
    {
        string joined = string.Join(' ', SessionEngineLauncher.AgentArguments(7, 1000, 1000, "alice"));

        joined.ShouldBe("--session-agent --agent-fd 7 --drop-uid 1000 --drop-gid 1000 --drop-user alice");
        SessionEngineLauncher.AgentArguments(7, 1234, 1234, "john smith")[^1].ShouldBe("\"john smith\"", "a directory's user can have a space in the name");
    }

    [Fact]
    public void A_data_directory_with_a_space_is_quoted()
    {
        string[] arguments = SessionEngineLauncher.EngineArguments("/var/lib/desk pair", (1, 1), 3, 4, 5, 6);

        arguments[2].ShouldBe("\"/var/lib/desk pair\"");
    }
}
