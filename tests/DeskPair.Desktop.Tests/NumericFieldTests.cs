using DeskPair.Desktop.ViewModels.Settings;

namespace DeskPair.Desktop.Tests;

/// <summary>
/// Settings save as they are typed, so the number boxes have to tolerate every half-finished state a user
/// passes through on the way to a valid number without storing any of them.
/// </summary>
public class NumericFieldTests
{
    [Fact]
    public void A_valid_number_becomes_the_value_and_reports_the_change()
    {
        var field = new NumericField(1, 65535, 21118, "settings.rangeInvalid");
        int changes = 0;
        field.ValueChanged += () => changes++;

        field.Text = "21119";

        field.Value.ShouldBe(21119);
        field.Problem.ShouldBeNull();
        changes.ShouldBe(1);
    }

    [Fact]
    public void An_empty_box_is_someone_still_typing_not_an_error()
    {
        var field = new NumericField(1, 65535, 21118, "settings.rangeInvalid");

        field.Text = string.Empty;

        field.Value.ShouldBe(21118);
        field.Problem.ShouldBeNull();
    }

    [Fact]
    public void A_number_on_the_way_to_a_valid_one_keeps_the_last_good_value()
    {
        var field = new NumericField(100, 200_000, 8000, "settings.rangeInvalid");
        int changes = 0;
        field.ValueChanged += () => changes++;

        field.Text = "1"; // below the minimum, but only because the user is not finished
        field.Value.ShouldBe(8000);
        changes.ShouldBe(0);

        field.Text = "12000";
        field.Value.ShouldBe(12000);
        changes.ShouldBe(1);
    }

    [Fact]
    public void Out_of_range_says_so_without_changing_what_is_stored()
    {
        var field = new NumericField(5, 300, 30, "settings.rangeInvalid");

        field.Text = "900";

        field.Value.ShouldBe(30);
        field.Problem.ShouldNotBeNull();
        field.Problem.ShouldContain("300");
    }

    [Fact]
    public void Text_that_is_not_a_number_at_all_is_a_problem()
    {
        var field = new NumericField(5, 300, 30, "settings.rangeInvalid");

        field.Text = "abc";

        field.Value.ShouldBe(30);
        field.Problem.ShouldNotBeNull();
    }

    [Fact]
    public void Retyping_the_same_number_does_not_report_a_change()
    {
        var field = new NumericField(1, 100, 10, "settings.rangeInvalid");
        int changes = 0;
        field.ValueChanged += () => changes++;

        field.Text = " 10 ";

        changes.ShouldBe(0);
    }

    [Fact]
    public void Reset_puts_a_stored_value_back_without_reporting_a_change()
    {
        var field = new NumericField(1, 100, 10, "settings.rangeInvalid");
        field.Text = "abc";
        int changes = 0;
        field.ValueChanged += () => changes++;

        field.Reset(42);

        field.Text.ShouldBe("42");
        field.Value.ShouldBe(42);
        field.Problem.ShouldBeNull();
        changes.ShouldBe(0);
    }

    [Fact]
    public void A_stored_value_outside_the_range_is_pulled_back_into_it()
    {
        var field = new NumericField(1, 100, 500, "settings.rangeInvalid");

        field.Value.ShouldBe(100);

        field.Reset(-7);
        field.Value.ShouldBe(1);
    }
}
