using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using DeskPair.Platform.Windows.Native;

namespace DeskPair.Platform.Windows.Capture;

/// <summary>
/// Putting <see cref="DisplayDriver"/> on this machine, bringing it up to date and taking it off again. All of it needs
/// administrator rights; none of it plugs in a display -- the engine does that when a viewer asks for one.
///
/// Installing stages the package in the driver store and puts it on the one root-enumerated device it runs on, the way
/// devcon does, made the first time and kept after. The device stays enabled: with no display plugged in, the adapter
/// shows nothing, and the engine talks to it through its control interface. The package is signed with an ordinary
/// Authenticode certificate, not by Microsoft, and Windows stages such a package without asking only when its publisher
/// is in the machine's Trusted Publishers -- otherwise it asks, and with nobody to answer (a service, a script) it fails.
/// So the publisher is trusted while the package is staged and no longer: left there, the certificate would make this
/// machine accept anything else it signs, silently. Installing it on the device afterwards, from the store, does not
/// ask again (measured).
///
/// An install also takes away the Virtual Display Driver an earlier DeskPair installed (<see cref="LegacyVirtualDisplayDriver"/>).
/// </summary>
[SupportedOSPlatform("windows")]
public static class DisplayDriverInstaller
{
    private const string DeviceDescription = "DeskPair virtual displays";
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(15);

    public static int Install(string dataDir, string packageDir, ILogger log)
    {
        if (!Environment.IsPrivilegedProcess)
        {
            log.LogError("Installing the virtual display driver needs administrator rights.");
            return 2;
        }

        if (DisplayDriver.PackageFiles.FirstOrDefault(f => !File.Exists(Path.Combine(packageDir, f))) is { } missing)
        {
            log.LogError("This copy of DeskPair carries no virtual display driver ({File} is not in {Dir}).", missing, packageDir);
            return 2;
        }

        try
        {
            if (LegacyVirtualDisplayDriver.IsInstalled(dataDir))
            {
                log.LogInformation("Replacing the Virtual Display Driver an earlier DeskPair installed");
                LegacyVirtualDisplayDriver.Uninstall(dataDir, log);
            }

            CreateRecordDirectory(DisplayDriver.RecordDirectory(dataDir));
            DisplayDriver.Installation? before = DisplayDriver.Installed(dataDir);
            string published = StageTrusted(Path.Combine(packageDir, DisplayDriver.PackageFiles[0]), Path.Combine(packageDir, DisplayDriver.PackageFiles[2]), log);

            // A second install keeps the device the first one made rather than adding another beside it.
            string device = before?.Device is { } kept && Locate(kept) is not null ? kept : CreateDevice(DisplayDriver.HardwareId);
            InstallOn(device, DisplayDriver.HardwareId, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", published), log);

            // The package an earlier install staged is no longer on any device once this one is.
            if (before is not null && !string.Equals(before.PublishedInf, published, StringComparison.OrdinalIgnoreCase))
            {
                if (DeviceSetup.SetupUninstallOEMInf(before.PublishedInf, 0, 0))
                {
                    log.LogInformation("Removed the earlier package {Inf} from the driver store", before.PublishedInf);
                }
                else
                {
                    log.LogWarning("Could not remove the earlier package {Inf} from the driver store (error {Error})", before.PublishedInf, Marshal.GetLastPInvokeError());
                }
            }

            Version? version = DisplayDriver.PackageVersion(packageDir);
            DisplayDriver.RecordInstallation(dataDir, new DisplayDriver.Installation(published, device, version));

            long start = Environment.TickCount64;
            while (!DisplayDriver.IsRunning && Environment.TickCount64 - start < StartTimeout.TotalMilliseconds)
            {
                Thread.Sleep(200);
            }

            if (!DisplayDriver.IsRunning)
            {
                log.LogWarning("The virtual display driver {Version} is installed as {Inf} on {Device}, but its adapter has not started; restarting the computer may help", version, published, device);
                return 1;
            }

            log.LogInformation("The virtual display driver {Version} is installed as {Inf} on {Device}; it shows nothing until a viewer asks for a display", version, published, device);
            return 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or System.ComponentModel.Win32Exception)
        {
            log.LogError(e, "Could not install the virtual display driver");
            return 1;
        }
    }

    /// <summary>
    /// Run as the service's engine starts. When an earlier DeskPair installed the Virtual Display Driver, or this build
    /// carries a newer package of this driver than the one installed, installs this build's: a viewer's display then
    /// works the way this build expects. Nothing when neither driver is on the machine -- installing one is the owner's
    /// choice, made in the settings.
    /// </summary>
    public static void Update(string dataDir, string packageDir, ILogger log)
    {
        bool legacy = LegacyVirtualDisplayDriver.IsInstalled(dataDir);
        DisplayDriver.Installation? installed = DisplayDriver.Installed(dataDir);
        if (!legacy && installed is null)
        {
            return;
        }

        Version? package = DisplayDriver.PackageVersion(packageDir);
        if (package is null)
        {
            if (legacy)
            {
                log.LogWarning("This copy of DeskPair carries no virtual display driver to replace the Virtual Display Driver with");
            }

            return;
        }

        if (!legacy && installed?.DriverVersion is { } current && current >= package && DisplayDriver.IsRunning)
        {
            return;
        }

        log.LogInformation("Bringing the virtual display driver up to date: {From} -> {To}", legacy ? "Virtual Display Driver" : installed?.DriverVersion?.ToString() ?? "unknown", package);
        Install(dataDir, packageDir, log);
    }

    /// <summary>
    /// Takes away the device, the driver package and the install record -- and the Virtual Display Driver of an earlier
    /// DeskPair, if it is still there. What is already gone is skipped: the caller asked for it to be gone, and it is.
    /// </summary>
    public static int Uninstall(string dataDir, ILogger log)
    {
        if (!Environment.IsPrivilegedProcess)
        {
            log.LogError("Removing the virtual display driver needs administrator rights.");
            return 2;
        }

        int code = LegacyVirtualDisplayDriver.IsInstalled(dataDir) ? LegacyVirtualDisplayDriver.Uninstall(dataDir, log) : 0;
        DisplayDriver.Installation? installed = DisplayDriver.Installed(dataDir);
        if (installed?.Device is { } device)
        {
            if (RemoveDevice(device))
            {
                log.LogInformation("Removed the device {Device}", device);
            }
            else
            {
                log.LogError("Could not remove the device {Device} (error {Error})", device, Marshal.GetLastPInvokeError());
                code = 1;
            }
        }

        if (installed is not null)
        {
            if (DeviceSetup.SetupUninstallOEMInf(installed.PublishedInf, DeviceSetup.SUOI_FORCEDELETE, 0))
            {
                log.LogInformation("Removed {Inf} from the driver store", installed.PublishedInf);
            }
            else
            {
                log.LogError("Could not remove {Inf} from the driver store (error {Error})", installed.PublishedInf, Marshal.GetLastPInvokeError());
                code = 1;
            }
        }

        string record = DisplayDriver.RecordDirectory(dataDir);
        if (Directory.Exists(record))
        {
            Directory.Delete(record, recursive: true);
        }

        if (code == 0)
        {
            log.LogInformation("The virtual display driver is not installed");
        }

        return code;
    }

    /// <summary>Stages a package in the driver store, its publisher trusted meanwhile; returns the <c>oemNN.inf</c> it became.</summary>
    internal static string StageTrusted(string inf, string catalog, ILogger log)
    {
        using X509Certificate2 publisher = Publisher(catalog);
        bool added = Trust(publisher, log);
        try
        {
            return Stage(inf);
        }
        finally
        {
            if (added)
            {
                Distrust(publisher.Thumbprint, log);
            }
        }
    }

    /// <summary>
    /// The root-enumerated device a driver runs on, answering to <paramref name="hardwareId"/>. Returns its instance id.
    /// </summary>
    internal static unsafe string CreateDevice(string hardwareId)
    {
        nint set = DeviceSetup.SetupDiCreateDeviceInfoList(DeviceSetup.DisplayClass, 0);
        if (set == DeviceSetup.InvalidHandle)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "No device list could be made");
        }

