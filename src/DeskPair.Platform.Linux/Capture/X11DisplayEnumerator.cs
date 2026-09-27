using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Linux.Native;

namespace DeskPair.Platform.Linux.Capture;

/// <summary>
/// Lists monitors through XRandR. Each connected crtc becomes one <see cref="DisplayDescriptor"/> positioned
/// on the virtual desktop, so the capturer can read just that monitor's rectangle out of the root window.
/// A machine with no XRandR — or a headless X server — still yields one display covering the whole screen,
/// because a remote desktop with no display to name is worse than one with a synthetic name.
/// </summary>
public sealed class X11DisplayEnumerator : IDisplayEnumerator, IDisposable
{
    private readonly ILogger _log;
    private readonly nint _display;
    private readonly nint _root;

    public X11DisplayEnumerator(ILogger log)
    {
        Xlib.EnsureThreadSafe();
        _log = log;
        _display = Xlib.XOpenDisplay(null);
        if (_display == 0)
        {
            throw new InvalidOperationException("Cannot open the X display; is DISPLAY set and XAUTHORITY readable?");
        }

        _root = Xlib.XDefaultRootWindow(_display);
    }

    public event EventHandler? DisplaysChanged
    {
        add { }
        remove { }
    }

    public IReadOnlyList<DisplayDescriptor> GetDisplays()
    {
        List<DisplayDescriptor> displays = FromXrandr();
        if (displays.Count > 0)
        {
            return displays;
        }

        // No XRandR, or nothing connected: fall back to the whole root window as one display.
        int screen = Xlib.XDefaultScreen(_display);
        int width = Xlib.XDisplayWidth(_display, screen);
        int height = Xlib.XDisplayHeight(_display, screen);
        return [new DisplayDescriptor(0, ":0", 0, 0, width, height, 1.0, FrameRotation.None, true, 0)];
    }

    private List<DisplayDescriptor> FromXrandr()
    {
        var result = new List<DisplayDescriptor>();
        nint resourcesPtr = Xrandr.XRRGetScreenResourcesCurrent(_display, _root);
        if (resourcesPtr == 0)
        {
            return result;
        }

        try
        {
            var resources = Marshal.PtrToStructure<Xrandr.XRRScreenResources>(resourcesPtr);
            nint primary = Xrandr.XRRGetOutputPrimary(_display, _root);
            int index = 0;

            for (int i = 0; i < resources.NOutput; i++)
            {
                nint output = Marshal.ReadIntPtr(resources.Outputs, i * nint.Size);
                nint outInfoPtr = Xrandr.XRRGetOutputInfo(_display, resourcesPtr, output);
                if (outInfoPtr == 0)
                {
                    continue;
                }

                try
                {
                    var outInfo = Marshal.PtrToStructure<Xrandr.XRROutputInfo>(outInfoPtr);
                    if (outInfo.Connection != Xrandr.RrConnected || outInfo.Crtc == 0)
                    {
                        continue;
                    }

                    nint crtcPtr = Xrandr.XRRGetCrtcInfo(_display, resourcesPtr, outInfo.Crtc);
                    if (crtcPtr == 0)
                    {
                        continue;
                    }

                    try
                    {
                        var crtc = Marshal.PtrToStructure<Xrandr.XRRCrtcInfo>(crtcPtr);
                        if (crtc.Mode == 0 || crtc.Width == 0 || crtc.Height == 0)
                        {
                            continue;
                        }

                        string name = outInfo.NameLen > 0 && outInfo.Name != 0
                            ? Marshal.PtrToStringUTF8(outInfo.Name, outInfo.NameLen)
                            : $"output-{index}";
                        result.Add(new DisplayDescriptor(
                            index++,
                            name,
                            crtc.X,
                            crtc.Y,
                            (int)crtc.Width,
                            (int)crtc.Height,
                            1.0,
                            FrameRotation.None,
                            output == primary,
                            0));
                    }
                    finally
                    {
                        Xrandr.XRRFreeCrtcInfo(crtcPtr);
                    }
                }
                finally
                {
                    Xrandr.XRRFreeOutputInfo(outInfoPtr);
                }
            }

            // The primary monitor goes first; the index is then the position, which is what every consumer
            // of the list assumes (see DisplayOrdering).
            result = DisplayOrdering.PrimaryFirst(result);
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "XRandR enumeration failed; falling back to the whole screen");
            result.Clear();
        }
        finally
        {
            Xrandr.XRRFreeScreenResources(resourcesPtr);
        }

        return result;
    }

    public void Dispose()
    {
        if (_display != 0)
        {
            Xlib.XCloseDisplay(_display);
        }
    }
}
