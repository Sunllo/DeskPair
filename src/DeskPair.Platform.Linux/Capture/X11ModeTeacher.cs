using Microsoft.Extensions.Logging;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Linux.Hosting;

namespace DeskPair.Platform.Linux.Capture;

/// <summary>
/// Teaches an XRandR output a size it did not advertise: <c>xrandr --newmode</c> with CVT timings (<see cref="Cvt"/>),
/// then <c>--addmode</c>. After that <see cref="X11DisplayModes"/> lists the size and sets it the usual way, so the
/// original is recorded, the change confirmed and put back like any other.
///
/// Every mode made here is called <c>deskpair-WxH</c>: plain to anybody who finds one in <c>xrandr</c>'s list, and
/// never the name of a mode somebody made themselves -- taking them away again, which happens once the displays are
/// back in their original modes, can only ever take away ours. Reduced-blanking timings: every digital output takes
/// them, and their clock is far lower (4K at 60 Hz is 533 MHz rather than 713, which HDMI 2.0 would refuse).
///
/// Only X11. Under Wayland there is nothing to teach -- RandR there is Xwayland's fiction -- and the scanout daemon
/// has no X at all.
/// </summary>
public sealed class X11ModeTeacher : IArbitraryModeSink
{
    internal const string Prefix = "deskpair-";

    private readonly ILogger _log;
    private readonly bool _wayland;
    private readonly Func<IReadOnlyList<string>, string?> _xrandr;
    private readonly object _lock = new();
    private readonly List<(string Output, string Mode)> _added = [];
    private readonly List<string> _made = [];

    public X11ModeTeacher(ILogger log)
        : this(log, new LinuxPlatformInfo().IsWayland, null)
    {
    }

    /// <param name="log">Where what was taught, and what could not be, is said.</param>
    /// <param name="wayland">Whether the desktop is Wayland's, where nothing can be taught.</param>
    /// <param name="xrandr">Runs xrandr and says what went wrong, or null; the real tool by default. A seam for tests.</param>
    internal X11ModeTeacher(ILogger log, bool wayland, Func<IReadOnlyList<string>, string?>? xrandr)
    {
        _log = log;
        _wayland = wayland;
        _xrandr = xrandr ?? (arguments => XrandrTool.Run(arguments, log));
    }

    public bool CanTeach(DisplayDescriptor display) => !_wayland && XrandrTool.IsSafeName(display.Name);

    public Task<DisplayActionResult> TeachAsync(DisplayDescriptor display, DisplayMode mode, CancellationToken ct)
    {
        if (!CanTeach(display))
        {
            return Task.FromResult(DisplayActionResult.Refused("A size can be added only to a display of an X11 desktop."));
        }

        if (mode.Width is < 320 or > 8192 || mode.Height is < 200 or > 8192)
        {
            return Task.FromResult(DisplayActionResult.Refused($"{mode.Width}x{mode.Height} is not a size a display can be given."));
        }

        string name = $"{Prefix}{mode.Width}x{mode.Height}";
        lock (_lock)
        {
            if (_added.Contains((display.Name, name)))
            {
                return Task.FromResult(DisplayActionResult.Done);
            }

            // A mode of this name may be left from an engine that died before it could take it away. xrandr then
            // refuses to make it again, and the one that is there is ours to use: the name says so.
            string? made = _made.Contains(name) ? null : _xrandr(["--newmode", name, .. Cvt.Compute(mode.Width, mode.Height, reducedBlanking: true).Arguments()]);
            string? added = _xrandr(["--addmode", display.Name, name]);
            if (added is not null)
            {
                // Made just now and then of no use: not left in the server for nobody to take away.
                if (made is null && !_made.Contains(name))
                {
                    _xrandr(["--rmmode", name]);
                }

                _log.LogWarning("Could not add {Mode} to {Output}: {Made} {Added}", name, display.Name, made, added);
                return Task.FromResult(DisplayActionResult.Refused($"{mode.Width}x{mode.Height} could not be added to this display: {made ?? added}"));
            }

            if (!_made.Contains(name))
            {
                _made.Add(name);
            }

            _added.Add((display.Name, name));
            _log.LogInformation("Taught {Output} {Mode}", display.Name, name);
            return Task.FromResult(DisplayActionResult.Done);
        }
    }

    /// <summary>
    /// Takes every size taught back: off the outputs first, then out of the server. Called once the displays are back
    /// in their original modes -- a mode still in use cannot be taken off, and would stay.
    /// </summary>
    public Task ForgetTaughtModesAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            foreach ((string output, string name) in _added)
            {
                if (_xrandr(["--delmode", output, name]) is { } failure)
                {
                    _log.LogWarning("Could not take {Mode} off {Output}: {Why}", name, output, failure);
                }
            }

            foreach (string name in _made)
            {
                if (_xrandr(["--rmmode", name]) is { } failure)
                {
                    _log.LogWarning("Could not remove {Mode}: {Why}", name, failure);
                }
            }

            _added.Clear();
            _made.Clear();
        }

        return Task.CompletedTask;
    }
}
