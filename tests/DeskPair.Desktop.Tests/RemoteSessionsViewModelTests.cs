using Microsoft.Extensions.Logging.Abstractions;
using DeskPair.Desktop.Services;
using DeskPair.Desktop.ViewModels;

namespace DeskPair.Desktop.Tests;

/// <summary>
/// Several machines now share one window, so the tab strip is what decides which session is on screen and
/// which are merely open. Nothing here connects to anything: a session that has not been started is still a
/// tab, which is exactly the state the strip has to get right.
/// </summary>
public class RemoteSessionsViewModelTests
{
    private static RemoteSessionViewModel Session(string target) =>
        new(target, "me", new DesktopConfig(), NullLoggerFactory.Instance);

    [Fact]
    public void A_new_session_becomes_the_selected_tab()
    {
        var sessions = new RemoteSessionsViewModel();

        RemoteSessionViewModel first = sessions.Add(Session("111111111"));
        RemoteSessionViewModel second = sessions.Add(Session("222222222"));

        sessions.Sessions.Count.ShouldBe(2);
        sessions.Selected.ShouldBe(second);
        second.IsActive.ShouldBeTrue();
        first.IsActive.ShouldBeFalse("only the tab on screen is drawn");
    }

    /// <summary>The strip is only worth its height once there is something to switch between.</summary>
    [Fact]
    public void The_strip_appears_with_the_second_session()
    {
        var sessions = new RemoteSessionsViewModel();

        sessions.ShowTabs.ShouldBeFalse();
        sessions.Add(Session("111111111"));
        sessions.ShowTabs.ShouldBeFalse();
        sessions.Add(Session("222222222"));
        sessions.ShowTabs.ShouldBeTrue();
    }

    /// <summary>
    /// Connecting again to a machine that is already open is almost always a second click, not a request for
    /// a second session to the same desk.
    /// </summary>
    [Fact]
    public void Opening_the_same_target_twice_selects_the_tab_it_is_already_on()
    {
        var sessions = new RemoteSessionsViewModel();
        RemoteSessionViewModel first = sessions.Add(Session("111111111"));
        sessions.Add(Session("222222222"));

        RemoteSessionViewModel again = sessions.Add(Session("111111111"));

        again.ShouldBe(first);
        sessions.Sessions.Count.ShouldBe(2);
        sessions.Selected.ShouldBe(first);
    }

    [Fact]
    public void A_target_is_matched_regardless_of_case()
    {
        var sessions = new RemoteSessionsViewModel();
        RemoteSessionViewModel first = sessions.Add(Session("desk.local"));

        sessions.Add(Session("DESK.LOCAL")).ShouldBe(first);
        sessions.Sessions.Count.ShouldBe(1);
    }

    /// <summary>Closing the middle of a row lands on a neighbour, not on nothing.</summary>
    [Fact]
    public async Task Closing_a_tab_selects_the_one_next_to_it()
    {
        var sessions = new RemoteSessionsViewModel();
        sessions.Add(Session("111111111"));
        RemoteSessionViewModel middle = sessions.Add(Session("222222222"));
        RemoteSessionViewModel last = sessions.Add(Session("333333333"));

        await sessions.CloseAsync(middle);

        sessions.Sessions.Count.ShouldBe(2);
        sessions.Selected.ShouldBe(last, "the tab that moved into the closed one's place");
        last.IsActive.ShouldBeTrue();
    }

    [Fact]
    public async Task Closing_the_last_tab_empties_the_window()
    {
        var sessions = new RemoteSessionsViewModel();
        RemoteSessionViewModel only = sessions.Add(Session("111111111"));
        bool emptied = false;
        sessions.Emptied += () => emptied = true;

        await sessions.CloseAsync(only);

        sessions.HasSessions.ShouldBeFalse();
        sessions.Selected.ShouldBeNull();
        emptied.ShouldBeTrue();
    }

    [Fact]
    public async Task Closing_a_tab_twice_is_harmless()
    {
        var sessions = new RemoteSessionsViewModel();
        RemoteSessionViewModel only = sessions.Add(Session("111111111"));
        int emptied = 0;
        sessions.Emptied += () => emptied++;

        await sessions.CloseAsync(only);
        await sessions.CloseAsync(only);
        await sessions.CloseAsync(null);

        emptied.ShouldBe(1);
    }

    [Fact]
    public async Task Closing_the_window_ends_every_session()
    {
        var sessions = new RemoteSessionsViewModel();
        sessions.Add(Session("111111111"));
        sessions.Add(Session("222222222"));

        await sessions.CloseAllAsync();

        sessions.Sessions.ShouldBeEmpty();
        sessions.Selected.ShouldBeNull();
        sessions.ShowTabs.ShouldBeFalse();
    }

    /// <summary>A session that disconnects itself takes its own tab with it.</summary>
    [Fact]
    public async Task A_session_that_asks_to_close_loses_its_tab()
    {
        var sessions = new RemoteSessionsViewModel();
        RemoteSessionViewModel first = sessions.Add(Session("111111111"));
        sessions.Add(Session("222222222"));

        await first.DisconnectCommand.ExecuteAsync(null);

        sessions.Sessions.ShouldNotContain(first);
        sessions.Sessions.Count.ShouldBe(1);
    }
}
