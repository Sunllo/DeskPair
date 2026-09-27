using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using DeskPair.Platform.Windows.Native;

namespace DeskPair.Platform.Windows.Capture;

/// <summary>
/// Putting <see cref="VirtualDisplayDriver"/> on this machine and taking it off again. Both need administrator
/// rights; neither plugs in a display -- the engine does that when a viewer asks for one.
///
/// Installing stages the package in the driver store, then makes the one root-enumerated device the driver
/// runs on, the way devcon does, and leaves it disabled with the driver installed on it. The package is signed
/// with an ordinary Authenticode certificate (SignPath Foundation's), not by Microsoft, and Windows stages such a
/// package without asking only when its publisher is in the machine's Trusted Publishers -- otherwise it asks, and
/// with nobody to answer (a service, a script) it fails. So the publisher is trusted while the package is staged
/// and no longer: left there, it would make this machine accept any driver SignPath signs for anyone, silently.
/// Installing it on the device afterwards, from the store, does not ask again (measured).
/// </summary>
[SupportedOSPlatform("windows")]
public static class VirtualDisplayDriverInstaller
{
    private const string DeviceDescription = "DeskPair virtual displays";

    public static int Install(string dataDir, string packageDir, ILogger log)
    {
        if (!Environment.IsPrivilegedProcess)
        {
            log.LogError("Installing the virtual display driver needs administrator rights.");
            return 2;
        }

        if (VirtualDisplayDriver.PackageFiles.FirstOrDefault(f => !File.Exists(Path.Combine(packageDir, f))) is { } missing)
        {
            log.LogError("This copy of DeskPair carries no virtual display driver ({File} is not in {Dir}).", missing, packageDir);
            return 2;
        }

        // Somebody's own install of the same driver reads its settings from wherever they pointed it. Taking the
        // pointer over would give their displays DeskPair's settings, and take them away when DeskPair goes.
        string config = VirtualDisplayDriver.ConfigDirectory(dataDir);
        if (VirtualDisplayDriver.ConfiguredPath() is { } existing && !SamePath(existing, config))
        {
            log.LogError("The Virtual Display Driver is already set up on this computer by something else (its settings are in {Path}); DeskPair will not take it over.", existing);
            return 2;
        }

        try
        {
            CreateConfigDirectory(config);
            VirtualDisplayDriver.WriteSettings(dataDir, 1, null, []);
            using (RegistryKey key = Registry.LocalMachine.CreateSubKey(VirtualDisplayDriver.RegistryKey))
            {
                key.SetValue(VirtualDisplayDriver.PathValue, config, RegistryValueKind.String);
            }

            using X509Certificate2 publisher = Publisher(Path.Combine(packageDir, "mttvdd.cat"));
            bool added = Trust(publisher, log);
            string published;
            try
            {
                published = Stage(Path.Combine(packageDir, "MttVDD.inf"));
            }
            finally
            {
                if (added)
                {
                    Distrust(publisher.Thumbprint, log);
                }
            }

            // A second install keeps the device the first one made rather than adding another beside it.
            string device = VirtualDisplayDriver.Installed(dataDir)?.Device is { } kept && Locate(kept) is not null
                ? kept
                : CreateDevice();
            InstallOn(device, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", published), log);

            VirtualDisplayDriver.RecordInstallation(dataDir, new VirtualDisplayDriver.Installation(published, device));
            log.LogInformation("The virtual display driver is installed as {Inf} on {Device}, switched off until a viewer asks for a display; its settings are in {Config}", published, device, config);
            return 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or System.ComponentModel.Win32Exception)
        {
            log.LogError(e, "Could not install the virtual display driver");
            return 1;
        }
    }

    /// <summary>
    /// Takes away the device, the driver package, the settings and the registry pointer. What is already gone is
    /// skipped: the caller asked for it to be gone, and it is.
    /// </summary>
    public static int Uninstall(string dataDir, ILogger log)
    {
        if (!Environment.IsPrivilegedProcess)
        {
            log.LogError("Removing the virtual display driver needs administrator rights.");
            return 2;
        }

        string config = VirtualDisplayDriver.ConfigDirectory(dataDir);
        VirtualDisplayDriver.Installation? installed = VirtualDisplayDriver.Installed(dataDir);
        int code = 0;

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
                int error = Marshal.GetLastPInvokeError();
                log.LogError("Could not remove {Inf} from the driver store (error {Error})", installed.PublishedInf, error);
                code = 1;
            }
        }

