using DeskPair.Desktop.Services.Update;

namespace DeskPair.Desktop.Tests;

/// <summary>
/// The marker the .deb, .rpm and Arch packages put beside the program. With it the app announces a new version and
/// leaves installing it to the package manager; without it the app updates itself, as it always has.
/// </summary>
public sealed class PackagedInstallTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("deskpair-packaged-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void No_marker_is_an_install_the_app_updates_itself()
    {
        PackagedInstall.FormatIn(_dir).ShouldBeNull();
    }

    [Theory]
    [InlineData("deb", "deb")]
    [InlineData("rpm\n", "rpm")]
    [InlineData("archlinux", "archlinux")]
    [InlineData("", "package")]
    public void The_marker_names_the_format_and_an_empty_one_still_counts(string content, string format)
    {
        File.WriteAllText(Path.Combine(_dir, "packaged"), content);

        PackagedInstall.FormatIn(_dir).ShouldBe(format);
    }

    [Fact]
    public void The_test_host_itself_is_not_a_packaged_install()
    {
        // The property every update check reads. A marker left in the build output would silently turn off
        // installing updates for every test that goes through UpdateCheckService.
        PackagedInstall.Format.ShouldBeNull();
    }
}
