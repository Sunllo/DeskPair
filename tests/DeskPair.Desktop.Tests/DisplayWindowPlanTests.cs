using DeskPair.Desktop.ViewModels;
using DeskPair.Protocol.Messages;

namespace DeskPair.Desktop.Tests;

/// <summary>Display windows matched to the host's answer by name, and the set a session asks for.</summary>
public class DisplayWindowPlanTests
{
    private static DisplaySubscription Answer(int focus, params (int Index, string Name)[] displays)
    {
        var answer = new DisplaySubscription { Focus = focus };
        answer.Displays.AddRange(displays.Select(d => d.Index));
        answer.Names.AddRange(displays.Select(d => d.Name));
        return answer;
    }

    /// <summary>
    /// The middle of three monitors is pulled. The window for the third now shows display 1, found by its name
    /// -- the answer arrives before the new list, and by index the window would have been closed.
    /// </summary>
    [Fact]
    public void A_pulled_monitor_moves_the_windows_after_it_by_name()
    {
        DisplayWindowPlan.Outcome plan = DisplayWindowPlan.Match(["B", "C"], tab: "A", Answer(0, (0, "A"), (1, "C")));

        plan.Keep.ShouldBe(new Dictionary<string, int> { ["C"] = 1 });
        plan.Close.ShouldBe(["B"]);
        plan.Tab.ShouldBe(0);
    }

    /// <summary>A refusal carries the set the host kept; the window just opened for a display it would not add closes.</summary>
    [Fact]
    public void A_refused_window_closes_and_the_rest_stay()
    {
        var refused = Answer(0, (0, "A"), (1, "B"));
        refused.Failure = "at most 2";
        DisplayWindowPlan.Outcome plan = DisplayWindowPlan.Match(["B", "C"], tab: "A", refused);

        plan.Keep.ShouldBe(new Dictionary<string, int> { ["B"] = 1 });
        plan.Close.ShouldBe(["C"]);
    }

    [Fact]
    public void A_tab_whose_display_went_follows_the_focus()
    {
        DisplayWindowPlan.Outcome plan = DisplayWindowPlan.Match(["B"], tab: "C", Answer(0, (0, "A"), (1, "B")));

        plan.Tab.ShouldBe(0);
        plan.Keep.ShouldBe(new Dictionary<string, int> { ["B"] = 1 });
    }

    /// <summary>One display, one place: if the tab lands on what a window was showing, the window goes.</summary>
    [Fact]
    public void A_window_showing_the_tabs_new_display_closes()
    {
        DisplayWindowPlan.Outcome plan = DisplayWindowPlan.Match(["B"], tab: "gone", Answer(1, (1, "B")));

        plan.Tab.ShouldBe(1);
        plan.Close.ShouldBe(["B"]);
        plan.Keep.ShouldBeEmpty();
    }

    [Fact]
    public void The_request_is_the_tab_then_the_windows_with_a_focus_that_is_one_of_them()
    {
        (int[] displays, int focus) = DisplayWindowPlan.Request(0, [2, 1, 2], focus: 2);
        displays.ShouldBe([0, 2, 1]);
        focus.ShouldBe(2);

        (displays, focus) = DisplayWindowPlan.Request(0, [1], focus: 5);
        displays.ShouldBe([0, 1]);
        focus.ShouldBe(0, "a focus on nothing streamed falls back to the tab");

        (displays, focus) = DisplayWindowPlan.Request(-1, [], focus: -1);
        displays.ShouldBeEmpty();
        focus.ShouldBe(-1);
    }
}