        if (VirtualDisplayDriver.ConfiguredPath() is { } path && SamePath(path, config))
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(VirtualDisplayDriver.RegistryKey, writable: true);
            key?.DeleteValue(VirtualDisplayDriver.PathValue, throwOnMissingValue: false);
            if (key is { ValueCount: 0, SubKeyCount: 0 })
            {
                key.Dispose();
                Registry.LocalMachine.DeleteSubKey(VirtualDisplayDriver.RegistryKey, throwOnMissingSubKey: false);

                // The vendor key above it came with the same install; left empty it would be the one thing that stayed.
                string vendor = VirtualDisplayDriver.RegistryKey[..VirtualDisplayDriver.RegistryKey.LastIndexOf('\\')];
                using RegistryKey? parent = Registry.LocalMachine.OpenSubKey(vendor);
                if (parent is { ValueCount: 0, SubKeyCount: 0 })
                {
                    parent.Dispose();
                    Registry.LocalMachine.DeleteSubKey(vendor, throwOnMissingSubKey: false);
                }
            }
        }

        if (Directory.Exists(config))
        {
            Directory.Delete(config, recursive: true);
        }

        if (code == 0)
        {
            log.LogInformation("The virtual display driver is not installed");
        }

        return code;
    }

    /// <summary>
    /// The root-enumerated device the driver runs on. Returns its instance id. It cannot be made disabled: installing
    /// the driver starts it whatever flag it was registered with (measured), so one monitor shows for a moment
    /// before <see cref="InstallOn"/> switches it off.
    /// </summary>
    private static unsafe string CreateDevice()
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

            string ids = VirtualDisplayDriver.HardwareId + "\0\0";
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

    /// <summary>
    /// Installs the staged package on the device, and makes sure it stays off: across restarts as well, so a
    /// machine does not boot with a display nobody asked for.
    /// </summary>
    private static void InstallOn(string device, string publishedInf, ILogger log)
    {
        if (!DeviceSetup.UpdateDriverForPlugAndPlayDevices(0, VirtualDisplayDriver.HardwareId, publishedInf, DeviceSetup.INSTALLFLAG_FORCE | DeviceSetup.INSTALLFLAG_NONINTERACTIVE, out bool reboot))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), $"The driver could not be installed on {device}");
        }

        if (reboot)
        {
            log.LogWarning("Windows says the driver needs a restart to take effect");
        }

        if (Locate(device) is { } node)
        {
            int cr = DeviceSetup.CM_Disable_DevNode(node, DeviceSetup.CM_DISABLE_UI_NOT_OK | DeviceSetup.CM_DISABLE_PERSIST);
            if (cr != DeviceSetup.CR_SUCCESS && DeviceSetup.CM_Get_DevNode_Status(out uint status, out _, node, 0) == DeviceSetup.CR_SUCCESS && (status & DeviceSetup.DN_STARTED) != 0)
            {
                log.LogWarning("The device {Device} is running and could not be switched off (CONFIGRET {Code}); the engine switches it off when it starts", device, cr);
            }
        }
    }

    private static uint? Locate(string device) =>
        DeviceSetup.CM_Locate_DevNode(out uint node, device, DeviceSetup.CM_LOCATE_DEVNODE_NORMAL) == DeviceSetup.CR_SUCCESS ? node : null;

    /// <summary>Removes the device node entirely: no disabled entry, no ghost left in Device Manager.</summary>
    private static unsafe bool RemoveDevice(string device)
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
    /// SYSTEM and administrators may change it; the driver (LocalService) and everybody else may read it -- the
    /// settings page reads the install record from here. Anybody able to write it could decide what this
    /// machine's added displays are.
    /// </summary>
    private static void CreateConfigDirectory(string path)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags both = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, both, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, both, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null), FileSystemRights.ReadAndExecute, both, PropagationFlags.None, AccessControlType.Allow));
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

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}
