using DeskPair.Core.Testing;
using DeskPair.Platform.Abstractions.Capture;

namespace DeskPair.Core.Tests;

/// <summary>
/// The invariant every consumer of a display list relies on: the index of a display is its position. The
/// failure mode when it is not is a black picture, not an exception, so the rule is pinned here rather
/// than left to whoever next writes an enumerator.
/// </summary>
public class DisplayOrderingTests
{
    private static DisplayDescriptor Display(int index, string name, bool primary, long luid = 0) =>
        new(index, name, index * 1920, 0, 1920, 1080, 1.0, FrameRotation.None, primary, luid);

    /// <summary>
    /// The case that was wrong on X11 and macOS: displays numbered as found, primary not first. Sorting the
    /// primary to the front used to leave display "1" at position 0.
    /// </summary>
    [Fact]
    public void A_primary_found_second_comes_first_and_every_index_is_its_position()
    {
        List<DisplayDescriptor> ordered = DisplayOrdering.PrimaryFirst(
        [
            Display(0, "HDMI-1", primary: false, luid: 11),
            Display(1, "DP-1", primary: true, luid: 22),
            Display(2, "DP-2", primary: false, luid: 33),
        ]);

        ordered.Select(d => d.Name).ShouldBe(["DP-1", "HDMI-1", "DP-2"], "primary first, the rest in the order found");
        ordered.Select(d => d.Index).ShouldBe([0, 1, 2]);
        DisplayOrdering.IsConsistent(ordered).ShouldBeTrue();
        ordered.Select(d => d.AdapterLuid).ShouldBe([22L, 11L, 33L], "native identity survives the renumbering");
        ordered[0].X.ShouldBe(1920, "geometry is untouched");
    }

    [Fact]
    public void A_list_that_is_already_right_is_left_alone()
    {
        DisplayDescriptor[] given = [Display(0, "A", primary: true), Display(1, "B", primary: false)];

        DisplayOrdering.PrimaryFirst(given).ShouldBe(given);
    }

    [Fact]
    public void Without_a_primary_the_order_found_stands()
    {
        List<DisplayDescriptor> ordered = DisplayOrdering.PrimaryFirst([Display(5, "A", false), Display(9, "B", false)]);

        ordered.Select(d => d.Name).ShouldBe(["A", "B"]);
        ordered.Select(d => d.Index).ShouldBe([0, 1]);
    }

    [Fact]
    public void Consistency_is_the_index_equal_to_the_position()
    {
        DisplayOrdering.IsConsistent([]).ShouldBeTrue();
        DisplayOrdering.IsConsistent([Display(0, "A", true)]).ShouldBeTrue();
        DisplayOrdering.IsConsistent([Display(1, "A", true), Display(0, "B", false)]).ShouldBeFalse();
        DisplayOrdering.IsConsistent([Display(0, "A", true), Display(2, "B", false)]).ShouldBeFalse();
    }

    /// <summary>The enumerator every host test runs against must obey the same rule as the real ones.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void The_fake_enumerator_obeys_the_rule(int count)
    {
        var fake = new FakeDisplayEnumerator(count: count);

        DisplayOrdering.IsConsistent(fake.GetDisplays()).ShouldBeTrue();
        fake.GetDisplays()[0].IsPrimary.ShouldBeTrue();
    }
}
