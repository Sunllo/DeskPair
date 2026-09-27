namespace DeskPair.Platform.Abstractions.Capture;

/// <summary>
/// The one order every enumerator hands out, and the invariant that goes with it.
///
/// <see cref="DisplayDescriptor.Index"/> is what travels: the viewer's picker, the <c>SwitchDisplay</c> request,
/// the <c>display</c> byte on every video packet and the mouse event's target all carry it, and the host side
/// resolves it by position in the list the enumerator returned. So the index of a display <b>must</b> equal its
/// position in that list. An enumerator that numbers its displays as it finds them and then sorts them
/// breaks this, and the failure is not an error but a black picture: the host encodes display 1 as
/// "display 0" and the viewer, told to expect stream 1, throws every frame away.
///
/// The primary display goes first, so a caller that takes the head of the list gets the screen the user
/// is most likely looking at; the rest keep the order they were found in. Native identity lives in
/// <see cref="DisplayDescriptor.Name"/> and <see cref="DisplayDescriptor.AdapterLuid"/>, so nothing is lost
/// by renumbering.
/// </summary>
public static class DisplayOrdering
{
    /// <summary>Primary first, then the given order, with <see cref="DisplayDescriptor.Index"/> rewritten to the position.</summary>
    public static List<DisplayDescriptor> PrimaryFirst(IEnumerable<DisplayDescriptor> displays) =>
        [.. displays.OrderByDescending(d => d.IsPrimary).Select((d, i) => d with { Index = i })];

    /// <summary>True when every display's index is its position, which is what every consumer assumes.</summary>
    public static bool IsConsistent(IReadOnlyList<DisplayDescriptor> displays)
    {
        for (int i = 0; i < displays.Count; i++)
        {
            if (displays[i].Index != i)
            {
                return false;
            }
        }

        return true;
    }
}
