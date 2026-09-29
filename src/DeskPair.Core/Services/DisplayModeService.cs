using Microsoft.Extensions.Logging;
using DeskPair.Platform.Abstractions.Capture;

namespace DeskPair.Core.Services;

/// <summary>
/// Changes a display's mode on a viewer's behalf and remembers what to put back.
///
/// The original is recorded the first time a display is changed and only then: however many viewers change
/// it afterwards, and to whatever, "original" means the mode the screen was in before anybody connected.
/// It is restored when the last remote session closes and when the engine stops. A host that crashes
/// restores nothing -- the platform implementations are asked not to persist the change (Windows leaves
/// the registry alone, macOS uses a per-session configuration), so a sign-out or reboot heals it.
/// </summary>
public sealed class DisplayModeService
{
    private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ConfirmPoll = TimeSpan.FromMilliseconds(100);

    private readonly IDisplayEnumerator _displays;
    private readonly IDisplayModeSwitcher? _switcher;
    private readonly IArbitraryModeSink? _teacher;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, DisplayMode> _originals = new(StringComparer.Ordinal);

    /// <summary>The size taught to each display and in force there now: forgotten as soon as the display leaves it.</summary>
    private readonly Dictionary<string, DisplayMode> _taught = new(StringComparer.Ordinal);

    /// <summary>
    /// The largest width or height offered for a size made up on request. A hardware H.264 encoder goes no further,
    /// and a stream the encoder refuses is worth less than a picture a little smaller than the window.
    /// </summary>
    public const int MaxMadeUpSide = 4096;

    /// <param name="displays">What the displays are now, read again to confirm a change took.</param>
    /// <param name="switcher">What sets a mode; null on a platform that cannot.</param>
    /// <param name="time">The clock the confirmation waits on.</param>
    /// <param name="log">Where changes and failures are written.</param>
    /// <param name="teacher">
    /// Where a display can be taught a size it did not advertise -- a Linux output, a virtual display. Null
    /// keeps every display to its own list.
    /// </param>
    public DisplayModeService(IDisplayEnumerator displays, IDisplayModeSwitcher? switcher, TimeProvider time, ILogger log, IArbitraryModeSink? teacher = null)
    {
        _displays = displays;
        _switcher = switcher;
        _teacher = teacher;
        _time = time;
        _log = log;
    }

    /// <summary>Whether any display is currently in a viewer-chosen mode.</summary>
    public bool HasChanges
    {
        get
        {
            lock (_originals)
            {
                return _originals.Count > 0;
            }
        }
    }

    public IReadOnlyList<DisplayMode> ModesFor(DisplayDescriptor display)
    {
        if (_switcher is null)
        {
            return [];
        }

        try
        {
            return _switcher.GetModes(display);
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Could not list the modes of display {Name}", display.Name);
            return [];
        }
    }

    /// <summary>
    /// The sizes <paramref name="display"/> can be given beyond its own list, or null when it has only that list. A
    /// viewer offered these may ask for exactly the size of its window.
    /// </summary>
    public TeachableSizes? MadeUpSizesFor(DisplayDescriptor display)
    {
        if (_switcher is null || _teacher is not { } teacher || !teacher.CanTeach(display))
        {
            return null;
        }

        TeachableSizes limits = teacher.Limits;
        int step = Math.Max(2, limits.Step); // encoders want even sizes
        int Up(int v) => (v + step - 1) / step * step;
        int Down(int v) => v / step * step;
        var sizes = new TeachableSizes(
            Up(limits.MinWidth), Up(limits.MinHeight), Down(Math.Min(limits.MaxWidth, MaxMadeUpSide)), Down(Math.Min(limits.MaxHeight, MaxMadeUpSide)), step);
        return sizes.MinWidth <= sizes.MaxWidth && sizes.MinHeight <= sizes.MaxHeight ? sizes : null;
    }

    /// <summary>The mode the display was in before a viewer changed it, or null when nobody has.</summary>
    public DisplayMode? OriginalFor(string displayName)
    {
        lock (_originals)
        {
            return _originals.TryGetValue(displayName, out DisplayMode original) ? original : null;
        }
    }

