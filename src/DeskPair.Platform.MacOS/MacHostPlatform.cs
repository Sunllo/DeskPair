using Microsoft.Extensions.Logging;
using DeskPair.Platform.Abstractions.Audio;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Abstractions.Clipboard;
using DeskPair.Platform.Abstractions.Cursor;
using DeskPair.Platform.Abstractions.Input;
using DeskPair.Platform.MacOS.Audio;
using DeskPair.Platform.MacOS.Capture;
using DeskPair.Platform.MacOS.Clipboard;
using DeskPair.Platform.MacOS.Input;
using DeskPair.Platform.MacOS.Native;

namespace DeskPair.Platform.MacOS;

/// <summary>
/// Assembles the macOS host-side contracts into the shape the composition root wants. The shim carries all of
/// it; the encoder (shared with the other platforms) is passed in by the caller. Audio is a factory built per
/// request (and only succeeds once Screen Recording is granted); the clipboard opens eagerly.
/// </summary>
public static class MacHostPlatform
{
    /// <summary>
    /// The capture, input, cursor, audio and clipboard contracts built on ScreenCaptureKit and CoreGraphics.
    /// Throws when the shim is not present so the caller can fall through to the synthetic desktop.
    /// </summary>
    public static (
        IDisplayEnumerator Displays,
        IScreenCapturerFactory Capturers,
        IInputInjector Input,
        ICursorProvider Cursor,
        Func<string?, IAudioCapture> Audio,
        IClipboard Clipboard) CreateScreenCaptureKit(ILoggerFactory logs)
    {
        if (!MacShim.IsAvailable)
        {
            throw new InvalidOperationException("The macOS shim (libSunlloMacShim.dylib) is not present.");
        }

        return (
            new MacDisplayEnumerator(logs.CreateLogger<MacDisplayEnumerator>()),
            new MacScreenCapturerFactory(logs),
            new MacInputInjector(logs.CreateLogger<MacInputInjector>()),
            new MacCursorProvider(),
            _ => new MacAudioCapture(logs.CreateLogger<MacAudioCapture>()),
            new MacClipboard(logs.CreateLogger<MacClipboard>()));
    }
}
