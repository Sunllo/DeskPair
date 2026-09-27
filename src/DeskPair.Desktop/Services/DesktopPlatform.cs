using Microsoft.Extensions.Logging;
using DeskPair.Platform.Abstractions.Audio;
using DeskPair.Platform.Abstractions.Clipboard;
using DeskPair.Platform.Abstractions.Codec;

namespace DeskPair.Desktop.Services;

/// <summary>Screen sharing on a Wayland desktop, as the settings page shows it.</summary>
public enum WaylandSharingState
{
    NotYet,
    WatchOnly,
    Allowed,
}

/// <summary>How asking from the settings page ended; the same values as the Linux platform's own outcome.</summary>
public enum WaylandAskOutcome
{
    Allowed,
    WatchOnly,
    Declined,
    Unanswered,
    Failed,
}

/// <summary>Controller-side platform pieces: video decoding, audio playback and the local clipboard.</summary>
public static class DesktopPlatform
{
    public static IVideoDecoderFactory? CreateDecoders(ILoggerFactory logs)
    {
        var software = new Codec.OpenH264.OpenH264DecoderFactory(logs);
        var vpx = new Codec.Vpx.VpxVideoDecoderFactory(logs);
        string? forced = Environment.GetEnvironmentVariable("SUNLLO_DECODER"); // diagnostics: "openh264", "mf" or "vpx"
        if (string.Equals(forced, "openh264", StringComparison.OrdinalIgnoreCase))
        {
            return software;
        }

        if (string.Equals(forced, "vpx", StringComparison.OrdinalIgnoreCase))
        {
            return vpx;
        }
#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            var mf = new Platform.Windows.Codec.MfVideoDecoderFactory(logs);
            return string.Equals(forced, "mf", StringComparison.OrdinalIgnoreCase) ? mf : new FallbackVideoDecoderFactory(mf, software, vpx);
        }
#endif
#if !WINDOWS
        if (OperatingSystem.IsMacOS())
        {
            // VideoToolbox H.264/HEVC hardware decode first, then the software decoders (including VP9).
            var vt = new Platform.MacOS.Codec.MacVideoDecoderFactory(logs);
            return string.Equals(forced, "vt", StringComparison.OrdinalIgnoreCase) ? vt : new FallbackVideoDecoderFactory(vt, software, vpx);
        }
#endif
        return Codec.OpenH264.OpenH264EncoderFactory.IsAvailable || vpx.Probe() != SupportedCodecs.None
            ? new FallbackVideoDecoderFactory(software, vpx)
            : null;
    }

    /// <summary>
    /// The local platform's capability and consent surface, or null when this build has no implementation
    /// for it. The settings UI uses it to show the permissions a platform actually needs — on macOS those
    /// are Screen Recording and Accessibility, which cannot be granted in code and must be turned on by
    /// the user in System Settings.
    /// </summary>
    public static Platform.Abstractions.Hosting.IPlatformInfo? CreatePlatformInfo()
    {
#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            return new Platform.Windows.Hosting.WindowsPlatformInfo();
        }
#endif
#if !WINDOWS
        if (OperatingSystem.IsMacOS())
        {
            return new Platform.MacOS.Hosting.MacPlatformInfo();
        }

        if (OperatingSystem.IsLinux())
        {
            return new Platform.Linux.Hosting.LinuxPlatformInfo(EngineSecrets());
        }
#endif
        return null;
    }

    /// <summary>
    /// Whether this app is in a Wayland desktop session: the screen is shared through the desktop's portal, which asks
    /// the person at this computer, and the settings page offers to be asked ahead of time.
    /// </summary>
    public static bool IsWaylandSession =>
#if !WINDOWS
        OperatingSystem.IsLinux() && new Platform.Linux.Hosting.LinuxPlatformInfo().IsWayland;
#else
        false;
