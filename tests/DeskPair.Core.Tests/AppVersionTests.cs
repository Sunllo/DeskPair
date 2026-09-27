using DeskPair.Core.Update;

namespace DeskPair.Core.Tests;

/// <summary>
/// Comparing what the app calls itself with what the portal says is newest.
///
/// <c>App.Version</c> is a label with a commit on the end. Everything here exists because comparing those
/// labels as strings gives the wrong answer in ways that are individually obvious and collectively easy to
/// ship: 0.10.0 would be older than 0.9.0, and every development build would be newer than itself.
/// </summary>
public class AppVersionTests
{
    [Fact]
    public void A_build_sha_is_not_part_of_the_version()
    {
        // The exact shape App.Version produces.
        AppVersion.TryParse("0.2.0+8ec4feb", out AppVersion parsed).ShouldBeTrue();
        parsed.ToString().ShouldBe("0.2.0");

        AppVersion.IsNewer("0.2.0+deadbee", "0.2.0+8ec4feb").ShouldBeFalse();
    }

    [Fact]
    public void Ten_is_newer_than_nine()
    {
        // The string comparison trap, and the reason this type exists at all.
        AppVersion.IsNewer("0.10.0", "0.9.0").ShouldBeTrue();
        AppVersion.IsNewer("0.9.0", "0.10.0").ShouldBeFalse();
    }

    [Fact]
    public void A_higher_minor_beats_a_higher_patch() =>
        AppVersion.IsNewer("0.3.0", "0.2.99").ShouldBeTrue();

    [Fact]
    public void The_same_version_is_not_newer() =>
        AppVersion.IsNewer("0.2.0", "0.2.0").ShouldBeFalse();

    [Fact]
    public void An_older_version_on_the_portal_is_not_an_update()
    {
        // A rolled-back release must not nag everybody who already has the newer one.
        AppVersion.IsNewer("0.1.0", "0.2.0").ShouldBeFalse();
    }

    [Fact]
    public void A_pre_release_is_older_than_its_release()
    {
        AppVersion.IsNewer("0.3.0", "0.3.0-rc.1").ShouldBeTrue();
        AppVersion.IsNewer("0.3.0-rc.1", "0.3.0").ShouldBeFalse();
    }

    [Fact]
    public void Pre_releases_compare_by_identifier()
    {
        AppVersion.IsNewer("0.3.0-rc.10", "0.3.0-rc.2").ShouldBeTrue();
        AppVersion.IsNewer("0.3.0-beta", "0.3.0-alpha").ShouldBeTrue();

        // A number ranks below a word, and a longer list wins when everything shared is equal.
        AppVersion.IsNewer("0.3.0-rc", "0.3.0-1").ShouldBeTrue();
        AppVersion.IsNewer("0.3.0-rc.1.1", "0.3.0-rc.1").ShouldBeTrue();
    }

    [Fact]
    public void A_two_part_version_reads_as_a_patch_of_zero()
    {
        AppVersion.TryParse("0.3", out AppVersion two).ShouldBeTrue();
        AppVersion.TryParse("0.3.0", out AppVersion three).ShouldBeTrue();
        two.ShouldBe(three);
    }

    [Fact]
    public void A_leading_v_is_accepted()
    {
        // A tag name pasted into a manifest should not silently disable the check.
        AppVersion.TryParse("v0.3.0", out AppVersion parsed).ShouldBeTrue();
        parsed.ToString().ShouldBe("0.3.0");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("0.2.0.1")]
    [InlineData("1..2")]
    [InlineData("-1.0.0")]
    [InlineData("0.2.0-")]
    [InlineData("0.2.0-rc..1")]
    [InlineData("0.2.0-rc!")]
    [InlineData("99999999999.0.0")]
    [InlineData("0. 2.0")]
    [InlineData("+1.0.0")]
    public void Anything_unreadable_is_refused(string? text) =>
        AppVersion.TryParse(text, out _).ShouldBeFalse();

    [Fact]
    public void A_version_that_cannot_be_read_never_hides_or_invents_an_update()
    {
        // Both directions. A portal answering nonsense produces silence; a build whose own version cannot
        // be read does not start claiming every release is an update.
        AppVersion.IsNewer("nonsense", "0.2.0").ShouldBeFalse();
        AppVersion.IsNewer("0.3.0", "nonsense").ShouldBeFalse();
        AppVersion.IsNewer(null, null).ShouldBeFalse();
    }

    [Fact]
    public void Ordering_is_total_and_consistent()
    {
        string[] ascending = ["0.9.0", "0.10.0-alpha", "0.10.0-rc.2", "0.10.0-rc.10", "0.10.0", "1.0.0"];
        for (int i = 1; i < ascending.Length; i++)
        {
            AppVersion.IsNewer(ascending[i], ascending[i - 1])
                .ShouldBeTrue($"{ascending[i]} should be newer than {ascending[i - 1]}");
            AppVersion.IsNewer(ascending[i - 1], ascending[i])
                .ShouldBeFalse($"{ascending[i - 1]} should not be newer than {ascending[i]}");
        }
    }
}
