namespace DeskPair.Desktop.Tests;

/// <summary>
/// One executable does every job, so the first argument decides which. Everything that is not a role is a
/// modifier for the app itself, and getting that backwards would either hide the window or start a second
/// engine, so it is worth pinning down.
/// </summary>
public class ProgramRoleTests
{
    [Fact]
    public void No_arguments_opens_the_app()
    {
        Program.ParseRole([]).ShouldBe(AppRole.Main);
    }

    [Theory]
    [InlineData("--server", AppRole.Server)]
    [InlineData("--service", AppRole.Service)]
    [InlineData("--install-service", AppRole.InstallService)]
    [InlineData("--uninstall-service", AppRole.UninstallService)]
    [InlineData("--allow-firewall", AppRole.AllowFirewall)]
    [InlineData("--remove-firewall", AppRole.RemoveFirewall)]
    [InlineData("--version", AppRole.Version)]
    [InlineData("--help", AppRole.Help)]
    [InlineData("-h", AppRole.Help)]
    public void The_first_argument_names_the_role(string argument, AppRole expected)
    {
        Program.ParseRole([argument]).ShouldBe(expected);
    }

    [Theory]
    [InlineData("--minimised")]
    [InlineData("--no-engine")]
    [InlineData("--connect")]
    public void A_modifier_still_opens_the_app(string argument)
    {
        Program.ParseRole([argument, "270786177"]).ShouldBe(AppRole.Main);
    }

    [Fact]
    public void A_role_keeps_its_own_modifiers()
    {
        Program.ParseRole(["--server", "--data", "/tmp/fd", "--verbose"]).ShouldBe(AppRole.Server);
    }

    /// <summary>
    /// The roles that were their own processes are windows now. Left as roles they would open a second,
    /// engine-less app, so they must fall through to a normal launch rather than be recognised.
    ///
    /// <c>--service</c> was on this list and is not any more. It named the old always-running background
    /// process, and it names the Windows service that puts the engine on the lock screen -- different
    /// things that happen to want the same word, and the word now belongs to the one that exists.
    /// </summary>
    [Theory]
    [InlineData("--cm")]
    [InlineData("--tray")]
    [InlineData("--install")]
    public void A_role_that_no_longer_exists_opens_the_app(string argument)
    {
        Program.ParseRole([argument]).ShouldBe(AppRole.Main);
    }

    /// <summary>
    /// Installing and removing are separate words, not one word and a flag.
    ///
    /// They are typed by hand at exactly the moment somebody wants this machine to stop being reachable,
    /// and a mistyped flag that silently fell through to opening the app would leave the service installed
    /// while looking like it had done something.
    /// </summary>
    [Theory]
    [InlineData("--installservice")]
    [InlineData("--install-services")]
    [InlineData("--service-install")]
    public void A_near_miss_is_not_the_service_role(string argument)
    {
        Program.ParseRole([argument]).ShouldBe(AppRole.Main);
    }
}