    /// <summary>
    /// Switches display <paramref name="displayIndex"/> to <paramref name="wanted"/>, or back to its original
    /// when <paramref name="wanted"/> is null. Returns null on success, otherwise why it was refused.
    /// </summary>
    public async Task<string?> ChangeAsync(int displayIndex, DisplayMode? wanted, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            IReadOnlyList<DisplayDescriptor> displays = _displays.GetDisplays();
            if (displayIndex < 0 || displayIndex >= displays.Count)
            {
                return "That display is not there any more.";
            }

            DisplayDescriptor display = displays[displayIndex];
            DisplayMode current = new(display.Width, display.Height, display.Scale);
            DisplayMode? original = OriginalFor(display.Name);
            if (wanted is null && original is null)
            {
                return null; // nothing was changed, so there is nothing to put back -- whatever this computer can do
            }

            if (_switcher is null)
            {
                return "This computer cannot change its resolution from here.";
            }
            DisplayMode target;
            bool teaching = false;
            if (wanted is { } chosen)
            {
                IReadOnlyList<DisplayMode> modes = ModesFor(display);
                bool Listed(IReadOnlyList<DisplayMode> list) =>
                    list.Any(m => m.Width == chosen.Width && m.Height == chosen.Height && (chosen.Scale == 0 || Math.Abs(m.Scale - chosen.Scale) < 0.01));

                // A size the display does not have: one that can be taught it learns it, and everything after
                // this -- recording the original, confirming the change, restoring it -- is the same as for a
                // size it always had.
                if (!Listed(modes) && _teacher is { } teacher && MadeUpSizesFor(display) is { } sizes)
                {
                    if (!sizes.Contains(chosen.Width, chosen.Height))
                    {
                        return $"{chosen.Width}x{chosen.Height} is not a size this display can be given.";
                    }

                    DisplayActionResult taught = await teacher.TeachAsync(display, chosen, ct).ConfigureAwait(false);
                    if (!taught.Succeeded)
                    {
                        return taught.Failure ?? $"{chosen.Width}x{chosen.Height} could not be added to this display.";
                    }

                    teaching = true;
                    modes = ModesFor(display);
                }

                if (!Listed(modes))
                {
                    return $"{chosen.Width}x{chosen.Height} is not a size this display can show.";
                }

                target = modes.First(m => m.Width == chosen.Width && m.Height == chosen.Height && (chosen.Scale == 0 || Math.Abs(m.Scale - chosen.Scale) < 0.01));
            }
            else if (original is { } o)
            {
                target = o;
            }
            else
            {
                return null; // nothing was changed, so there is nothing to put back
            }

            if (target.Matches(display))
            {
                if (wanted is null || original is { } back && SameMode(target, back))
                {
                    Forget(display.Name);
                }

                if (teaching)
                {
                    // Taught a size the display already has under another name: the copy is of no use.
                    await _teacher!.ForgetAsync(display, target, CancellationToken.None).ConfigureAwait(false);
                }

                return null;
            }

            bool recorded = false;
            lock (_originals)
            {
                if (!_originals.ContainsKey(display.Name))
                {
                    _originals[display.Name] = current;
                    recorded = true;
                }
            }

            string? failure = Apply(display, target);
            if (failure is null)
            {
                failure = await ConfirmAsync(display.Index, target, ct).ConfigureAwait(false);
                if (failure is not null && original is { } back)
                {
                    // It said yes and then did not; put it back rather than leave the screen in a state
                    // neither side asked for.
                    Apply(display, back);
                }
            }

            if (failure is not null)
            {
                if (recorded)
                {
                    Forget(display.Name);
                }

                if (teaching)
                {
                    await _teacher!.ForgetAsync(display, target, CancellationToken.None).ConfigureAwait(false);
                }

                _log.LogWarning("Display {Name}: could not switch to {W}x{H}: {Why}", display.Name, target.Width, target.Height, failure);
                return failure;
            }

            if (original is { } restored && SameMode(target, restored))
            {
                Forget(display.Name);
            }

            await ForgetLeftSizeAsync(display, target, teaching).ConfigureAwait(false);

            _log.LogInformation("Display {Name}: {FromW}x{FromH} -> {ToW}x{ToH}", display.Name, current.Width, current.Height, target.Width, target.Height);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Puts every changed display back. Called when the last viewer leaves and when the engine stops.</summary>
    public async Task RestoreAllAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            List<KeyValuePair<string, DisplayMode>> pending;
            lock (_originals)
            {
                pending = _originals.ToList();
                _originals.Clear();
                _taught.Clear();
            }

            try
            {
                Restore(pending);
            }
            finally
            {
                // Taught sizes go once nothing is using them any more.
                if (_teacher is not null)
                {
                    await _teacher.ForgetTaughtModesAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Restore(List<KeyValuePair<string, DisplayMode>> pending)
    {
        if (pending.Count == 0 || _switcher is null)
        {
            return;
        }

        IReadOnlyList<DisplayDescriptor> displays = _displays.GetDisplays();
        foreach ((string name, DisplayMode original) in pending)
        {
            DisplayDescriptor? display = displays.FirstOrDefault(d => d.Name == name);
            if (display is null)
            {
                _log.LogWarning("Display {Name} is gone; its original mode {W}x{H} was not restored", name, original.Width, original.Height);
                continue;
            }

            string? failure = Apply(display.Value, original);
            if (failure is null)
            {
                _log.LogInformation("Display {Name}: restored to {W}x{H}", name, original.Width, original.Height);
            }
            else
            {
                _log.LogWarning("Display {Name}: could not restore {W}x{H}: {Why}", name, original.Width, original.Height, failure);
            }
        }
    }

    private string? Apply(DisplayDescriptor display, DisplayMode mode)
    {
        try
        {
            return _switcher!.TrySetMode(display, mode, out string? failure) ? null : (failure ?? "The display refused the mode.");
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Setting the mode of display {Name} threw", display.Name);
            return e.Message;
        }
    }

    /// <summary>The platform said yes; wait for the enumerator to agree, since the capture loop reads it.</summary>
    private async Task<string?> ConfirmAsync(int index, DisplayMode target, CancellationToken ct)
    {
        long started = _time.GetTimestamp();
        while (true)
        {
            DisplayDescriptor? now = _displays.GetDisplays().FirstOrDefault(d => d.Index == index);
            if (now is { } d && target.Matches(d))
            {
                return null;
            }

            if (_time.GetElapsedTime(started) >= ConfirmTimeout)
            {
                return "The display did not switch.";
            }

            await Task.Delay(ConfirmPoll, _time, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The display has just moved to <paramref name="now"/>: a size taught to it earlier, which it has now left, is
    /// taken back at once -- a window being resized leaves one behind at every pause. The size now in force is
    /// remembered when it was taught, to be taken back in its turn.
    /// </summary>
    private async Task ForgetLeftSizeAsync(DisplayDescriptor display, DisplayMode now, bool taughtNow)
    {
        DisplayMode? left = null;
        lock (_originals)
        {
            if (_taught.TryGetValue(display.Name, out DisplayMode before) && !SameMode(before, now))
            {
                left = before;
                _taught.Remove(display.Name);
            }

            if (taughtNow)
            {
                _taught[display.Name] = now;
            }
        }

        if (left is { } size && _teacher is { } teacher)
        {
            await teacher.ForgetAsync(display, size, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>Same size, and the same scale where both sides know one; a request without a scale matches any.</summary>
    private static bool SameMode(DisplayMode a, DisplayMode b) =>
        a.Width == b.Width && a.Height == b.Height && (a.Scale == 0 || b.Scale == 0 || Math.Abs(a.Scale - b.Scale) < 0.01);

    private void Forget(string name)
    {
        lock (_originals)
        {
            _originals.Remove(name);
        }
    }

    /// <summary>
    /// A display unplugged from here is gone for good: what was recorded about it goes too, before another display
    /// plugged in later comes up under the same name (Windows hands its GDI names out again) and inherits it.
    /// </summary>
    public void ForgetDisplay(string name)
    {
        lock (_originals)
        {
            _originals.Remove(name);
            _taught.Remove(name);
        }
    }
}