        try
        {
            var data = new DeviceSetup.SP_DEVINFO_DATA { cbSize = (uint)sizeof(DeviceSetup.SP_DEVINFO_DATA) };
            if (!DeviceSetup.SetupDiCreateDeviceInfo(set, "DISPLAY", DeviceSetup.DisplayClass, DeviceDescription, 0, DeviceSetup.DICD_GENERATE_ID, ref data))
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "The device could not be described");
            }

            string ids = hardwareId + "\0\0";
            fixed (char* idChars = ids)
            {
                if (!DeviceSetup.SetupDiSetDeviceRegistryProperty(set, ref data, DeviceSetup.SPDRP_HARDWAREID, (byte*)idChars, (uint)(ids.Length * sizeof(char)))
                    || !DeviceSetup.SetupDiCallClassInstaller(DeviceSetup.DIF_REGISTERDEVICE, set, ref data))
                {
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "The device could not be registered");
                }
            }

            char* id = stackalloc char[400];
            int cr = DeviceSetup.CM_Get_Device_ID(data.DevInst, id, 400, 0);
            return cr == DeviceSetup.CR_SUCCESS
                ? new string(id)
                : throw new System.ComponentModel.Win32Exception($"The new device has no instance id (CONFIGRET {cr})");
        }
        finally
        {
            DeviceSetup.SetupDiDestroyDeviceInfoList(set);
        }
    }

    /// <summary>Installs the staged package on the device, and makes sure the device is on.</summary>
    private static void InstallOn(string device, string hardwareId, string publishedInf, ILogger log)
    {
        if (!DeviceSetup.UpdateDriverForPlugAndPlayDevices(0, hardwareId, publishedInf, DeviceSetup.INSTALLFLAG_FORCE | DeviceSetup.INSTALLFLAG_NONINTERACTIVE, out bool reboot))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), $"The driver could not be installed on {device}");
        }

        if (reboot)
        {
            log.LogWarning("Windows says the driver needs a restart to take effect");
        }

        if (Locate(device) is { } node
            && !(DeviceSetup.CM_Get_DevNode_Status(out uint status, out _, node, 0) == DeviceSetup.CR_SUCCESS && (status & DeviceSetup.DN_STARTED) != 0))
        {
            int cr = DeviceSetup.CM_Enable_DevNode(node, 0);
            if (cr != DeviceSetup.CR_SUCCESS)
            {
                log.LogWarning("The device {Device} is not running and could not be switched on (CONFIGRET {Code})", device, cr);
            }
        }
    }

    internal static uint? Locate(string device) =>
        DeviceSetup.CM_Locate_DevNode(out uint node, device, DeviceSetup.CM_LOCATE_DEVNODE_NORMAL) == DeviceSetup.CR_SUCCESS ? node : null;

    /// <summary>Removes the device node entirely: no disabled entry, no ghost left in Device Manager.</summary>
    internal static unsafe bool RemoveDevice(string device)
    {
        nint set = DeviceSetup.SetupDiCreateDeviceInfoList(DeviceSetup.DisplayClass, 0);
        if (set == DeviceSetup.InvalidHandle)
        {
            return false;
        }

        try
        {
            var data = new DeviceSetup.SP_DEVINFO_DATA { cbSize = (uint)sizeof(DeviceSetup.SP_DEVINFO_DATA) };
            return DeviceSetup.SetupDiOpenDeviceInfo(set, device, 0, 0, ref data)
                && DeviceSetup.SetupDiCallClassInstaller(DeviceSetup.DIF_REMOVE, set, ref data);
        }
        finally
        {
            DeviceSetup.SetupDiDestroyDeviceInfoList(set);
        }
    }

    /// <summary>
    /// SYSTEM and administrators may change it; everybody else may read it -- the settings page reads the install record
    /// from here. Anybody able to write it could make the uninstall take away a driver of somebody else's.
    /// </summary>
    private static void CreateRecordDirectory(string path)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags both = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, both, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, both, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, both, PropagationFlags.None, AccessControlType.Allow));
        DirectoryInfo directory = Directory.CreateDirectory(path);
        directory.SetAccessControl(security);
    }

    /// <summary>The certificate that signed the catalog: the one in it that may sign code and is not an authority.</summary>
    private static X509Certificate2 Publisher(string catalog)
    {
        var all = new X509Certificate2Collection();
#pragma warning disable SYSLIB0057 // a catalog is PKCS #7 signed data; the loader that replaces this reads no such thing
        all.Import(File.ReadAllBytes(catalog));
#pragma warning restore SYSLIB0057
        X509Certificate2? signer = all.FirstOrDefault(c =>
            c.Extensions.OfType<X509EnhancedKeyUsageExtension>().Any(e => e.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>().Any(o => o.Value == "1.3.6.1.5.5.7.3.3"))
            && !c.Extensions.OfType<X509BasicConstraintsExtension>().Any(b => b.CertificateAuthority));
        foreach (X509Certificate2 other in all)
        {
            if (!ReferenceEquals(other, signer))
            {
                other.Dispose();
            }
        }

        return signer ?? throw new System.Security.Cryptography.CryptographicException($"{catalog} names no code-signing certificate.");
    }

    /// <summary>Adds the publisher to Trusted Publishers; false when it was there already (and so is not ours to take away).</summary>
    private static bool Trust(X509Certificate2 publisher, ILogger log)
    {
        using var store = new X509Store(StoreName.TrustedPublisher, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        if (store.Certificates.Find(X509FindType.FindByThumbprint, publisher.Thumbprint, validOnly: false).Count > 0)
        {
            return false;
        }

        store.Add(publisher);
        log.LogInformation("Trusting {Subject} ({Thumbprint}) while the driver is staged", publisher.Subject, publisher.Thumbprint);
        return true;
    }

    private static void Distrust(string thumbprint, ILogger log)
    {
        using var store = new X509Store(StoreName.TrustedPublisher, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        foreach (X509Certificate2 certificate in store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false))
        {
            store.Remove(certificate);
            log.LogInformation("No longer trusting {Subject} ({Thumbprint})", certificate.Subject, thumbprint);
        }
    }

    /// <summary>Imports the package into the driver store and returns the <c>oemNN.inf</c> it became.</summary>
    private static unsafe string Stage(string inf)
    {
        char* name = stackalloc char[260];
        if (!DeviceSetup.SetupCopyOEMInf(inf, null, DeviceSetup.SPOST_PATH, 0, name, 260, out _, 0))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), $"The driver package {inf} could not be added to the driver store");
        }

        return Path.GetFileName(new string(name));
    }
}
