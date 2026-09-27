using DeskPair.Core.Portal;
using DeskPair.Desktop.Localization;

namespace DeskPair.Desktop.Tests;

/// <summary>
/// What a portal failure says, and in whose language.
///
/// The portal serves every copy of the app in the world and answers in English; it has no idea who is
/// reading. So a wrong password reached a Chinese-language window reading "That email address and
/// password do not match", and so did everything else that could go wrong with signing in.
///
/// The code is the part meant to be acted on, and it is what gets translated. The English sentence stays
/// as the fallback, which is the point: the portal can add a code without every installed client having
/// to be updated first, and those clients say something true rather than nothing.
/// </summary>
[Collection("ProcessState")] // touches process-wide state (Strings.Language, Toasts.Current), so never alongside another test that does
public class PortalMessageTests : IDisposable
{
    private readonly string _was = Strings.Language;

    public void Dispose()
    {
        Strings.Language = _was;
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void A_code_the_app_knows_is_said_in_the_readers_language()
    {
        Strings.Language = "zh-TW";

        string message = PortalMessage.For(
            new PortalException("That email address and password do not match.", "bad_credentials"),
            string.Empty);

        message.ShouldBe("電子郵件與密碼不符。");
    }

    /// <summary>
    /// A code from a newer portal falls back to the sentence it came with.
    ///
    /// Strings.Get answers a missing key with the key itself, which is right for a label -- it is visible
    /// and names what is missing -- and would be wrong here: "portal.some_new_reason" tells the reader
    /// nothing, while the English sentence at least says what happened.
    /// </summary>
    [Fact]
    public void A_code_the_app_has_never_heard_of_keeps_the_portals_own_sentence()
    {
        Strings.Language = "zh-TW";

        string message = PortalMessage.For(
            new PortalException("Your account is over its device limit.", "too_many_devices"),
            string.Empty);

        message.ShouldBe("Your account is over its device limit.");
    }

    /// <summary>A failure with no code at all is still a finished sentence, so it is shown as it arrived.</summary>
    [Fact]
    public void A_failure_with_no_code_is_shown_as_it_arrived()
    {
        Strings.Language = "zh-TW";

        PortalMessage.For(new PortalException("Something went wrong."), string.Empty)
            .ShouldBe("Something went wrong.");
    }

    /// <summary>
    /// The two failures that are about reaching the portal name the address they could not reach.
    ///
    /// That address is the whole diagnosis when it is wrong -- which is how a stray "portal.example:21120"
    /// in a config file was spotted at all.
    /// </summary>
    [Theory]
    [InlineData("unreachable")]
    [InlineData("timeout")]
    public void Not_reaching_the_portal_says_which_portal(string code)
    {
        Strings.Language = "zh-TW";

        string message = PortalMessage.For(new PortalException("ignored", code), "portal.example:21120");

        message.ShouldContain("http://portal.example:21120");
        message.ShouldNotContain("ignored");
    }

    /// <summary>An install with nothing configured names the official portal, not an empty space.</summary>
    [Fact]
    public void Not_reaching_the_default_portal_names_the_official_one()
    {
        Strings.Language = "en";

        PortalMessage.For(new PortalException("ignored", "unreachable"), string.Empty)
            .ShouldContain(Core.Update.UpdateEndpoints.OfficialPortal);
    }

    /// <summary>Both languages have every one of these, or one of them falls back to English in use.</summary>
    [Theory]
    [InlineData("unreachable")]
    [InlineData("timeout")]
    [InlineData("bad_credentials")]
    [InlineData("not_verified")]
    [InlineData("disabled")]
    [InlineData("closed")]
    [InlineData("email_taken")]
    [InlineData("weak_password")]
    [InlineData("bad_email")]
    [InlineData("too_many")]
    [InlineData("unlinked")]
    public void Every_refusal_the_portal_can_send_has_both_languages(string code)
    {
        Strings.Has("portal." + code).ShouldBeTrue();

        Strings.Language = "en";
        string english = Strings.Get("portal." + code);
        Strings.Language = "zh-TW";
        string chinese = Strings.Get("portal." + code);

        english.ShouldNotBeEmpty();
        chinese.ShouldNotBeEmpty();
        chinese.ShouldNotBe(english, $"portal.{code} was never translated");
    }
}
