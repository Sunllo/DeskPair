using DeskPair.Desktop.Localization;
using DeskPair.Desktop.Services;
using DeskPair.Desktop.ViewModels.Settings;

namespace DeskPair.Desktop.Tests;

/// <summary>
/// What a settings page says, and how long it says it for.
///
/// Every passing message goes to the one place that takes it away again after ten seconds. The general
/// page did not: it wrote into a Problem property bound to a red line at the bottom of the page, and
/// nothing ever cleared it. On a page somebody sets once and leaves, "saved, the new language applies to
/// windows opened from now on" then sat under the recording settings in red for the rest of the session,
/// reading as a complaint about them.
/// </summary>
[Collection("ProcessState")] // touches process-wide state (Strings.Language, Toasts.Current), so never alongside another test that does
public class SettingsNoticeTests : IDisposable
{
    private readonly string _was = Strings.Language;

    public void Dispose()
    {
        Strings.Language = _was;
        Toasts.Current.Hide();
        GC.SuppressFinalize(this);
    }

    private static GeneralSettingsViewModel Loaded()
    {
        var vm = new GeneralSettingsViewModel();
        vm.Load(new DesktopConfig(), host: null);
        return vm;
    }

    [Fact]
    public void Changing_the_language_is_said_once_and_taken_away_again()
    {
        Strings.Language = "en";
        GeneralSettingsViewModel general = Loaded();

        general.SelectedLanguage = general.Languages.First(o => o.Code == "zh-TW");

        Toasts.Current.IsVisible.ShouldBeTrue();
        Toasts.Current.Message.ShouldBe(Strings.Get("settings.languageRestart"));

        // A confirmation, not a complaint: it used to be shown in the colour reserved for things that
        // went wrong, because the only line on the page able to show it was the one for problems.
        Toasts.Current.IsProblem.ShouldBeFalse();
    }

    /// <summary>
    /// Saying something is not a setting changing, on any section.
    ///
    /// Sections list the properties that must not cause a save, and four of them wrote a list that did
    /// not mention Notice -- harmlessly, only because none of those four ever set one. The general page
    /// was about to become the fifth and the first to actually do it, which would have written
    /// config.json every time it said anything at all.
    /// </summary>
    [Fact]
    public void Saying_something_never_counts_as_a_setting_changing()
    {
        GeneralSettingsViewModel general = Loaded();
        var saves = 0;
        ((ISettingsSection)general).Changed += () => saves++;

        general.Notice = "anything at all";
        general.NoticeIsFailure = true;

        saves.ShouldBe(0);
    }

    /// <summary>And a real setting still does, or nothing on the page would ever be written.</summary>
    [Fact]
    public void A_setting_changing_still_counts()
    {
        GeneralSettingsViewModel general = Loaded();
        var saves = 0;
        ((ISettingsSection)general).Changed += () => saves++;

        general.DeviceName = "Front desk";

        saves.ShouldBe(1);
    }
}
