namespace DeskPair.Platform.Abstractions.Capture;

/// <summary>
/// A screen of the session's own, the way a Windows remote desktop session has one: while viewers work on the host,
/// its desktop is a display of the size of their window, and every other display is off, so the person at the
/// computer sees none of it. Put back when the viewers stop following their windows or leave, and by the platform
/// itself when the host dies meanwhile.
///
/// Only Windows has one (DeskPair's virtual display driver). The host opens it only when its owner allows it.
/// </summary>
public interface ISessionScreen
{
    /// <summary>Null when one can be opened here; otherwise why not, for the host's log.</summary>
    string? UnavailableReason { get; }

    /// <summary>Whether it is up.</summary>
    bool IsOpen { get; }

    /// <summary>Whether <paramref name="display"/> is it.</summary>
    bool IsSessionScreen(DisplayDescriptor display);

    /// <summary>
    /// Plugs it in at <paramref name="size"/>, able to take <paramref name="sizes"/> as well, and makes it the only display
    /// on the desktop.
    /// </summary>
    Task<DisplayActionResult> OpenAsync(DisplayMode size, IReadOnlyList<DisplayMode> sizes, CancellationToken ct);

    /// <summary>
    /// Whether it is still the only display on: false once somebody at the computer has brought the others back
    /// (Windows' Win+P), which is theirs to do, and ends it.
    /// </summary>
    bool IsAlone();

    /// <summary>Unplugs it; the displays that were on before come back.</summary>
    Task CloseAsync(CancellationToken ct);
}
