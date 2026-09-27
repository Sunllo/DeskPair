using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Windows.Native;

namespace DeskPair.Platform.Windows.Capture;

/// <summary>
/// Displays that do not exist, through <see cref="VirtualDisplayDriver"/>: the device the install made for it,
/// disabled until a viewer asks for a display, and rebuilt -- disabled, its settings file rewritten, enabled -- each
/// time the number wanted changes. Enabling and disabling it needs administrator rights, so this is the service's
/// engine's; the app's reports itself unavailable.
///
/// A rebuild unplugs every virtual monitor and plugs the new count back in, under new names (\\.\DISPLAY10 comes
/// back as \\.\DISPLAY11). So taking one away takes the last -- they are identical, and whatever was on it moves to
/// the others the way Windows does for any unplugged monitor -- and adding one restarts the others' streams. For
/// the same reason a size is not taught to a display already up: it would come back as another display. The size
/// asked for when a display is added goes into the file first, and the common sizes are always there.
///
/// An engine that dies with displays plugged in leaves the device enabled; the next one disables it on start.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsVirtualDisplays : IVirtualDisplayProvider, IDisposable
{
    private static readonly TimeSpan GoneTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan AppearTimeout = TimeSpan.FromSeconds(20);

    private readonly string _dataDir;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<DisplayMode> _asked = [];
    private DisplayMode? _preferred;
    private int _count;

    public WindowsVirtualDisplays(string dataDir, ILogger log)
    {
        _dataDir = dataDir;
        _log = log;
        if (Environment.IsPrivilegedProcess && Device() is { } device && Started(device))
        {
            _log.LogInformation("Virtual displays: the device was left enabled; disabling it");
            Disable(device);
        }
    }

    public string? UnavailableReason =>
        !Environment.IsPrivilegedProcess
            ? "This computer can add a display only while DeskPair runs as its service (unattended access)."
            : Device() is null
                ? "DeskPair's virtual display driver is not installed on this computer."
                : null;

    public int Count => Volatile.Read(ref _count);

    public bool IsVirtual(DisplayDescriptor display) =>
        Count > 0 && VirtualSources().Contains(display.Name, StringComparer.OrdinalIgnoreCase);

    public async Task<DisplayActionResult> AddAsync(DisplayMode? mode, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (UnavailableReason is { } why)
            {
                return DisplayActionResult.Refused(why);
            }

            // First in the file is what the new monitor comes up at, unless Windows remembers another size for it;
            // either way the size is on offer, so the host can set it without teaching anything.
            if (mode is { } m)
            {
                _preferred = m;
                if (!_asked.Any(a => a.Width == m.Width && a.Height == m.Height))
                {
                    _asked.Add(m);
                }
            }

            return await RebuildAsync(_count + 1, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DisplayActionResult> RemoveAsync(DisplayDescriptor display, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!IsVirtual(display))
            {
                return DisplayActionResult.Refused("Only a display that was added from here can be removed.");
            }

            return await RebuildAsync(_count - 1, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAllAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_count > 0)
            {
                await RebuildAsync(0, ct).ConfigureAwait(false);
            }

            // The sizes asked for were for these displays; the next ones start from the common list again.
            _asked.Clear();
            _preferred = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        // No waiting: this is the engine stopping. Nothing it plugged in outlives it.
        if (Count > 0 && Device() is { } device)
        {
            Disable(device);
        }

        _gate.Dispose();
    }

    /// <summary>The GDI names (<c>\\.\DISPLAYn</c>) of the sources the driver's adapter drives.</summary>
    internal static List<string> VirtualSources()
    {
        var names = new List<string>();
        var device = new User32.DISPLAY_DEVICEW { cb = (uint)Marshal.SizeOf<User32.DISPLAY_DEVICEW>() };
        for (uint i = 0; User32.EnumDisplayDevices(null, i, ref device, 0); i++)
        {
            string name, id;
            unsafe
            {
                name = new string(device.DeviceName);
                id = new string(device.DeviceID);
            }

            if (id.Equals(VirtualDisplayDriver.HardwareId, StringComparison.OrdinalIgnoreCase))
            {
                names.Add(name);
            }

            device = new User32.DISPLAY_DEVICEW { cb = (uint)Marshal.SizeOf<User32.DISPLAY_DEVICEW>() };
        }

        return names;
    }

    /// <summary>
    /// Makes the device show <paramref name="count"/> monitors (none: disabled), or puts back the ones there were
    /// when that fails: a third display that will not start must not take the first two with it. Under the gate.
    /// </summary>
    private async Task<DisplayActionResult> RebuildAsync(int count, CancellationToken ct)
    {
        int before = Count;
        DisplayActionResult result = await BuildAsync(count, ct).ConfigureAwait(false);
        if (!result.Succeeded && before > 0 && before != count)
        {
            await BuildAsync(before, ct).ConfigureAwait(false);
        }

        return result;
    }

    private async Task<DisplayActionResult> BuildAsync(int count, CancellationToken ct)
    {
        if (Device() is not { } device)
        {
            return Failed("DeskPair's virtual display driver is not installed on this computer.");
        }

        // Already off is as good as switched off; only one still running is a failure.
        int disabled = Disable(device);
        if (disabled != DeviceSetup.CR_SUCCESS && Started(device))
        {
            return Failed($"The virtual display device could not be switched off (CONFIGRET {disabled}).");
        }

        Volatile.Write(ref _count, 0);
        if (!await WaitForSourcesAsync(0, GoneTimeout, ct).ConfigureAwait(false))
        {
            _log.LogWarning("Virtual displays: still listed {Seconds} s after the device was switched off", GoneTimeout.TotalSeconds);
        }

        if (count <= 0)
        {
            _log.LogInformation("Virtual displays: none");
            return DisplayActionResult.Done;
        }

        VirtualDisplayDriver.WriteSettings(_dataDir, count, _preferred, _asked);
        int enabled = DeviceSetup.CM_Enable_DevNode(device, 0);
        if (enabled != DeviceSetup.CR_SUCCESS)
        {
            return Failed($"The virtual display device could not be switched on (CONFIGRET {enabled}).");
        }

        // Started is not arrived: the driver loads, reads its file and plugs its monitors in after this.
        if (!await WaitForSourcesAsync(count, AppearTimeout, ct).ConfigureAwait(false))
        {
            Disable(device);
            return Failed("The virtual display driver started but its displays did not appear.");
        }

        Volatile.Write(ref _count, count);
        _log.LogInformation("Virtual displays: {Count} ({Sources})", count, string.Join(", ", VirtualSources()));
        return DisplayActionResult.Done;
    }

    private static async Task<bool> WaitForSourcesAsync(int count, TimeSpan timeout, CancellationToken ct)
    {
        long start = Environment.TickCount64;
        while (VirtualSources().Count != count)
        {
            if (Environment.TickCount64 - start > timeout.TotalMilliseconds)
            {
                return false;
            }

            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        return true;
    }

    private DisplayActionResult Failed(string why)
    {
        _log.LogWarning("Virtual displays: {Why}", why);
        return DisplayActionResult.Refused(why);
    }

    /// <summary>The device the install made, if it is recorded and still there.</summary>
    private uint? Device() =>
        VirtualDisplayDriver.Installed(_dataDir)?.Device is { } id
        && DeviceSetup.CM_Locate_DevNode(out uint device, id, DeviceSetup.CM_LOCATE_DEVNODE_NORMAL) == DeviceSetup.CR_SUCCESS
            ? device
            : null;

    private static bool Started(uint device) =>
        DeviceSetup.CM_Get_DevNode_Status(out uint status, out _, device, 0) == DeviceSetup.CR_SUCCESS
        && (status & DeviceSetup.DN_STARTED) != 0;

    /// <summary>Disabled across restarts as well: a machine that reboots with displays plugged in must not come back with them.</summary>
    private static int Disable(uint device) =>
        DeviceSetup.CM_Disable_DevNode(device, DeviceSetup.CM_DISABLE_UI_NOT_OK | DeviceSetup.CM_DISABLE_PERSIST);
}
