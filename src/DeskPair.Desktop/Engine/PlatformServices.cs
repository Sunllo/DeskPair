using Microsoft.Extensions.Logging;
using DeskPair.Core.Config;
using DeskPair.Core.Services;
using DeskPair.Core.Testing;
using DeskPair.Platform.Abstractions.Codec;
using DeskPair.Platform.Abstractions.Hosting;
using DeskPair.Platform.Abstractions.Security;

namespace DeskPair.Desktop.Engine;

/// <summary>
/// Selects the platform implementation for the OS this process runs on. A machine with no desktop this
/// program can capture is a failure, not a fallback: see <see cref="SyntheticAllowed"/>.
/// </summary>
public sealed record PlatformServices(HostPlatform Host, ISecretStore SecretStore, IMachineIdProvider MachineId, DeskPair.Platform.Abstractions.Terminal.ITerminalHost? Terminal = null)
{
    public const string IpcTokenVariable = "SUNLLO_IPC_TOKEN";

    /// <summary>
    /// What the Linux daemon handed this engine: the scanout channel and the two input devices, as
    /// inherited descriptors. Set by the server role from argv before the platform is built; null in
    /// every other engine, which then captures through X11 as before.
    /// </summary>
    public static DaemonHandles? Daemon { get; set; }

    public sealed record DaemonHandles(int SupervisorFd, int KeyboardFd, int PointerFd, int RelativeFd);

    /// <summary>Set to anything but 0 to allow the synthetic desktop; see <see cref="SyntheticAllowed"/>.</summary>
    public const string SyntheticVariable = "SUNLLO_FAKE_DESKTOP";

    /// <summary>Set to anything but 0 to capture a Wayland session through X11 anyway; see <see cref="X11Requested"/>.</summary>
    public const string X11Variable = "SUNLLO_X11";

    /// <summary>
    /// Just the keystore, without building a host.
    ///
    /// The UI needs the device's identity key to sign a portal link, and <see cref="Create"/> is the wrong
    /// way to get it: that probes displays, encoders and audio devices, which is a lot of work and a lot of
    /// log noise to reach one file. It is the same store either way, so the key is the same key -- including
    /// when the engine turns out to be running in another copy of this program, which shares the data dir.
    /// </summary>
#if WINDOWS
    /// <summary>
    /// The DPAPI scope follows who the engine is running as.
    ///
    /// LocalSystem gets the machine scope, because the host service's engine and the app share one data
    /// directory on purpose -- one machine, one identity, one id at the rendezvous server. A user-scoped
    /// secret is opaque to LocalSystem, and a LocalSystem one is opaque to the user, so a shared store
    /// that is not machine-scoped is a store one of them will refuse to read.
    ///
    /// Anyone else gets the user scope, which is stronger: a machine-scoped secret can be decrypted by
    /// anything running on the machine that can open the file, and the app has no need of that.
    ///
    /// The move between them is not automatic and cannot be: LocalSystem cannot decrypt a signed-in
    /// user's blob to re-write it. Installing the service does that migration, from a process that is
    /// still that user. See <c>HostServiceInstaller</c>.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static ISecretStore WindowsStore(string dataDir)
    {
        bool system = false;
        try
        {
            using var me = System.Security.Principal.WindowsIdentity.GetCurrent();
            system = me.IsSystem;
        }
        catch (Exception)
        {
            // Unable to say who we are, so take the narrower scope. Being unreadable later is a fault
            // somebody will see; being readable by the whole machine is one nobody will.
        }

        return new Platform.Windows.Security.WindowsSecretStore(Path.Combine(dataDir, "secrets"), machineScope: system);
    }
#endif

