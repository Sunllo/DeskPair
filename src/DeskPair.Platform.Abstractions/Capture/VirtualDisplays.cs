namespace DeskPair.Platform.Abstractions.Capture;

/// <summary>What became of a request to change the set of displays or what one can show.</summary>
/// <param name="Succeeded">True when it was done.</param>
/// <param name="Failure">Why not, in words fit for a viewer's screen; null on success.</param>
public readonly record struct DisplayActionResult(bool Succeeded, string? Failure)
{
    public static DisplayActionResult Done => new(true, null);

    public static DisplayActionResult Refused(string why) => new(false, why);
}

/// <summary>
/// Plugs in displays that do not exist, for a viewer who wants a second screen the host does not have, or a
/// host with no screen at all.
///
/// A change of topology, with a lifetime, which is why it is not part of <see cref="IDisplayModeSwitcher"/>: that
/// one answers what a display can be set to. Only Windows has one (an indirect display driver). Linux can teach a
/// real output a new size (<see cref="IArbitraryModeSink"/>) but never create a display, and macOS has no
/// supported way to do either.
///
/// What was added is reported by the <see cref="IDisplayEnumerator"/> like any other display, once it is there;
/// the host waits for it rather than trusting that it will appear.
/// </summary>
public interface IVirtualDisplayProvider
{
    /// <summary>Null when displays can be added here; otherwise why not, in words fit for a viewer's screen.</summary>
    string? UnavailableReason { get; }

    /// <summary>How many displays this provider has added that are still there.</summary>
    int Count { get; }

    /// <summary>Whether <paramref name="display"/> is one this provider added.</summary>
    bool IsVirtual(DisplayDescriptor display);

    /// <summary>
    /// Plugs in one more display, at <paramref name="mode"/> when one is given. <paramref name="sizes"/> are others the
    /// viewer expects to ask for later (its screens, say): a platform that fixes a display's sizes when it is plugged in
    /// (Windows) makes sure those are among them.
    /// </summary>
    Task<DisplayActionResult> AddAsync(DisplayMode? mode, IReadOnlyList<DisplayMode> sizes, CancellationToken ct);

    /// <summary>Unplugs a display this provider added.</summary>
    Task<DisplayActionResult> RemoveAsync(DisplayDescriptor display, CancellationToken ct);

    /// <summary>Unplugs everything this provider added: the last viewer has gone, or the host is stopping.</summary>
    Task RemoveAllAsync(CancellationToken ct);
}

/// <summary>
/// Teaches a display a size it did not advertise, so a viewer can have the host's screen at exactly the size of
/// its own window.
///
/// Separate from <see cref="IDisplayModeSwitcher"/> because it changes that one's answer rather than acting on
/// it: once a size is taught, the switcher lists it and sets it the usual way. Linux teaches real outputs
/// (<c>xrandr --newmode</c>); Windows teaches the displays its virtual display driver added.
/// </summary>
public interface IArbitraryModeSink
{
    /// <summary>The sizes this can teach. A viewer is offered these, so it can ask for its window's size exactly.</summary>
    TeachableSizes Limits { get; }

    /// <summary>Whether sizes can be taught to <paramref name="display"/>.</summary>
    bool CanTeach(DisplayDescriptor display);

    /// <summary>Adds <paramref name="mode"/> to what the display can show.</summary>
    Task<DisplayActionResult> TeachAsync(DisplayDescriptor display, DisplayMode mode, CancellationToken ct);

    /// <summary>
    /// Takes back one size taught to <paramref name="display"/>, which it has just left for another. A viewer whose
    /// window is resized asks for a new size each time; without this every one of them would stay behind.
    /// </summary>
    Task ForgetAsync(DisplayDescriptor display, DisplayMode mode, CancellationToken ct);

    /// <summary>Takes back every size taught, once the displays are back in their original modes.</summary>
    Task ForgetTaughtModesAsync(CancellationToken ct);
}

/// <summary>Sizes from the minimum to the maximum, in both directions, in whole steps.</summary>
public readonly record struct TeachableSizes(int MinWidth, int MinHeight, int MaxWidth, int MaxHeight, int Step)
{
    public bool Contains(int width, int height) =>
        width >= MinWidth && width <= MaxWidth && height >= MinHeight && height <= MaxHeight
        && (Step <= 1 || (width % Step == 0 && height % Step == 0));
}
