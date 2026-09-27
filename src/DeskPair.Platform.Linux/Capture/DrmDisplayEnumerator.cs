using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Linux.Capture.Drm;

namespace DeskPair.Platform.Linux.Capture;

/// <summary>
/// The displays the daemon can see: one per CRTC it is scanning out, which for this milestone is the
/// one the primary plane is on.
///
/// A display's name and size come from the daemon's first answer rather than from XRandR, because at
/// the login screen there is no X to ask. <c>AdapterLuid</c> carries the CRTC and connector ids the way
/// the macOS enumerator carries a <c>CGDirectDisplayID</c> -- the platform's own name for the thing,
/// smuggled through the one field the contract leaves for it. The driver name rides along in the same
/// field's high byte so the capturer can classify the format without a second channel.
/// </summary>
/// <param name="channel">The daemon's scanout channel.</param>
/// <param name="driver">The DRM driver name, for the format classifier.</param>
/// <param name="wake">
/// Wakes a display the compositor has switched off, usually by nudging the pointer. An idle login
/// screen has no scanout at all, and a viewer who connects then would be told there are no displays; a
/// person would have moved the mouse first, so this does.
/// </param>
public sealed class DrmDisplayEnumerator(DrmCaptureChannel channel, string driver, Action? wake = null) : IDisplayEnumerator
{
    /// <summary>How long a switched-off display is given to come back after being nudged.</summary>
    private static readonly TimeSpan WakeTimeout = TimeSpan.FromSeconds(4);

    private static readonly Dictionary<long, string> Drivers = new();

    /// <summary>The driver the enumerator was told, keyed by the descriptor it produced.</summary>
    public static string DriverFor(DisplayDescriptor display) =>
        Drivers.TryGetValue(display.AdapterLuid, out string? name) ? name : "unknown";

    public event EventHandler? DisplaysChanged
    {
        add { }
        remove { }
    }

    public IReadOnlyList<DisplayDescriptor> GetDisplays()
    {
        DrmPollResult result = channel.Poll();
        if (result.Kind == DrmPollKind.NoScanout && !result.NoHardware && wake is not null)
        {
            wake();
            long deadline = Environment.TickCount64 + (long)WakeTimeout.TotalMilliseconds;
            while (result.Kind == DrmPollKind.NoScanout && Environment.TickCount64 < deadline)
            {
                Thread.Sleep(100);
                result = channel.Poll();
            }
        }

        if (result.Kind != DrmPollKind.Frame)
        {
            // Off and staying off, or not answered: no display to offer, said as such rather than as black.
            return [];
        }

        DrmFrameInfo f = result.Frame;
        long key = ((long)f.CrtcId << 32) | f.ConnectorId;
        Drivers[key] = driver;
        return
        [
            new DisplayDescriptor(
                Index: 0,
                Name: $"scanout-{f.ConnectorId}",
                X: 0,
                Y: 0,
                Width: (int)f.SrcWidth,
                Height: (int)f.SrcHeight,
                Scale: 1.0,
                Rotation: FrameRotation.None,
                IsPrimary: true,
                AdapterLuid: key),
        ];
    }
}
