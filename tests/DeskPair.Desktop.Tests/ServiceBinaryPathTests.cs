using DeskPair.Desktop.Services;

namespace DeskPair.Desktop.Tests;

/// <summary>
/// Whether a registered service command line runs this program. The case that matters: the service was turned on
/// from a zip folder (or a test build), the MSI was installed afterwards, and the installer must notice that the
/// service still starts the other copy.
/// </summary>
public class ServiceBinaryPathTests
{
    [Fact]
    public void The_command_line_quotes_the_program_and_adds_the_service_switch() =>
        ServiceBinaryPath.For(@"C:\Program Files\Sunllo\DeskPair\DeskPair.exe")
            .ShouldBe("\"C:\\Program Files\\Sunllo\\DeskPair\\DeskPair.exe\" --service");

    [Theory]
    [InlineData("\"C:\\Program Files\\Sunllo\\DeskPair\\DeskPair.exe\" --service", @"C:\Program Files\Sunllo\DeskPair\DeskPair.exe")]
    [InlineData(@"C:\DeskPair\DeskPair.exe --service", @"C:\DeskPair\DeskPair.exe")]
    [InlineData(@"C:\DeskPair\DeskPair.exe", @"C:\DeskPair\DeskPair.exe")]
    [InlineData("  \"C:\\DeskPair\\DeskPair.exe\"  ", @"C:\DeskPair\DeskPair.exe")]
    public void The_executable_is_the_first_token_quoted_or_not(string commandLine, string exe) =>
        ServiceBinaryPath.ExecutableOf(commandLine).ShouldBe(exe);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_command_line_means_no_executable(string? commandLine) =>
        ServiceBinaryPath.ExecutableOf(commandLine).ShouldBeNull();

    [Fact]
    public void A_service_registered_against_this_program_is_left_alone_whatever_the_spelling()
    {
        string exe = Path.Combine(Path.GetTempPath(), "Sunllo", "DeskPair", "DeskPair.exe");
        ServiceBinaryPath.Runs(ServiceBinaryPath.For(exe), exe).ShouldBeTrue();
        ServiceBinaryPath.Runs(ServiceBinaryPath.For(exe.ToUpperInvariant()), exe).ShouldBeTrue("case does not make it another file");
        ServiceBinaryPath.Runs(exe + " --service", exe).ShouldBeTrue("an unquoted path is the same file");
    }

    [Fact]
    public void A_service_registered_against_another_copy_is_not_running_this_program()
    {
        string installed = Path.Combine(Path.GetTempPath(), "Program Files", "Sunllo", "DeskPair", "DeskPair.exe");
        string unpacked = Path.Combine(Path.GetTempPath(), "Downloads", "DeskPair-0.5.1-win-x64", "DeskPair.exe");
        ServiceBinaryPath.Runs(ServiceBinaryPath.For(unpacked), installed).ShouldBeFalse();
        ServiceBinaryPath.Runs(null, installed).ShouldBeFalse();
    }

    /// <summary>
    /// Guards the MSI: the action exists, runs as LocalSystem, never fails the install, and is sequenced after
    /// InstallFiles (so DeskPair.exe is the new one) and only while a service is registered.
    /// </summary>
    [Fact]
    public void The_installer_adopts_the_service_after_the_files_are_in_place()
    {
        string package = File.ReadAllText(Path.Combine(PackagingFolder(), "Package.wxs"));
        string action = package[package.IndexOf("<CustomAction Id=\"AdoptService\"", StringComparison.Ordinal)..];
        action = action[..action.IndexOf("/>", StringComparison.Ordinal)];
        action.ShouldContain("ExeCommand=\"--adopt-service\"");
        action.ShouldContain("Execute=\"deferred\"");
        action.ShouldContain("Impersonate=\"no\"", customMessage: "runs as LocalSystem, like the service it changes");
        action.ShouldContain("Return=\"ignore\"", customMessage: "a failure is logged, not an install that rolls back");
        package.ShouldContain("<Custom Action=\"AdoptService\" After=\"InstallFiles\" Condition=\"DESKPAIR_SERVICE AND NOT REMOVE~=&quot;ALL&quot;\" />");
        Program.ParseRole(["--adopt-service"]).ShouldBe(AppRole.AdoptService);
    }

    private static string PackagingFolder()
    {
        string dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "DeskPair.slnx")))
        {
            dir = Path.GetDirectoryName(dir) ?? throw new DirectoryNotFoundException("DeskPair.slnx not found above " + AppContext.BaseDirectory);
        }

        return Path.Combine(dir, "packaging", "windows");
    }
}
