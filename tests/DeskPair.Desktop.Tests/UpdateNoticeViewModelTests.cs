using DeskPair.Desktop.Services;
using DeskPair.Desktop.ViewModels;

namespace DeskPair.Desktop.Tests;

/// <summary>
/// When the banner appears, and when it stays away.
///
/// Dismissal lives here rather than in the check service on purpose: the service reports facts, and what
/// somebody waved away is not one. That split is what these tests are really pinning.
/// </summary>
[Collection("ProcessState")] // touches process-wide state (Strings.Language, Toasts.Current), so never alongside another test that does
public class UpdateNoticeViewModelTests
{
    private static (UpdateNoticeViewModel Notice, Func<string> Dismissed) Make(string dismissed = "")
    {
        string remembered = dismissed;
        var notice = new UpdateNoticeViewModel(() => remembered, v => remembered = v);
        return (notice, () => remembered);
    }

    private static UpdateState Available(string version) => new()
    {
        LastChecked = DateTimeOffset.UnixEpoch,
        LatestVersion = version,
        IsUpdateAvailable = true,
        DownloadUrl = "https://portal.test/download",
    };

    [Fact]
    public void An_available_update_is_shown()
    {
        (UpdateNoticeViewModel notice, _) = Make();

        notice.Show(Available("0.3.0"));

        notice.IsVisible.ShouldBeTrue();
        notice.Text.ShouldContain("0.3.0");
    }

    [Fact]
    public void Nothing_is_shown_when_there_is_no_update()
    {
        (UpdateNoticeViewModel notice, _) = Make();

        notice.Show(UpdateState.Unknown);

        notice.IsVisible.ShouldBeFalse();
        notice.Text.ShouldBeEmpty();
    }

    [Fact]
    public void Nothing_is_shown_for_a_check_that_failed()
    {
        (UpdateNoticeViewModel notice, _) = Make();

        notice.Show(new UpdateState { LastChecked = DateTimeOffset.UnixEpoch, Problem = "could not reach it" });

        notice.IsVisible.ShouldBeFalse();
    }

    [Fact]
    public void Dismissing_remembers_the_version_and_hides_it()
    {
        (UpdateNoticeViewModel notice, Func<string> dismissed) = Make();
        notice.Show(Available("0.3.0"));

        notice.DismissCommand.Execute(null);

        notice.IsVisible.ShouldBeFalse();
        dismissed().ShouldBe("0.3.0");
    }

    [Fact]
    public void A_dismissed_version_does_not_come_back()
    {
        (UpdateNoticeViewModel notice, _) = Make(dismissed: "0.3.0");

        notice.Show(Available("0.3.0"));

        notice.IsVisible.ShouldBeFalse();
    }

    [Fact]
    public void A_newer_version_comes_back_after_one_was_dismissed()
    {
        // Dismissing silences this version, not every future one.
        (UpdateNoticeViewModel notice, _) = Make(dismissed: "0.3.0");

        notice.Show(Available("0.3.1"));

        notice.IsVisible.ShouldBeTrue();
        notice.Text.ShouldContain("0.3.1");
    }

    [Fact]
    public void Opening_a_download_page_that_cannot_be_reached_says_the_address()
    {
        // The URL is built from configuration, so it is well-formed; the browser is what fails. Silence
        // would leave somebody pressing a button that does nothing.
        (UpdateNoticeViewModel notice, _) = Make();
        notice.Show(Available("0.3.0") with { DownloadUrl = "javascript:alert(1)" });

        notice.GetCommand.Execute(null);

        notice.Notice.ShouldContain("javascript:alert(1)");
        notice.IsVisible.ShouldBeTrue("a failed launch is not a reason to hide the news");
    }
}
