using System.Runtime.InteropServices;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Windows.Native;
using Vortice.DXGI;

namespace DeskPair.Platform.Windows.Capture;

/// <summary>
/// Enumerates monitors through GDI (stable order, primary flag, DPI) and matches each to its DXGI output
/// by device name so the capturer can find the right adapter.
///
/// <see cref="DisplaysChanged"/> is raised for <c>WM_DISPLAYCHANGE</c>: a monitor plugged in or pulled, a
/// resolution change by anyone (including this program), a virtual display appearing. Windows sends it to
/// every top-level window, so the first subscriber starts a hidden one on its own thread; a message-only
/// window would not do, those get no broadcasts. The event fires on that thread and says nothing about
/// what changed -- the subscriber re-enumerates and compares, which is the only reliable way anyway,
/// since Windows sends the message once per monitor it touched.
/// </summary>
public sealed class WindowsDisplayEnumerator : IDisplayEnumerator, IDisposable
{
    private const int ErrorClassAlreadyExists = 1410;

    public sealed record WindowsDisplay(DisplayDescriptor Descriptor, string DeviceName, nint Monitor, int AdapterIndex, int OutputIndex, long AdapterLuid);

    private readonly object _lock = new();
    private readonly string _className = $"DeskPairDisplays.{Guid.NewGuid():N}";
    private readonly TaskCompletionSource<nint> _windowReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private User32.WndProc? _wndProc; // kept alive for the window's lifetime
    private Thread? _pump;
    private nint _hwnd;
    private EventHandler? _displaysChanged;
    private bool _disposed;

    public IReadOnlyList<DisplayDescriptor> GetDisplays() => GetWindowsDisplays().Select(d => d.Descriptor).ToList();

    public event EventHandler? DisplaysChanged
    {
        add
        {
            lock (_lock)
            {
                _displaysChanged += value;
                StartListening();
            }
        }
        remove
        {
            lock (_lock)
            {
                _displaysChanged -= value;
            }
        }
    }

    /// <summary>Whether the listening window is up; a test's way of telling that subscribing did something.</summary>
    internal bool IsListening => _pump is not null;

    /// <summary>Posts the message Windows would post, so the plumbing can be tested without replugging a monitor.</summary>
    internal async Task SimulateDisplayChangeAsync()
    {
        nint hwnd = await _windowReady.Task.ConfigureAwait(false);
        User32.PostMessageW(hwnd, User32.WM_DISPLAYCHANGE, 0, 0);
    }

    private void StartListening()
    {
        if (_pump is not null || _disposed)
        {
            return;
        }

        _wndProc = WindowProc;
        _pump = new Thread(MessageLoop) { Name = "display-listener", IsBackground = true };
        _pump.Start();
    }

    private void MessageLoop()
    {
        nint instance = Kernel32.GetModuleHandleW(null);
        nint className = Marshal.StringToHGlobalUni(_className);
        try
        {
            var wc = new User32.WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<User32.WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc!),
                hInstance = instance,
                lpszClassName = className,
            };
            if (User32.RegisterClassExW(ref wc) == 0 && Marshal.GetLastPInvokeError() != ErrorClassAlreadyExists)
            {
                _windowReady.TrySetException(new InvalidOperationException($"RegisterClassEx failed: {Marshal.GetLastPInvokeError()}"));
                return;
            }

            // A real top-level window, never shown: WM_DISPLAYCHANGE is a broadcast, and broadcasts skip
            // message-only windows.
            nint hwnd = User32.CreateWindowExW(0, _className, "Sunllo DeskPair displays", 0, 0, 0, 0, 0, 0, 0, instance, 0);
            if (hwnd == 0)
            {
                _windowReady.TrySetException(new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastPInvokeError()}"));
                return;
            }

