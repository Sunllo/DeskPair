using DeskPair.Desktop.Services;
using DeskPair.Desktop.ViewModels.Settings;
using DeskPair.Protocol.Ipc;

namespace DeskPair.Desktop.Tests;

/// <summary>
/// With unattended access installed, the settings page asks the daemon's engine to have the user allow sharing their
/// Wayland desktop; the answer comes back as numbers, which have to read as what the page says.
/// </summary>
public class DesktopSharingAnswerTests
{
    /// <summary>The daemon's answer to asking, as the settings page reads it; zero outcome means nobody was asked.</summary>
    [Fact]
    public void The_daemons_answer_reads_as_the_settings_page_says_it()
    {
        SecuritySettingsViewModel.OutcomeOf(new DesktopSharingState { Outcome = 1 }).Outcome.ShouldBe(WaylandAskOutcome.Allowed);
        SecuritySettingsViewModel.OutcomeOf(new DesktopSharingState { Outcome = 2 }).Outcome.ShouldBe(WaylandAskOutcome.WatchOnly);
        SecuritySettingsViewModel.OutcomeOf(new DesktopSharingState { Outcome = 3 }).Outcome.ShouldBe(WaylandAskOutcome.Declined);
        SecuritySettingsViewModel.OutcomeOf(new DesktopSharingState { Outcome = 4 }).Outcome.ShouldBe(WaylandAskOutcome.Unanswered);
        SecuritySettingsViewModel.OutcomeOf(new DesktopSharingState { Outcome = 5, Detail = "why" }).ShouldBe((WaylandAskOutcome.Failed, "why"));
        SecuritySettingsViewModel.OutcomeOf(new DesktopSharingState()).Outcome.ShouldBe(WaylandAskOutcome.Failed, "an engine that did not ask");
        SecuritySettingsViewModel.OutcomeOf(null).Outcome.ShouldBe(WaylandAskOutcome.Failed);

        SecuritySettingsViewModel.StateOf(new DesktopSharingState { State = 3 }).ShouldBe(WaylandSharingState.Allowed);
        SecuritySettingsViewModel.StateOf(new DesktopSharingState { State = 2 }).ShouldBe(WaylandSharingState.WatchOnly);
        SecuritySettingsViewModel.StateOf(new DesktopSharingState { State = 0 }).ShouldBe(WaylandSharingState.NotYet);
    }
}