    public static ISecretStore SecretStoreFor(string dataDir)
    {
#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            return WindowsStore(dataDir);
        }
#else
        // The Keychain, on macOS, because that is where the engine keeps them.
        //
        // This used to answer with a file store on every platform but Windows, while the engine built
        // itself a Keychain one -- so the two halves of the same program read this machine's identity from
        // two different places. The file store found nothing, and PeerIdentityStore's answer to finding
        // nothing is to mint a key and save it, so the settings page signed portal links and address-book
        // syncs as a host that did not exist, while the engine advertised the real one. Nothing failed
        // loudly; the fingerprints simply did not match.
        // Only when the shim is actually loadable. MacSecretStore is a P/Invoke into it, so on a Mac
        // without it this returned a store that threw DllNotFoundException on first read and took the
        // engine down -- where before it had quietly degraded. A machine with no shim has no Keychain
        // store to disagree with either, so the file store is both the safe answer and the honest one.
        if (OperatingSystem.IsMacOS() && Platform.MacOS.Security.MacSecretStore.IsAvailable)
        {
            return new Platform.MacOS.Security.MacSecretStore();
        }
#endif
        return new FileSecretStore(Path.Combine(dataDir, "secrets"));
    }

    /// <summary>
    /// Whether this run may stream the synthetic desktop in place of this machine's screen.
    ///
    /// It has to be asked for, and it did not used to be. Whenever the native host could not be built --
    /// no DISPLAY, no Screen Recording consent, no libX11 -- the engine quietly served a made-up desktop
    /// instead: the connection succeeded, the viewer saw a picture, and one warning in a log file was the
    /// only thing that said the picture was of nothing. A daemon started outside a session lands in that
    /// case every time, so the arrangement the unattended work is built on would have failed by appearing
    /// to work. The synthetic desktop is for the harness and for tests, which are the two callers that
    /// want a screen without a computer behind it, and they can say so.
    /// </summary>
    public static bool SyntheticAllowed(string[] args) =>
        Array.IndexOf(args, "--synthetic") >= 0 ||
        Environment.GetEnvironmentVariable(SyntheticVariable) is { Length: > 0 } and not "0";

    /// <summary>
    /// Whether a Wayland session is to be captured through X11 regardless. It sees only Xwayland's clients -- a
    /// nearly empty picture on GNOME -- so it is never the fallback for the portal, only something to ask for
    /// (a compositor whose portal cannot share, a test).
    /// </summary>
    public static bool X11Requested(string[] args) =>
        Array.IndexOf(args, "--x11") >= 0 ||
        Environment.GetEnvironmentVariable(X11Variable) is { Length: > 0 } and not "0";

    public static PlatformServices Create(string dataDir, ILoggerFactory logs, bool allowSynthetic = false, bool preferX11 = false)
    {
#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            return Windows(dataDir, logs);
        }
#endif
        ILogger log = logs.CreateLogger("platform");

        // Asked for, not constructed. Building a store here as well is how the last version of this bug
        // survived being fixed: the macOS path was corrected to ask, and the fall-through below -- which is
        // where a Mac lands whenever the shim is missing or screen recording has not been granted, so most
        // of the time -- went on making its own file store and its own identity beside the Keychain one.
        ISecretStore store = SecretStoreFor(dataDir);
#if WINDOWS
        if (store is Platform.Windows.Security.WindowsSecretStore { AccessControlError: { } acl })
        {
            // Said out loud rather than swallowed: DPAPI's machine scope has no wrong key, so the file
            // permissions are the whole of what keeps another account on this computer out.
            logs.CreateLogger("secrets").LogWarning("Could not restrict the secrets directory to SYSTEM, administrators and this account: {Reason}", acl);
        }
#endif

#if !WINDOWS
        if (OperatingSystem.IsLinux())
        {
            return Linux(logs, log, store, allowSynthetic, preferX11);
        }

        if (OperatingSystem.IsMacOS())
        {
            return Mac(logs, log, store, allowSynthetic);
        }
#endif

        return Synthetic(logs, log, store, allowSynthetic, "there is no native host for this operating system", "");
    }

    /// <summary>
    /// A desktop that is not this computer's: a moving test pattern, real encoders, and input that goes
    /// nowhere. Reached only when <see cref="SyntheticAllowed"/> says the caller asked for it; otherwise
    /// the reason and the machine state are thrown, because a host that cannot show this screen has
    /// nothing to offer and saying so is the only useful thing left to do.
    /// </summary>
    private static PlatformServices Synthetic(
        ILoggerFactory logs,
        ILogger log,
        ISecretStore store,
        bool allowed,
        string reason,
        string diagnostics,
        Exception? cause = null)
    {
        if (!allowed)
        {
            throw new HostPlatformUnavailableException(reason, diagnostics, cause);
        }

        log.LogWarning(
            cause,
            "Streaming the synthetic desktop because {Reason}. It is not this computer's screen; it was asked for with --synthetic or {Variable}. {Diagnostics}",
            reason,
            SyntheticVariable,
            diagnostics);

        // The capture and input side is a fake here, but the codec side is real: VP9 through libvpx is the
        // royalty-free encoder that runs anywhere, so the harness exercises a real stream rather than the
        // test encoder. OpenH264 comes first for whoever supplied a binary; the fake is only reached when
        // neither is present.
        IVideoEncoderFactory vpx = new Codec.Vpx.VpxVideoEncoderFactory(logs);
        IVideoEncoderFactory encoders =
            Codec.OpenH264.OpenH264EncoderFactory.IsAvailable || vpx.Probe() != SupportedCodecs.None
                ? new FallbackVideoEncoderFactory(new Codec.OpenH264.OpenH264EncoderFactory(logs), vpx)
                : new FakeVideoEncoderFactory();
        var fake = new HostPlatform(new FakeDisplayEnumerator(1280, 720), new FakeScreenCapturerFactory { UnchangedEvery = 5 }, encoders, new FakeInputInjector(), new FakeCursorProvider(), _ => new FakeAudioCapture(), new FakeClipboard());
        return new PlatformServices(fake, store, StoredMachineIdProvider.LoadOrCreateAsync(store).GetAwaiter().GetResult());
    }

