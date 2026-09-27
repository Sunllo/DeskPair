using Microsoft.Extensions.Logging;
using DeskPair.Platform.Abstractions.Audio;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Abstractions.Clipboard;
using DeskPair.Platform.Abstractions.Cursor;
using DeskPair.Platform.Abstractions.Input;
using DeskPair.Platform.Linux.Audio;
using DeskPair.Platform.Linux.Capture;
using DeskPair.Platform.Linux.Clipboard;
using DeskPair.Platform.Abstractions.Security;
using DeskPair.Platform.Linux.Input;
using DeskPair.Platform.Linux.Wayland;

namespace DeskPair.Platform.Linux;

/// <summary>
/// Assembles the Linux host-side capture, input, cursor, audio and clipboard into the shape the composition
/// root wants. Kept here rather than in the Service project so the platform assembly owns its own wiring, and
/// so the encoder factory (which is shared with Windows) is passed in rather than referenced.
/// </summary>
public static class LinuxHostPlatform
{
    /// <summary>
    /// The five required host contracts plus the two optional ones (audio and clipboard), built from X11 and
    /// PulseAudio, for an X11 session. A Wayland session is <see cref="CreateWayland"/>: under Xwayland XShm sees
    /// only the X11 clients, a nearly empty picture.
    ///
    /// Audio is a factory, not an instance: the host builds a capture per device on demand, and the monitor
    /// source is resolved lazily so a machine with no PulseAudio still starts (the factory throws only when a
    /// capture is actually requested, which the audio publisher tolerates). The clipboard opens its display
    /// eagerly, so a headless machine without an X display would throw here — the caller catches that and
    /// falls through to the synthetic desktop.
    /// </summary>
    public static (
        IDisplayEnumerator Displays,
        IScreenCapturerFactory Capturers,
        IInputInjector Input,
        ICursorProvider Cursor,
        Func<string?, IAudioCapture> Audio,
        IClipboard Clipboard,
        IDisplayModeSwitcher DisplayModes) CreateX11(ILoggerFactory logs)
    {
        // The clipboard is created first so the input injector can borrow it to paste Unicode text (IBus makes
        // synthetic keysym injection of non-ASCII unreliable, so those characters go via a clipboard round-trip).
        var clipboard = new X11Clipboard(logs.CreateLogger<X11Clipboard>());
        return (
            new X11DisplayEnumerator(logs.CreateLogger<X11DisplayEnumerator>()),
            new X11ScreenCapturerFactory(logs),
            new X11InputInjector(logs.CreateLogger<X11InputInjector>(), clipboard),
            new X11CursorProvider(logs.CreateLogger<X11CursorProvider>()),
            device => new PulseAudioCapture(logs.CreateLogger<PulseAudioCapture>(), device),
            clipboard,
            new X11DisplayModes(logs.CreateLogger<X11DisplayModes>()));
    }

    /// <summary>
    /// The Wayland host: the portal for displays, capture, input and the pointer (<see cref="PortalHost"/>, open only
    /// while somebody watches), PulseAudio for sound as under X11 (pipewire-pulse speaks its API), and the X11
    /// clipboard through Xwayland, which GNOME and KDE bridge to the Wayland one both ways. The clipboard is optional:
    /// a session with no X display has none, and then text the keyboard layout cannot type is not typed.
    /// </summary>
    public static (PortalHost Portal, Func<string?, IAudioCapture> Audio, IClipboard? Clipboard) CreateWayland(ILoggerFactory logs, ISecretStore store)
    {
        IClipboard? clipboard = null;
        try
        {
            clipboard = new X11Clipboard(logs.CreateLogger<X11Clipboard>());
        }
        catch (Exception e) when (e is InvalidOperationException or DllNotFoundException)
        {
            logs.CreateLogger("wayland").LogInformation("No clipboard in this Wayland session: {Reason}", e.Message);
        }

        return (
            new PortalHost(store, logs, clipboard),
            device => new PulseAudioCapture(logs.CreateLogger<PulseAudioCapture>(), device),
            clipboard);
    }
}
