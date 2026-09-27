using DeskPair.Desktop.Services;

namespace DeskPair.Desktop.Tests;

/// <summary>
/// What this application is willing to hand to the desktop to open.
///
/// Nothing is launched in any of these: they exercise the check, which is the part that matters. The
/// update manifest goes unsigned on the strength of the client never opening a URL that came over the
/// network, and this is the second half of that argument -- if such a URL ever reached here, it would
/// have to be a web page on the expected host or it goes nowhere.
/// </summary>
public class LauncherTests
{
    private static bool Allowed(string? url, string? host = null) =>
        Launcher.IsSafeUrl(url, host, out _);

    [Theory]
    [InlineData("https://deskpair.app/download")]
    [InlineData("http://localhost:21120/download")]
    [InlineData("https://portal.example.test:8443/download?x=1#y")]
    public void A_web_page_is_allowed(string url) => Allowed(url).ShouldBeTrue();

    [Theory]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ms-settings:windowsupdate")]
    [InlineData("vscode://file/etc/passwd")]
    [InlineData("mailto:someone@example.test")]
    [InlineData("ftp://example.test/thing")]
    [InlineData("\\\\server\\share")]
    [InlineData("C:\\Windows")]
    [InlineData("//evil.example/path")]
    [InlineData("deskpair.app/download")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Anything_that_is_not_a_web_page_is_refused(string? url) => Allowed(url).ShouldBeFalse();

    [Fact]
    public void A_url_carrying_a_user_name_is_refused()
    {
        // https://deskpair.app@evil.example/ reads as deskpair.app and goes somewhere else entirely.
        Allowed("https://deskpair.app@evil.example/download").ShouldBeFalse();
    }

    [Theory]
    [InlineData("https://deskpair.app/\ndownload")]
    [InlineData("https://deskpair.app/\rdownload")]
    [InlineData("https://deskpair.app/\u0000download")]
    public void A_url_with_a_control_character_is_refused(string url) => Allowed(url).ShouldBeFalse();

    [Fact]
    public void A_host_that_is_not_the_expected_one_is_refused()
    {
        Allowed("https://deskpair.app/download", "deskpair.app").ShouldBeTrue();
        Allowed("https://evil.example/download", "deskpair.app").ShouldBeFalse();
    }

    [Fact]
    public void The_expected_host_comparison_ignores_case() =>
        Allowed("https://DeskPair.App/download", "deskpair.app").ShouldBeTrue();

    [Fact]
    public void The_parsed_url_is_handed_back_only_when_it_passed()
    {
        Launcher.IsSafeUrl("https://deskpair.app/download", null, out Uri? ok).ShouldBeTrue();
        ok.ShouldNotBeNull();

        Launcher.IsSafeUrl("javascript:alert(1)", null, out Uri? refused).ShouldBeFalse();
        refused.ShouldBeNull();
    }
}