#if !WINDOWS
    /// <summary>
    /// The Linux host, built from X11 (capture, input, cursor) plus the shared codec chain.
    ///
    /// The display is probed once, here, before anything is constructed. Capture, input, cursor and the
    /// clipboard each open their own connection, so one missing display used to surface as whichever of
    /// their four messages happened to be built first -- and none of them could say what was actually set
    /// on the machine, because none of them knew.
    /// </summary>
    private static PlatformServices Linux(ILoggerFactory logs, ILogger log, ISecretStore store, bool allowSynthetic, bool preferX11)
    {
        // Who is at the screen, for the IPC owner check. Root and the engine's own account are owners
        // already; this is how the person signed in reaches their own engine's settings. Nobody at the
        // greeter: gdm's session is not a person, and a token in its name would only be something for
        // anything able to run as gdm to find.
        Core.Ipc.IpcCaller.ConsoleUser = static () =>
            Platform.Linux.Hosting.LinuxSessions.Active() is { Role: Platform.Linux.Hosting.SessionRole.User or Platform.Linux.Hosting.SessionRole.LockScreen } session
                ? session.Uid
                : null;

        if (Daemon is { } handles)
        {
            return LinuxScanout(handles, logs, log, store);
        }

        string where = Platform.Linux.Hosting.LinuxSessionDiagnostics.Describe();
        bool wayland = new Platform.Linux.Hosting.LinuxPlatformInfo().IsWayland;
        if (wayland && !preferX11)
        {
            return LinuxWayland(logs, log, store, where);
        }

        if (!Platform.Linux.Hosting.LinuxSessionDiagnostics.CanOpenDisplay(out string failure))
        {
            return Synthetic(logs, log, store, allowSynthetic, "there is no desktop this process can capture: " + failure, where);
        }

        try
        {
            (var displays, var capturers, var input, var cursor, var audio, var clipboard, var displayModes) = Platform.Linux.LinuxHostPlatform.CreateX11(logs);

            var vpx = new Codec.Vpx.VpxVideoEncoderFactory(logs);
            IVideoEncoderFactory encoders = new FallbackVideoEncoderFactory(new Codec.OpenH264.OpenH264EncoderFactory(logs), vpx);

            // A size a display does not advertise is taught to it (xrandr --newmode); the displays themselves stay the ones there are.
            var host = new HostPlatform(displays, capturers, encoders, input, cursor, audio, clipboard, displayModes,
                ModeTeacher: new Platform.Linux.Capture.X11ModeTeacher(logs.CreateLogger("modes")));

            // Report what was actually probed. Claiming "VP9 encoding" unconditionally was a lie on a machine
            // with no libvpx, where the session then closed with "this computer has no encoder" and this line
            // was the last thing the log had said.
            SupportedCodecs codecs = encoders.Probe();
            log.LogInformation(
                "Linux host: X11 capture + XTest input + XFixes cursor, PulseAudio monitor, X11 clipboard, encoders: {Codecs}. {Diagnostics}",
                codecs == SupportedCodecs.None ? "none -- no video will be sent" : codecs.ToString(),
                where);

            // Under Xwayland the X root holds only X11 clients, so capture succeeds and shows a nearly empty
            // screen. Only reached when asked for; saying so is what keeps it from being a mystery.
            if (wayland)
            {
                log.LogWarning("This is a Wayland session and X11 capture was asked for ({Variable} or --x11): it sees only Xwayland clients, so the picture will be mostly empty", X11Variable);
            }

            return new PlatformServices(host, store, StoredMachineIdProvider.LoadOrCreateAsync(store).GetAwaiter().GetResult(), Platform.Linux.Terminal.LinuxTerminal.Create(logs.CreateLogger("terminal")));
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or DllNotFoundException)
        {
            return Synthetic(logs, log, store, allowSynthetic, "the X11 host could not be built: " + e.Message, where, e);
        }
    }

    /// <summary>
    /// The Linux host in a Wayland session: the screen, the input and the pointer through xdg-desktop-portal, opened
    /// when a viewer arrives and closed when the last one leaves (the person at the machine allows it, once if they
    /// let it remember), with PulseAudio and the Xwayland clipboard as under X11. Built even when the Wayland shim is
    /// missing: the portal host then tells every viewer why, which says more than an engine that will not start.
    /// </summary>
    private static PlatformServices LinuxWayland(ILoggerFactory logs, ILogger log, ISecretStore store, string where)
    {
        (var portal, var audio, var clipboard) = Platform.Linux.LinuxHostPlatform.CreateWayland(logs, store);
        var vpx = new Codec.Vpx.VpxVideoEncoderFactory(logs);
        IVideoEncoderFactory encoders = new FallbackVideoEncoderFactory(new Codec.OpenH264.OpenH264EncoderFactory(logs), vpx);
        var host = new HostPlatform(portal, portal, encoders, portal, portal, audio, clipboard, DisplaySession: portal);

        SupportedCodecs codecs = encoders.Probe();
        log.LogInformation(
            "Linux host (Wayland): screen, input and pointer through the portal while somebody watches ({Shim}), PulseAudio monitor, {Clipboard}, encoders: {Codecs}. {Diagnostics}",
            Platform.Linux.Wayland.PortalHost.Unavailable is { } why ? "cannot capture: " + why : "PipeWire shim loaded",
            clipboard is null ? "no clipboard" : "clipboard through Xwayland",
            codecs == SupportedCodecs.None ? "none -- no video will be sent" : codecs.ToString(),
            where);

        return new PlatformServices(host, store, StoredMachineIdProvider.LoadOrCreateAsync(store).GetAwaiter().GetResult(), Platform.Linux.Terminal.LinuxTerminal.Create(logs.CreateLogger("terminal")));
    }

    /// <summary>
    /// The Linux host under the daemon: the screen read from the display hardware through the daemon's
    /// socketpair, input typed through the daemon's virtual keyboard and pointer. This is the one path
    /// that sees the lock screen and the login screen, and it needs no X server and no session of its own.
    ///
    /// Audio and clipboard are the fakes for now: neither has a source without a session, and saying so
    /// with a fake is better than a provider that throws at the greeter. The cursor is an arrow shape and
    /// no position (<see cref="Platform.Linux.Capture.ScanoutCursorProvider"/>): the compositor draws the
    /// pointer into the picture on a card without a cursor plane, and reading the plane where there is one
    /// is the next thing the daemon can serve.
    /// </summary>
    private static PlatformServices LinuxScanout(DaemonHandles handles, ILoggerFactory logs, ILogger log, ISecretStore store)
    {
        // The daemon handed these over by leaving them open across exec, so they are not close-on-exec
        // here either -- and anything this engine starts would inherit them: a shell as this account would
        // hold the virtual keyboard and a socket to the root daemon. Mark them now, before anything starts.
        foreach (int fd in new[] { handles.SupervisorFd, handles.KeyboardFd, handles.PointerFd, handles.RelativeFd })
        {
            if (fd >= 0)
            {
                _ = Platform.Linux.Native.UnixSocketMsg.fcntl(fd, Platform.Linux.Native.UnixSocketMsg.FSetFd, 1);
            }
        }

        var channel = new Platform.Linux.Capture.Drm.DrmCaptureChannel(handles.SupervisorFd);
        var input = new Platform.Linux.Input.UinputInputInjector(handles.KeyboardFd, handles.PointerFd, handles.RelativeFd, logs.CreateLogger("uinput"), lockSeat: channel.Lock);
        var displays = new Platform.Linux.Capture.DrmDisplayEnumerator(channel, "unknown", wake: input.HasDevices ? input.Nudge : null);
        var capturers = new Platform.Linux.Capture.DrmScreenCapturerFactory(channel, logs);

        var vpx = new Codec.Vpx.VpxVideoEncoderFactory(logs);
        IVideoEncoderFactory encoders = new FallbackVideoEncoderFactory(new Codec.OpenH264.OpenH264EncoderFactory(logs), vpx);

        // A signed-in Wayland desktop is shared through the portal -- every monitor, the pointer, input as the
        // compositor means it -- by way of the session agent the daemon starts as its user, whenever that will not
        // ask anybody; the hardware serves the rest of the time, the lock screen and the login screen included.
        var screen = new Platform.Linux.Wayland.ScanoutPortalHost(channel, displays, capturers, input, new Platform.Linux.Capture.ScanoutCursorProvider(), store, logs);
        var host = new HostPlatform(screen, screen, encoders, screen, screen, _ => new FakeAudioCapture(), new FakeClipboard(), DisplaySession: screen);

        SupportedCodecs codecs = encoders.Probe();
        log.LogInformation(
            "Linux host under the daemon: scanout capture on fd {Fd}, a signed-in Wayland desktop through its session agent ({Shim}), {Input}, encoders: {Codecs}. {Diagnostics}",
            handles.SupervisorFd,
            Platform.Linux.Wayland.PortalHost.Unavailable is { } why ? "cannot read it: " + why : "PipeWire shim loaded",
            input.HasDevices ? "uinput keyboard/pointer" : "no input devices (viewers' input is ignored)",
            codecs == SupportedCodecs.None ? "none -- no video will be sent" : codecs.ToString(),
            Platform.Linux.Hosting.LinuxSessionDiagnostics.Describe());
        if (displays.GetDisplays().Count == 0)
        {
            log.LogWarning("This machine has no display to capture; viewers will be told so. File transfer and chat still work.");
        }

        // Shells through the daemon: root, or the signed-in user, when its gate allows; this engine's own account when it does not.
        return new PlatformServices(host, store, StoredMachineIdProvider.LoadOrCreateAsync(store).GetAwaiter().GetResult(), new Platform.Linux.Terminal.DaemonTerminalHost(channel, logs.CreateLogger("terminal")));
    }

    /// <summary>
    /// The macOS host, built from ScreenCaptureKit (capture), CoreGraphics (input, cursor) and the shared VP9
    /// encoder. VideoToolbox hardware encoding comes first on Apple silicon; the software encoders are the net.
    /// </summary>
    private static PlatformServices Mac(ILoggerFactory logs, ILogger log, ISecretStore store, bool allowSynthetic)
    {
        string where = Platform.MacOS.Hosting.MacSessionDiagnostics.Describe();
        try
        {
            (var displays, var capturers, var input, var cursor, var audio, var clipboard) = Platform.MacOS.MacHostPlatform.CreateScreenCaptureKit(logs);

            var videoToolbox = new Platform.MacOS.Codec.MacVideoEncoderFactory(logs);
            var vpx = new Codec.Vpx.VpxVideoEncoderFactory(logs);
            IVideoEncoderFactory encoders = new FallbackVideoEncoderFactory(videoToolbox, new Codec.OpenH264.OpenH264EncoderFactory(logs), vpx);

            var host = new HostPlatform(displays, capturers, encoders, input, cursor, audio, clipboard, new Platform.MacOS.Capture.MacDisplayModes());

            // The store the caller already resolved, not a second one built here. Two constructions are
            // two opinions about where this machine's identity lives, and they were different.
            var macMachineId = new Platform.MacOS.Security.MacMachineIdProvider();
            log.LogInformation(
                "macOS host: ScreenCaptureKit capture + audio, CGEvent input + cursor, NSPasteboard clipboard, Keychain secrets, VideoToolbox H.264/HEVC encoding. {Diagnostics}",
                where);
            return new PlatformServices(host, store, macMachineId, Platform.MacOS.Terminal.MacTerminal.Create(logs.CreateLogger("terminal")));
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or DllNotFoundException)
        {
            return Synthetic(logs, log, store, allowSynthetic, "the macOS host could not be started: " + e.Message, where, e);
        }
    }
