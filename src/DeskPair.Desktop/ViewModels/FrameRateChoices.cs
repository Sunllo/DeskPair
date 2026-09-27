using System.Globalization;
using DeskPair.Desktop.Localization;

namespace DeskPair.Desktop.ViewModels;

/// <summary>
/// The frame-rate drop-down on a session's toolbar: item 0 is "automatic" -- the host's own cap, which the host
/// lowers on a congested link -- and the rest are fixed caps. Any quality may have one: the host honours a viewer's
/// frame rate whatever the quality, so the bitrate policy a quality stands for does not have to change with it.
/// Pure, so the list and the selected item can be tested without a window.
/// </summary>
internal static class FrameRateChoices
{
    /// <summary>What is offered: 0 for automatic, then the caps people reach for.</summary>
    public static readonly int[] Offered = [0, 15, 24, 30, 45, 60, 90, 120];

    /// <summary>
    /// The rates to offer, their labels, and which one is <paramref name="current"/>: a rate from the settings page
    /// that is not among the offered ones (any of 5 to 120 can be typed there) is added in its place in the order.
    /// </summary>
    public static (IReadOnlyList<int> Rates, IReadOnlyList<string> Labels, int Selected) Build(int current)
    {
        int wanted = current > 0 ? current : 0;
        List<int> rates = Offered.Contains(wanted) ? [.. Offered] : [.. Offered.Append(wanted).Order()];
        var labels = rates
            .Select(rate => rate == 0 ? Strings.Get("session.frameRate.auto") : rate.ToString(CultureInfo.CurrentCulture))
            .ToList();
        return (rates, labels, rates.IndexOf(wanted));
    }
}
