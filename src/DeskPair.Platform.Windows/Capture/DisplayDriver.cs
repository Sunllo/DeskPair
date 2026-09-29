using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Windows.Native;

namespace DeskPair.Platform.Windows.Capture;

/// <summary>
/// DeskPair's virtual display driver (<c>native/idd</c>): where a build carries it, what an install recorded, and the
/// control interface its adapter offers (<see cref="IddControl"/>) -- plug a display into a slot, switch it to another of
/// its sizes, unplug it, feed the watchdog. Every call opens the interface afresh, so a driver updated or restarted in
/// between is simply the one answering; opening it takes SYSTEM or an administrator.
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class DisplayDriver
{
    /// <summary>The hardware id the driver's INF answers to, and the root-enumerated device the install makes carries.</summary>
    public const string HardwareId = @"Root\DeskPairDisplay";

    /// <summary>The files a package directory must hold.</summary>
    internal static readonly string[] PackageFiles = ["DeskPairDisplay.inf", "DeskPairDisplay.dll", "deskpairdisplay.cat"];

    private const string RecordFile = "installed";

    /// <summary>Where a build carries the driver package: an <c>idd</c> folder beside the program.</summary>
    public static string PackageDirectory => Path.Combine(AppContext.BaseDirectory, "idd");

    /// <summary>Where the install record lives on this machine.</summary>
    public static string RecordDirectory(string dataDir) => Path.Combine(dataDir, "idd");

    /// <summary>What an install recorded: the driver store's name for the package, the device it made, the version it put there.</summary>
    public sealed record Installation(string PublishedInf, string? Device, Version? DriverVersion);

    /// <summary>The installation recorded under <paramref name="dataDir"/>, or null when there is none.</summary>
    public static Installation? Installed(string dataDir)
    {
        string record = Path.Combine(RecordDirectory(dataDir), RecordFile);
        if (!File.Exists(record))
        {
            return null;
        }

        Dictionary<string, string> values = File.ReadAllLines(record)
            .Select(l => l.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0].Trim(), p => p[1].Trim(), StringComparer.Ordinal);
        return values.TryGetValue("inf", out string? inf) && inf.Length > 0
            ? new Installation(
                inf,
                values.TryGetValue("device", out string? d) && d.Length > 0 ? d : null,
                values.TryGetValue("version", out string? v) && Version.TryParse(v, out Version? version) ? version : null)
            : null;
    }

    internal static void RecordInstallation(string dataDir, Installation installation) =>
        File.WriteAllLines(
            Path.Combine(RecordDirectory(dataDir), RecordFile),
            [$"inf={installation.PublishedInf}", $"device={installation.Device ?? string.Empty}", $"version={installation.DriverVersion?.ToString() ?? string.Empty}"]);

    /// <summary>The version in a package's INF (<c>DriverVer=date,a.b.c.d</c>), or null when it has none that reads.</summary>
    public static Version? PackageVersion(string packageDir)
    {
        string inf = Path.Combine(packageDir, PackageFiles[0]);
        if (!File.Exists(inf))
        {
            return null;
        }

        Match match = DriverVer().Match(File.ReadAllText(inf));
        return match.Success && Version.TryParse(match.Groups[1].Value, out Version? version) ? version : null;
    }

    [GeneratedRegex(@"^\s*DriverVer\s*=\s*[^,\r\n]*,\s*(\d+\.\d+\.\d+\.\d+)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex DriverVer();

    // ---- the control interface ----

    /// <summary>The control interface's path, or null when no adapter of this driver is running.</summary>
    public static unsafe string? InterfacePath()
    {
        Guid kind = IddControl.Interface;
        if (IddControl.CM_Get_Device_Interface_List_Size(out uint length, in kind, null, IddControl.CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != DeviceSetup.CR_SUCCESS
            || length <= 1)
        {
            return null;
        }

        char[] buffer = new char[length];
        fixed (char* chars = buffer)
        {
            if (IddControl.CM_Get_Device_Interface_List(in kind, null, chars, length, IddControl.CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != DeviceSetup.CR_SUCCESS)
            {
                return null;
            }
        }

        int end = Array.IndexOf(buffer, '\0');
        return end > 0 ? new string(buffer, 0, end) : null;
    }

    /// <summary>Whether the driver's adapter is running (not whether this process may talk to it).</summary>
    public static bool IsRunning => InterfacePath() is not null;

    internal static unsafe IddControl.DESKPAIR_DISPLAY_INFO Info()
    {
        IddControl.DESKPAIR_DISPLAY_INFO info = default;
        Call(IddControl.IOCTL_DESKPAIR_DISPLAY_INFO, null, 0, &info, sizeof(IddControl.DESKPAIR_DISPLAY_INFO));
        return info;
    }

    /// <summary>
    /// Plugs a display into <paramref name="slot"/> with <paramref name="modes"/>, at <paramref name="current"/> of them.
    /// Returns what finds it in the display configuration: its adapter and target.
    /// </summary>
    internal static unsafe (User32.LUID Adapter, uint TargetId) Plug(int slot, int current, IReadOnlyList<DisplayMode> modes)
    {
        int size = sizeof(IddControl.DESKPAIR_DISPLAY_PLUG) + modes.Count * sizeof(IddControl.DESKPAIR_DISPLAY_MODE);
        byte* request = stackalloc byte[size];
        *(IddControl.DESKPAIR_DISPLAY_PLUG*)request = new IddControl.DESKPAIR_DISPLAY_PLUG
        {
            Protocol = IddControl.Protocol,
            Slot = (uint)slot,
            Current = (uint)current,
            ModeCount = (uint)modes.Count,
        };
        var list = (IddControl.DESKPAIR_DISPLAY_MODE*)(request + sizeof(IddControl.DESKPAIR_DISPLAY_PLUG));
        for (int i = 0; i < modes.Count; i++)
        {
            list[i] = new IddControl.DESKPAIR_DISPLAY_MODE { Width = (uint)modes[i].Width, Height = (uint)modes[i].Height, RefreshHz = 60 };
        }

        IddControl.DESKPAIR_DISPLAY_PLUGGED plugged = default;
        Call(IddControl.IOCTL_DESKPAIR_DISPLAY_PLUG, request, size, &plugged, sizeof(IddControl.DESKPAIR_DISPLAY_PLUGGED));
        return (new User32.LUID { LowPart = plugged.AdapterLuidLow, HighPart = plugged.AdapterLuidHigh }, plugged.TargetId);
    }

    internal static unsafe void Unplug(int slot)
    {
        var request = new IddControl.DESKPAIR_DISPLAY_UNPLUG { Protocol = IddControl.Protocol, Slot = (uint)slot };
        Call(IddControl.IOCTL_DESKPAIR_DISPLAY_UNPLUG, &request, sizeof(IddControl.DESKPAIR_DISPLAY_UNPLUG), null, 0);
    }

    /// <summary>Switches the display in <paramref name="slot"/> to its size at <paramref name="index"/>; Windows follows at once.</summary>
    internal static unsafe void Select(int slot, int index)
    {
        var request = new IddControl.DESKPAIR_DISPLAY_SELECT { Protocol = IddControl.Protocol, Slot = (uint)slot, Current = (uint)index };
        Call(IddControl.IOCTL_DESKPAIR_DISPLAY_SELECT, &request, sizeof(IddControl.DESKPAIR_DISPLAY_SELECT), null, 0);
    }

    /// <summary>Arms or feeds the watchdog: every display goes when <paramref name="timeout"/> passes without another call. Zero disarms it.</summary>
    internal static unsafe void Watchdog(TimeSpan timeout)
    {
        var request = new IddControl.DESKPAIR_DISPLAY_WATCHDOG { Protocol = IddControl.Protocol, TimeoutMs = (uint)timeout.TotalMilliseconds };
        Call(IddControl.IOCTL_DESKPAIR_DISPLAY_WATCHDOG, &request, sizeof(IddControl.DESKPAIR_DISPLAY_WATCHDOG), null, 0);
    }

    /// <summary>Win32 errors the driver answers with, as <see cref="Win32Exception.NativeErrorCode"/>.</summary>
    internal const int ErrorNotFound = 1168;
    internal const int ErrorBusy = 170;
    internal const int ErrorRevisionMismatch = 1306;

    private static unsafe void Call(uint code, void* input, int inputLength, void* output, int outputLength)
    {
        string path = InterfacePath() ?? throw new Win32Exception(2, "DeskPair's virtual display driver is not running.");
        using SafeFileHandle device = IddControl.CreateFile(
            path, IddControl.GENERIC_READ | IddControl.GENERIC_WRITE, IddControl.FILE_SHARE_READ | IddControl.FILE_SHARE_WRITE, 0, IddControl.OPEN_EXISTING, 0, 0);
        if (device.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        if (!IddControl.DeviceIoControl(device, code, input, (uint)inputLength, output, (uint)outputLength, out _, 0))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }
}
