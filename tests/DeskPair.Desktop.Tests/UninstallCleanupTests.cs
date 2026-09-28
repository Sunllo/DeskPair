using Microsoft.Extensions.Logging.Abstractions;
using DeskPair.Desktop.Engine;
using DeskPair.Desktop.Services;

namespace DeskPair.Desktop.Tests;

/// <summary>
/// What removing DeskPair on Windows takes away besides its own folder: the firewall rules and the login entries that
/// name this copy of the program, and only those.
/// </summary>
public class UninstallCleanupTests
{
    private const string Installed = @"C:\Program Files\Sunllo\DeskPair\DeskPair.exe";

    /// <summary>
    /// The entry the app writes, with and without <c>--minimised</c>, in whatever case Windows hands it back; and not
    /// the entry of another copy of DeskPair, which is somebody else's to keep.
    /// </summary>
    [Theory]
    [InlineData("\"C:\\Program Files\\Sunllo\\DeskPair\\DeskPair.exe\" --minimised", true)]
    [InlineData("\"C:\\Program Files\\Sunllo\\DeskPair\\DeskPair.exe\"", true)]
    [InlineData("\"c:\\program files\\sunllo\\deskpair\\deskpair.exe\" --minimised", true)]
    [InlineData("C:\\Users\\alice\\Downloads\\DeskPair\\DeskPair.exe --minimised", false)]
    [InlineData("\"C:\\Program Files\\Sunllo\\DeskPair\\DeskPair.exe.old\"", false)]
    [InlineData("", false)]
    public void A_login_entry_is_this_copys_only_when_it_starts_this_very_file(string command, bool ours)
    {
        StartupEntry.Starts(command, Installed).ShouldBe(ours);
    }

    /// <summary>A quote in the path cannot end the PowerShell string early and have the rest run as a command.</summary>
    [Fact]
    public void The_program_path_stays_inside_its_quotes()
    {
        FirewallRules.RemoveAllForScript(@"C:\it's here\DeskPair.exe").ShouldContain(@"-Program 'C:\it''s here\DeskPair.exe'");
    }

    /// <summary>
    /// The script runs for real, against this machine's firewall: for a program no rule names it finds none, removes
    /// nothing and says so with exit 0, which it could not do with a word of it wrong. Removing a rule needs an
    /// administrator; this does not, since there is nothing to remove.
    /// </summary>
    [Fact]
    public void Removing_the_rules_of_a_program_that_has_none_finds_none()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // the firewall is a Windows arrangement
        }

        string nowhere = Path.Combine(Path.GetTempPath(), "deskpair-" + Guid.NewGuid().ToString("N"), "DeskPair.exe");

        FirewallRules.RemoveAllFor(nowhere, NullLogger.Instance).ShouldBe(0);
    }
}
