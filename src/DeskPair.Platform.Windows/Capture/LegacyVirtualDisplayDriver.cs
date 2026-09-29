using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using DeskPair.Platform.Windows.Native;

namespace DeskPair.Platform.Windows.Capture;

/// <summary>
/// The Virtual Display Driver (MttVDD, github.com/VirtualDrivers/Virtual-Display-Driver) DeskPair installed before it
/// had a driver of its own: found by the record that install left in <c>vdd</c> under the data directory, and taken away
/// -- the device, the driver package, that folder (its settings and the record) and the registry value pointing the
/// driver at it. Nothing installs it any more; <see cref="DisplayDriverInstaller"/> removes it on the way.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class LegacyVirtualDisplayDriver
{
    private const string RegistryKey = @"SOFTWARE\MikeTheTech\VirtualDisplayDriver";
    private const string PathValue = "VDDPATH";
    private const string RecordFile = "installed";

    public static string ConfigDirectory(string dataDir) => Path.Combine(dataDir, "vdd");

    public static bool IsInstalled(string dataDir) => File.Exists(Path.Combine(ConfigDirectory(dataDir), RecordFile));

    public static int Uninstall(string dataDir, ILogger log)
    {
        string config = ConfigDirectory(dataDir);
        (string? inf, string? device) = Recorded(Path.Combine(config, RecordFile));
        int code = 0;

        if (device is not null)
        {
            if (DisplayDriverInstaller.RemoveDevice(device))
            {
                log.LogInformation("Removed the Virtual Display Driver's device {Device}", device);
            }
            else
            {
                log.LogError("Could not remove the Virtual Display Driver's device {Device} (error {Error})", device, Marshal.GetLastPInvokeError());
                code = 1;
            }
        }

        if (inf is not null)
        {
            if (DeviceSetup.SetupUninstallOEMInf(inf, DeviceSetup.SUOI_FORCEDELETE, 0))
            {
                log.LogInformation("Removed {Inf} (the Virtual Display Driver) from the driver store", inf);
            }
            else
            {
                log.LogError("Could not remove {Inf} (the Virtual Display Driver) from the driver store (error {Error})", inf, Marshal.GetLastPInvokeError());
                code = 1;
            }
        }

        // The pointer is ours only while it points at our folder: somebody's own install of the driver keeps theirs.
        using (RegistryKey? key = Registry.LocalMachine.OpenSubKey(RegistryKey, writable: true))
        {
            if (key?.GetValue(PathValue) is string path && SamePath(path, config))
            {
                key.DeleteValue(PathValue, throwOnMissingValue: false);
            }
        }

        DeleteIfEmpty(RegistryKey);
        DeleteIfEmpty(RegistryKey[..RegistryKey.LastIndexOf('\\')]);

        if (Directory.Exists(config))
        {
            Directory.Delete(config, recursive: true);
        }

        return code;
    }

    private static (string? Inf, string? Device) Recorded(string record)
    {
        if (!File.Exists(record))
        {
            return (null, null);
        }

        Dictionary<string, string> values = File.ReadAllLines(record)
            .Select(l => l.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0].Trim(), p => p[1].Trim(), StringComparer.Ordinal);
        return (
            values.TryGetValue("inf", out string? inf) && inf.Length > 0 ? inf : null,
            values.TryGetValue("device", out string? device) && device.Length > 0 ? device : null);
    }

    /// <summary>The driver's key and the vendor key above it came with the install; left empty they would be what stayed.</summary>
    private static void DeleteIfEmpty(string subKey)
    {
        bool empty;
        using (RegistryKey? key = Registry.LocalMachine.OpenSubKey(subKey))
        {
            empty = key is { ValueCount: 0, SubKeyCount: 0 };
        }

        if (empty)
        {
            Registry.LocalMachine.DeleteSubKey(subKey, throwOnMissingSubKey: false);
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}