            _hwnd = hwnd;
            _windowReady.TrySetResult(hwnd);
            while (User32.GetMessageW(out User32.MSG msg, 0, 0, 0) > 0)
            {
                User32.TranslateMessage(ref msg);
                User32.DispatchMessageW(ref msg);
            }
        }
        finally
        {
            User32.UnregisterClassW(_className, instance);
            Marshal.FreeHGlobal(className);
        }
    }

    private nint WindowProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case User32.WM_DISPLAYCHANGE:
                EventHandler? handlers;
                lock (_lock)
                {
                    handlers = _displaysChanged;
                }

                try
                {
                    handlers?.Invoke(this, EventArgs.Empty);
                }
                catch (Exception)
                {
                    // A subscriber's failure must not take the message loop down with it.
                }

                return 0;
            case User32.WM_CLOSE:
                User32.DestroyWindow(hwnd);
                return 0;
            case User32.WM_DESTROY:
                User32.PostMessageW(0, User32.WM_QUIT, 0, 0);
                return 0;
        }

        return User32.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        Thread? pump;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            pump = _pump;
        }

        if (pump is null)
        {
            return;
        }

        // The window may still be on its way up; give it a moment so the loop has something to close.
        try
        {
            _windowReady.Task.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // It never came up; the thread has already left.
        }

        if (_windowReady.Task.IsCompletedSuccessfully)
        {
            User32.PostMessageW(_windowReady.Task.Result, User32.WM_CLOSE, 0, 0);
            _hwnd = 0;
        }

        pump.Join(TimeSpan.FromSeconds(2));
    }

    public IReadOnlyList<WindowsDisplay> GetWindowsDisplays()
    {
        var monitors = new List<(nint Handle, User32.RECT Rect, bool Primary, string Device)>();
        User32.EnumDisplayMonitors(0, 0, (nint hMonitor, nint _, ref User32.RECT _, nint _) =>
        {
            var info = new User32.MONITORINFOEXW { cbSize = (uint)Marshal.SizeOf<User32.MONITORINFOEXW>() };
            if (User32.GetMonitorInfo(hMonitor, ref info) != 0)
            {
                string device;
                unsafe
                {
                    device = new string(info.szDevice);
                }

                monitors.Add((hMonitor, info.rcMonitor, (info.dwFlags & User32.MONITORINFOF_PRIMARY) != 0, device));
            }

            return 1;
        }, 0);

        Dictionary<string, (int Adapter, int Output, long Luid, ModeRotation Rotation)> outputs = EnumerateDxgiOutputs();
        var result = new List<WindowsDisplay>();
        int index = 0;
        foreach ((nint handle, User32.RECT rect, bool primary, string device) in monitors.OrderBy(m => m.Primary ? 0 : 1).ThenBy(m => m.Device, StringComparer.Ordinal))
        {
            double scale = 1.0;
            if (User32.GetDpiForMonitor(handle, 0, out uint dpiX, out _) == 0 && dpiX > 0)
            {
                scale = dpiX / 96.0;
            }

            (int adapter, int output, long luid, ModeRotation rotation) = outputs.GetValueOrDefault(device, (-1, -1, 0L, ModeRotation.Identity));
            var descriptor = new DisplayDescriptor(
                index++, device, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, scale,
                rotation switch
                {
                    ModeRotation.Rotate90 => FrameRotation.Rotate90,
                    ModeRotation.Rotate180 => FrameRotation.Rotate180,
                    ModeRotation.Rotate270 => FrameRotation.Rotate270,
                    _ => FrameRotation.None,
                },
                primary, luid);
            result.Add(new WindowsDisplay(descriptor, device, handle, adapter, output, luid));
        }

        return result;
    }

    private static Dictionary<string, (int, int, long, ModeRotation)> EnumerateDxgiOutputs()
    {
        var map = new Dictionary<string, (int, int, long, ModeRotation)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (int a = 0; factory.EnumAdapters1((uint)a, out IDXGIAdapter1? adapter).Success && adapter is not null; a++)
            {
                using (adapter)
                {
                    long luid = adapter.Description1.Luid;
                    for (int o = 0; adapter.EnumOutputs((uint)o, out IDXGIOutput? output).Success && output is not null; o++)
                    {
                        using (output)
                        {
                            OutputDescription desc = output.Description;
                            if (desc.AttachedToDesktop)
                            {
                                map[desc.DeviceName] = (a, o, luid, desc.Rotation);
                            }
                        }
                    }
                }
            }
        }
        catch (Exception)
        {
            // No DXGI (e.g. session 0 without a GPU); GDI-only displays still work.
        }

        return map;
    }
}