#endif

#if WINDOWS
    private static PlatformServices Windows(string dataDir, ILoggerFactory logs)
    {
        var displays = new Platform.Windows.Capture.WindowsDisplayEnumerator();
        var virtualDisplays = new Platform.Windows.Capture.WindowsVirtualDisplays(dataDir, logs.CreateLogger("vdd"));
        var host = new HostPlatform(
            displays,
            new Platform.Windows.Capture.WindowsScreenCapturerFactory(displays, logs),
            new FallbackVideoEncoderFactory(
                new Platform.Windows.Codec.MfVideoEncoderFactory(logs),
                new Codec.OpenH264.OpenH264EncoderFactory(logs),
                new Codec.Vpx.VpxVideoEncoderFactory(logs)),
            new Platform.Windows.Input.WindowsInputInjector(logs.CreateLogger("input")),
            new Platform.Windows.Input.WindowsCursorProvider(),
            device => new Platform.Windows.Audio.WasapiAudioCapture(logs.CreateLogger("audio"), device),
            new Platform.Windows.Clipboard.WindowsClipboard(logs.CreateLogger("clipboard")),
            new Platform.Windows.Capture.WindowsDisplayModes(),
            virtualDisplays);
        return new PlatformServices(host, WindowsStore(dataDir), new Platform.Windows.Security.WindowsMachineIdProvider(), new Platform.Windows.Terminal.WindowsTerminalHost(logs.CreateLogger("terminal")));
    }
#endif
}
