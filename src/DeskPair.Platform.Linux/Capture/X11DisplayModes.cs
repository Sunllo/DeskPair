using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Linux.Hosting;
using DeskPair.Platform.Linux.Native;

namespace DeskPair.Platform.Linux.Capture;

/// <summary>
/// The modes an XRandR output advertises, and switching between them by running <c>xrandr</c>.
///
/// Listing goes through libXrandr so it agrees with <see cref="X11DisplayEnumerator"/>, which names each
/// display after its output. Switching shells out to the xrandr tool on purpose: it already knows how to
/// grow or shrink the screen, keep the other outputs where they were and pick a CRTC, and getting that
/// wrong by hand leaves a desktop nobody can see. Nothing is written anywhere -- an X server that restarts
/// is back to its configured mode. Under Wayland the RandR extension is Xwayland's fiction, so there are
/// no modes to offer.
/// </summary>
public sealed class X11DisplayModes : IDisplayModeSwitcher, IDisposable
{
    private readonly ILogger _log;
    private readonly bool _wayland;
    private readonly object _lock = new();
    private nint _display;

    public X11DisplayModes(ILogger log)
        : this(log, new LinuxPlatformInfo().IsWayland)
    {
    }

    internal X11DisplayModes(ILogger log, bool wayland)
    {
        _log = log;
        _wayland = wayland;
    }

    public IReadOnlyList<DisplayMode> GetModes(DisplayDescriptor display)
    {
        if (_wayland || ModesOf(display.Name) is not { } modes)
        {
            return [];
        }

        return modes
            .Where(m => m.Width >= 640 && m.Height >= 480)
            .Select(m => (m.Width, m.Height))
            .Distinct()
            .OrderByDescending(s => (long)s.Width * s.Height)
            .ThenByDescending(s => s.Width)
            .Select(s => new DisplayMode((int)s.Width, (int)s.Height))
            .ToList();
    }

    public bool TrySetMode(DisplayDescriptor display, DisplayMode mode, out string? failure)
    {
        if (_wayland)
        {
            failure = "The resolution cannot be changed from here under Wayland.";
            return false;
        }

        if (!XrandrTool.IsSafeName(display.Name))
        {
            failure = "The display has no output name xrandr would accept.";
            return false;
        }

        failure = XrandrTool.Run(["--output", display.Name, "--mode", ModeName(display.Name, mode)], _log);
        return failure is null;
    }

    /// <summary>
    /// What xrandr calls the mode of this size on this output. Usually just <c>WxH</c>; a size taught to it has a name
    /// of its own (<see cref="X11ModeTeacher"/>), and xrandr finds a mode by its name, not by its size.
    /// </summary>
    private string ModeName(string output, DisplayMode mode)
    {
        string plain = $"{mode.Width}x{mode.Height}";
        if (ModesOf(output) is not { } modes || modes.Any(m => m.Name == plain))
        {
            return plain;
        }

        return modes.FirstOrDefault(m => m.Width == mode.Width && m.Height == mode.Height && XrandrTool.IsSafeName(m.Name)).Name ?? plain;
    }

    /// <summary>The modes of the connected output called <paramref name="output"/>, by name and size; null when there is no such output.</summary>
    private List<(string Name, uint Width, uint Height)>? ModesOf(string output)
    {
        lock (_lock)
        {
            nint dpy = Display();
            if (dpy == 0)
            {
                return null;
            }

            nint root = Xlib.XDefaultRootWindow(dpy);
            nint resourcesPtr = Xrandr.XRRGetScreenResourcesCurrent(dpy, root);
            if (resourcesPtr == 0)
            {
                return null;
            }

            try
            {
                var resources = Marshal.PtrToStructure<Xrandr.XRRScreenResources>(resourcesPtr);
                List<nint>? wanted = ModeIdsOf(dpy, resourcesPtr, resources, output);
                if (wanted is null)
                {
                    return null;
                }

                // Every mode the server knows, by id; then the ones this output can show, in its order.
                var byId = new Dictionary<nint, (string Name, uint Width, uint Height)>();
                int modeSize = Marshal.SizeOf<Xrandr.XRRModeInfo>();
                for (int i = 0; i < resources.NMode; i++)
                {
                    var mode = Marshal.PtrToStructure<Xrandr.XRRModeInfo>(resources.Modes + i * modeSize);
                    string name = mode.Name != 0 && mode.NameLength > 0 ? Marshal.PtrToStringUTF8(mode.Name, (int)mode.NameLength) : string.Empty;
                    byId[mode.Id] = (name, mode.Width, mode.Height);
                }

                return [.. wanted.Where(byId.ContainsKey).Select(id => byId[id])];
            }
            finally
            {
                Xrandr.XRRFreeScreenResources(resourcesPtr);
            }
        }
    }

    /// <summary>The mode ids of the connected output called <paramref name="name"/>, or null when there is none.</summary>
    private static List<nint>? ModeIdsOf(nint dpy, nint resourcesPtr, Xrandr.XRRScreenResources resources, string name)
    {
        for (int i = 0; i < resources.NOutput; i++)
        {
            nint output = Marshal.ReadIntPtr(resources.Outputs, i * nint.Size);
            nint infoPtr = Xrandr.XRRGetOutputInfo(dpy, resourcesPtr, output);
            if (infoPtr == 0)
            {
                continue;
            }

            try
            {
                var info = Marshal.PtrToStructure<Xrandr.XRROutputInfo>(infoPtr);
                string outputName = info.NameLen > 0 && info.Name != 0 ? Marshal.PtrToStringUTF8(info.Name, info.NameLen) : string.Empty;
                if (outputName != name || info.Connection != Xrandr.RrConnected)
                {
                    continue;
                }

                var ids = new List<nint>(info.NMode);
                for (int m = 0; m < info.NMode; m++)
                {
                    ids.Add(Marshal.ReadIntPtr(info.Modes, m * nint.Size));
                }

                return ids;
            }
            finally
            {
                Xrandr.XRRFreeOutputInfo(infoPtr);
            }
        }

        return null;
    }

    private nint Display()
    {
        if (_display == 0)
        {
            Xlib.EnsureThreadSafe();
            _display = Xlib.XOpenDisplay(null);
            if (_display == 0)
            {
                _log.LogWarning("Cannot open the X display to list its modes");
            }
        }

        return _display;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_display != 0)
            {
                Xlib.XCloseDisplay(_display);
                _display = 0;
            }
        }
    }
}
