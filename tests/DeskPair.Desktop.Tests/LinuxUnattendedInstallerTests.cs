using DeskPair.Core.Config;
using DeskPair.Desktop.Engine.LinuxService;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeskPair.Desktop.Tests;

/// <summary>
/// The parts of the Linux installer that decide things, pinned on a machine with no systemd, no useradd
/// and no pkexec -- which is every developer machine. The rest of it is verified on a real Linux box.
/// </summary>
public class LinuxUnattendedInstallerTests
{
    [Fact]
    public void The_engine_account_is_a_system_account_with_no_shell_whose_home_is_the_data_directory()
    {
        string[] args = LinuxUnattendedInstaller.UseraddArguments("/usr/sbin/nologin");

        args.ShouldContain("--system");
        args.ShouldContain("--user-group");
        args.ShouldContain("--no-create-home");
        args[Array.IndexOf(args, "--home-dir") + 1].ShouldBe("/var/lib/deskpair");
        args[Array.IndexOf(args, "--shell") + 1].ShouldBe("/usr/sbin/nologin");
        args[^1].ShouldBe(LinuxAccounts.EngineUser);
    }

    [Fact]
    public void Pkexec_saying_no_is_declined_and_pkexec_unable_to_ask_is_no_elevation()
    {
        // 126 is the person closing the prompt; 127 is pkexec with nobody to ask, which is what it says
        // over SSH. Neither is "the install failed", and the settings page shows each as what it is.
        LinuxUnattendedInstaller.OutcomeOf(126).ShouldBe(LinuxUnattendedInstaller.Declined);
        LinuxUnattendedInstaller.OutcomeOf(127).ShouldBe(LinuxUnattendedInstaller.NoElevation);
        LinuxUnattendedInstaller.OutcomeOf(0).ShouldBe(0);
        LinuxUnattendedInstaller.OutcomeOf(2).ShouldBe(2);
    }

    [Fact]
    public void The_hand_over_moves_the_five_keys_that_are_this_machine_and_nothing_else()
    {
        // The same five as macOS. A key missing here is a machine that becomes a different machine on
        // install; a key added here is a per-installation setting frozen into the machine.
        LinuxUnattendedInstaller.Identity.ShouldBe(["identity-key", "peer-id", "machine-id", "password-salt", "password-permanent-h1"]);
    }

    [Fact]
    public async Task A_machine_that_minted_its_own_salt_but_has_no_password_gets_the_users_salt_and_hash_together()
    {
        // The daemon had run before the install (it does on the verification machine), so the machine-wide
        // store already has an identity and a salt of its own, and no permanent password. The hash is
        // computed against the salt beside it: copying the hash alone would leave a machine that declines
        // every password. The identity stays; the salt is the one thing overwritten, with its hash.
        string root = Path.Combine(Path.GetTempPath(), "deskpair-handover-" + Guid.NewGuid().ToString("N"));
        string user = Path.Combine(root, "user");
        string machine = Path.Combine(root, "machine");
        try
        {
            var from = new FileSecretStore(Path.Combine(user, "secrets"));
            var to = new FileSecretStore(Path.Combine(machine, "secrets"));
            await from.SetAsync("identity-key", new byte[] { 1 });
            await from.SetAsync("machine-id", new byte[] { 4 });
            await from.SetAsync("password-salt", new byte[] { 2 });
            await from.SetAsync("password-permanent-h1", new byte[] { 3 });
            await to.SetAsync("identity-key", new byte[] { 9 });
            await to.SetAsync("password-salt", new byte[] { 8 });

            LinuxUnattendedInstaller.HandOver(user, machine, NullLogger.Instance);

            (await to.GetAsync("identity-key")).ShouldBe(new byte[] { 9 });
            (await to.GetAsync("machine-id")).ShouldBe(new byte[] { 4 });
            (await to.GetAsync("password-salt")).ShouldBe(new byte[] { 2 });
            (await to.GetAsync("password-permanent-h1")).ShouldBe(new byte[] { 3 });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task A_machine_that_already_has_a_permanent_password_keeps_it_and_its_salt()
    {
        string root = Path.Combine(Path.GetTempPath(), "deskpair-handover-" + Guid.NewGuid().ToString("N"));
        string user = Path.Combine(root, "user");
        string machine = Path.Combine(root, "machine");
        try
        {
            var from = new FileSecretStore(Path.Combine(user, "secrets"));
            var to = new FileSecretStore(Path.Combine(machine, "secrets"));
            await from.SetAsync("password-salt", new byte[] { 2 });
            await from.SetAsync("password-permanent-h1", new byte[] { 3 });
            await to.SetAsync("password-salt", new byte[] { 8 });
            await to.SetAsync("password-permanent-h1", new byte[] { 7 });

            LinuxUnattendedInstaller.HandOver(user, machine, NullLogger.Instance);

            (await to.GetAsync("password-salt")).ShouldBe(new byte[] { 8 });
            (await to.GetAsync("password-permanent-h1")).ShouldBe(new byte[] { 7 });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void The_sudo_command_quotes_the_paths_and_names_the_system_stage()
    {
        string command = LinuxUnattendedInstaller.SudoCommand(install: true, "/home/some body/.local/share/deskpair");

        command.ShouldStartWith("sudo '");
        command.ShouldContain("--install-service --system-stage --from '/home/some body/.local/share/deskpair'");
        LinuxUnattendedInstaller.SudoCommand(install: false, "/ignored").ShouldEndWith("--uninstall-service");
    }

    [Fact]
    public void The_unit_points_at_the_installed_copy_not_at_wherever_the_app_was_run_from()
    {
        // The engine account cannot read a home directory; the copy under /opt is what the unit starts.
        LinuxUnattendedInstaller.Executable.ShouldBe("/opt/deskpair/DeskPair");
        SystemdUnit.Daemon(LinuxUnattendedInstaller.Executable).ShouldContain("ExecStart=/opt/deskpair/DeskPair --service");
    }
}
