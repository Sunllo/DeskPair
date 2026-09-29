using DeskPair.Desktop.Localization;
using DeskPair.Desktop.ViewModels;

namespace DeskPair.Desktop.Tests;

/// <summary>The toolbar's frame-rate list: automatic first, the settings page's rate kept when it is not a usual one.</summary>
[Collection("ProcessState")] // reads the string table, which the language tests move
public sealed class FrameRateChoicesTests
{
    /// <summary>Whatever language the process is in: the label is what the table says, not a fixed word.</summary>
    private static string Auto => Strings.Get("session.frameRate.auto");

    [Fact]
    public void Automatic_leads_and_is_chosen_when_nothing_was_asked()
    {
        (IReadOnlyList<int> rates, IReadOnlyList<string> labels, int selected) = FrameRateChoices.Build(0);

        rates.ShouldBe(FrameRateChoices.Offered);
        rates[0].ShouldBe(0, "0 asks the host to choose");
        labels.ShouldBe([Auto, "15", "24", "30", "45", "60", "90", "120"]);
        selected.ShouldBe(0);
        FrameRateChoices.Build(-5).Selected.ShouldBe(0);
    }

    [Fact]
    public void A_usual_rate_is_selected_where_it_is()
    {
        (IReadOnlyList<int> rates, _, int selected) = FrameRateChoices.Build(60);

        rates[selected].ShouldBe(60);
        rates.Count.ShouldBe(FrameRateChoices.Offered.Length, "nothing added");
    }

    /// <summary>Anything from 5 to 120 can be typed on the settings page; the toolbar shows it rather than something else.</summary>
    [Fact]
    public void Another_rate_from_the_settings_page_joins_the_list_in_order()
    {
        (IReadOnlyList<int> rates, IReadOnlyList<string> labels, int selected) = FrameRateChoices.Build(20);

        rates.ShouldBe([0, 15, 20, 24, 30, 45, 60, 90, 120]);
        labels[selected].ShouldBe("20");
        rates[selected].ShouldBe(20);
    }
}
