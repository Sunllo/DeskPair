namespace DeskPair.Platform.Abstractions.Capture;

/// <summary>
/// A size a display can be set to, in the pixels that get captured. <paramref name="Scale"/> is the backing
/// factor where the platform has one (2 for a Retina "HiDPI" mode) and 0 elsewhere: on macOS two modes can
/// share a pixel size and differ in scale, so the triple is the identity, not the pair.
/// </summary>
public readonly record struct DisplayMode(int Width, int Height, double Scale = 0)
{
    /// <summary>Whether this is the mode a descriptor is currently in.</summary>
    public bool Matches(DisplayDescriptor display) =>
        display.Width == Width && display.Height == Height && (Scale == 0 || Math.Abs(display.Scale - Scale) < 0.01);
}

/// <summary>
/// Lists and sets a display's mode, for a viewer that wants the host's screen at a size that suits it.
///
/// Only modes the display itself advertises; a size it does not have is taught to it first, where that can
/// be done, by an <see cref="IArbitraryModeSink"/>. A platform that cannot do this (the Linux scanout daemon,
/// Wayland, a remote desktop session) returns no modes, and the viewer shows no picker.
/// </summary>
public interface IDisplayModeSwitcher
{
    /// <summary>The modes <paramref name="display"/> can be switched to, largest first; empty when it cannot be switched from here.</summary>
    IReadOnlyList<DisplayMode> GetModes(DisplayDescriptor display);

    /// <summary>Switches the display. <paramref name="failure"/> says why not, in words fit for a viewer's screen.</summary>
    bool TrySetMode(DisplayDescriptor display, DisplayMode mode, out string? failure);
}