#endif

    /// <summary>Whether screen sharing is remembered as allowed for this account, and for how much.</summary>
    public static async Task<WaylandSharingState> WaylandSharingStateAsync()
    {
#if !WINDOWS
        if (OperatingSystem.IsLinux())
        {
            return await Platform.Linux.Wayland.PortalPermission.StateAsync(EngineSecrets(), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance).ConfigureAwait(false) switch
            {
                Platform.Linux.Wayland.PortalPermissionState.Allowed => WaylandSharingState.Allowed,
                Platform.Linux.Wayland.PortalPermissionState.WatchOnly => WaylandSharingState.WatchOnly,
                _ => WaylandSharingState.NotYet,
            };
        }
#endif
        await Task.CompletedTask.ConfigureAwait(false);
        return WaylandSharingState.NotYet;
    }

    /// <summary>
    /// Asks the person at this computer now, through the portal's dialog on this screen, and remembers the answer where
    /// the engine will find it.
    /// </summary>
    public static async Task<(WaylandAskOutcome Outcome, string? Detail)> AskWaylandSharingAsync(ILoggerFactory logs, CancellationToken ct)
    {
#if !WINDOWS
        if (OperatingSystem.IsLinux())
        {
            (Platform.Linux.Wayland.PortalAskOutcome outcome, string? detail) =
                await Platform.Linux.Wayland.PortalPermission.AskAsync(EngineSecrets(), logs, ct).ConfigureAwait(false);
            return ((WaylandAskOutcome)(int)outcome, detail);
        }
#endif
        await Task.CompletedTask.ConfigureAwait(false);
        return (WaylandAskOutcome.Failed, "not a Wayland desktop");
    }

    /// <summary>The engine's secrets, where the portal's permission is remembered for the account this runs as.</summary>
    private static Platform.Abstractions.Security.ISecretStore EngineSecrets() =>
        Engine.PlatformServices.SecretStoreFor(Engine.ServerRole.DefaultDataDir());

    /// <summary>Records a session to a file; null when this platform cannot write one.</summary>
    public static Platform.Abstractions.Recording.ISessionRecorderFactory? CreateRecorders(ILoggerFactory logs)
    {
#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            return new Platform.Windows.Recording.MfSessionRecorderFactory(logs);
        }
#endif
        return null;
    }

    /// <summary>Sound devices this computer can play through; empty on platforms without a device list.</summary>
    public static IReadOnlyList<Platform.Abstractions.Audio.AudioDeviceInfo> PlaybackDevices()
    {
#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            return Platform.Windows.Audio.AudioDevices.Playback();
        }
#endif
        return [];
    }

    /// <summary>Asks the OS for 1 ms timer resolution while a remote session is presented; dispose to release.</summary>
    public static IDisposable RequestHighResolutionTimer()
    {
#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            Platform.Windows.Native.WinMm.BeginPeriod();
            return new TimerLease();
        }
#endif
        return new TimerLease(false);
    }

    private sealed class TimerLease(bool active = true) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed || !active)
            {
                return;
            }

            _disposed = true;
#if WINDOWS
            if (OperatingSystem.IsWindows())
            {
                Platform.Windows.Native.WinMm.EndPeriod();
            }
#endif
        }
    }

    public static IAudioPlayback? CreateAudioPlayback(ILoggerFactory logs, string? deviceId = null, bool exclusive = false)
    {
#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            try
            {
                return new Platform.Windows.Audio.WasapiAudioPlayback(logs.CreateLogger("audio")) { DeviceId = deviceId ?? string.Empty, Exclusive = exclusive };
            }
            catch (Exception)
            {
                return null;
            }
        }
#endif
#if !WINDOWS
        if (OperatingSystem.IsLinux())
        {
            try
            {
                // PulseAudio has no exclusive mode; the flag is a Windows WASAPI concept and is ignored here.
                return new Platform.Linux.Audio.PulseAudioPlayback(logs.CreateLogger("audio")) { DeviceId = deviceId ?? string.Empty };
            }
            catch (Exception)
            {
                return null;
            }
        }

        if (OperatingSystem.IsMacOS())
        {
            try
            {
                return new Platform.MacOS.Audio.MacAudioPlayback(logs.CreateLogger("audio")) { DeviceId = deviceId ?? string.Empty };
            }
            catch (Exception)
            {
                return null;
            }
        }
#endif
        return null;
    }

    public static IClipboard? CreateClipboard(ILoggerFactory logs)
    {
#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            try
            {
                return new Platform.Windows.Clipboard.WindowsClipboard(logs.CreateLogger("clipboard"));
            }
            catch (Exception)
            {
                return null;
            }
        }
#endif
#if !WINDOWS
        if (OperatingSystem.IsLinux())
        {
            try
            {
                return new Platform.Linux.Clipboard.X11Clipboard(logs.CreateLogger("clipboard"));
            }
            catch (Exception)
            {
                return null;
            }
        }

        if (OperatingSystem.IsMacOS())
        {
            try
            {
                return new Platform.MacOS.Clipboard.MacClipboard(logs.CreateLogger("clipboard"));
            }
            catch (Exception)
            {
                return null;
            }
        }
#endif
        return null;
    }
}
