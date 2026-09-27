using DeskPair.Protocol.Messages;

namespace DeskPair.Desktop.ViewModels;

/// <summary>
/// Matches a session's display windows to what the host now streams, by name.
///
/// The host's answer to a subscription carries each display's index and its name. Indices move when a monitor
/// is pulled -- everything after it moves down one -- and the answer can arrive before the new display list
/// does, so a window matched by index could be closed, or shown the wrong display, for the moment in between.
/// Names do not move.
/// </summary>
public static class DisplayWindowPlan
{
    /// <summary>What to do with a session's windows after <paramref name="answer"/>.</summary>
    /// <param name="Keep">Each window that stays, by display name, with the display's index now.</param>
    /// <param name="Close">Windows whose display the host no longer streams: pulled, or refused.</param>
    /// <param name="Tab">The index the session's own tab shows now; -1 when it shows nothing.</param>
    public sealed record Outcome(IReadOnlyDictionary<string, int> Keep, IReadOnlyList<string> Close, int Tab);

    /// <param name="windows">Names of the displays open in windows of their own.</param>
    /// <param name="tab">Name of the display the session's tab shows, or null.</param>
    /// <param name="answer">The host's answer, with names alongside the indices.</param>
    public static Outcome Match(IReadOnlyCollection<string> windows, string? tab, DisplaySubscription answer)
    {
        var streamed = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < answer.Displays.Count && i < answer.Names.Count; i++)
        {
            if (answer.Names[i].Length > 0)
            {
                streamed[answer.Names[i]] = answer.Displays[i];
            }
        }

        // The tab keeps its display when that is still streamed; otherwise it follows the host's focus.
        int tabIndex = tab is not null && streamed.TryGetValue(tab, out int stays)
            ? stays
            : answer.Focus >= 0 ? answer.Focus : answer.Displays.Count > 0 ? answer.Displays[0] : -1;

        var keep = new Dictionary<string, int>(StringComparer.Ordinal);
        var close = new List<string>();
        foreach (string name in windows)
        {
            // One display, one place: a window showing what the tab has just moved to goes.
            if (streamed.TryGetValue(name, out int index) && index != tabIndex)
            {
                keep[name] = index;
            }
            else
            {
                close.Add(name);
            }
        }

        return new Outcome(keep, close, tabIndex);
    }

    /// <summary>What to ask the host for: the tab's display, then every window's, with the focus on <paramref name="focus"/> when it is one of them.</summary>
    public static (int[] Displays, int Focus) Request(int tab, IEnumerable<int> windows, int focus)
    {
        var set = new List<int>();
        if (tab >= 0)
        {
            set.Add(tab);
        }

        foreach (int w in windows)
        {
            if (w >= 0 && !set.Contains(w))
            {
                set.Add(w);
            }
        }

        return ([.. set], set.Contains(focus) ? focus : set.Count > 0 ? set[0] : -1);
    }
}
