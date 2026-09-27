using System.Runtime.Versioning;
using System.Xml.Linq;
using Microsoft.Win32;
using DeskPair.Platform.Abstractions.Capture;

namespace DeskPair.Platform.Windows.Capture;

/// <summary>
/// The indirect display driver DeskPair adds displays with: VDD (github.com/VirtualDrivers/Virtual-Display-Driver,
/// MIT), shipped exactly as its project released it, signed by the SignPath Foundation. Nothing here builds or signs
/// it; see <c>native/vdd/README.md</c>.
///
/// How it is driven. The driver reads <c>vdd_settings.xml</c> -- how many monitors, which sizes -- when its device
/// starts, and only then; its pipe's RELOAD_DRIVER re-initialises the adapter without reading the file again, so
/// the pipe is not used. The install makes one root-enumerated device for it, the way its own tools (nefcon,
/// devcon) do, and leaves it disabled; a change is the device disabled, the file written, the device enabled:
/// every virtual monitor goes, and as many come back as the file now says.
///
/// Where the file lives is the driver's own choice, read from <c>HKLM\SOFTWARE\MikeTheTech\VirtualDisplayDriver</c>
/// (VDDPATH), defaulting to <c>C:\VirtualDisplayDriver</c> -- a folder any user may create and fill. DeskPair points
/// it at a folder under its data directory that only SYSTEM and administrators may write.
/// </summary>
[SupportedOSPlatform("windows")]
public static class VirtualDisplayDriver
{
    /// <summary>The hardware id the driver's INF answers to without the Root\ prefix: the one a software device takes.</summary>
    public const string HardwareId = "MttVDD";

    internal const string RegistryKey = @"SOFTWARE\MikeTheTech\VirtualDisplayDriver";
    internal const string PathValue = "VDDPATH";
    internal const string SettingsFile = "vdd_settings.xml";
    private const string MarkerFile = "installed";

    /// <summary>The files a package directory must hold.</summary>
    internal static readonly string[] PackageFiles = ["MttVDD.inf", "MttVDD.dll", "mttvdd.cat"];

    /// <summary>
    /// Sizes every added display offers from the start, so the usual requests never need a new device: 16:9,
    /// 16:10, 4:3, 5:4 and the two ultrawides, at 60 Hz.
    /// </summary>
    internal static readonly (int Width, int Height)[] CommonSizes =
    [
        (1920, 1080), (1280, 720), (1366, 768), (1600, 900), (2560, 1440), (3840, 2160),
        (1280, 800), (1440, 900), (1680, 1050), (1920, 1200), (2560, 1600),
        (1024, 768), (1600, 1200), (1280, 1024), (2560, 1080), (3440, 1440),
    ];

    /// <summary>Where the driver's settings live on this machine.</summary>
    public static string ConfigDirectory(string dataDir) => Path.Combine(dataDir, "vdd");

    /// <summary>Where a build carries the driver package: a <c>vdd</c> folder beside the program.</summary>
    public static string PackageDirectory => Path.Combine(AppContext.BaseDirectory, "vdd");

    /// <summary>What an install recorded: the driver store's name for the package, and the device it made for it.</summary>
    public sealed record Installation(string PublishedInf, string? Device);

    /// <summary>The installation recorded under <paramref name="dataDir"/>, or null when there is none.</summary>
    public static Installation? Installed(string dataDir)
    {
        string marker = Path.Combine(ConfigDirectory(dataDir), MarkerFile);
        if (!File.Exists(marker))
        {
            return null;
        }

        Dictionary<string, string> values = File.ReadAllLines(marker)
            .Select(l => l.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0].Trim(), p => p[1].Trim(), StringComparer.Ordinal);
        return values.TryGetValue("inf", out string? inf) && inf.Length > 0
            ? new Installation(inf, values.TryGetValue("device", out string? d) && d.Length > 0 ? d : null)
            : null;
    }

    internal static void RecordInstallation(string dataDir, Installation installation) =>
        File.WriteAllLines(
            Path.Combine(ConfigDirectory(dataDir), MarkerFile),
            [$"inf={installation.PublishedInf}", $"device={installation.Device ?? string.Empty}"]);

    /// <summary>Where the driver on this machine reads its settings from, when anything has said.</summary>
    internal static string? ConfiguredPath()
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(RegistryKey);
        return key?.GetValue(PathValue) as string;
    }

    /// <summary>
    /// The settings file for <paramref name="count"/> monitors (never fewer than one: the driver reads zero as one,
    /// so no monitors is no device). <paramref name="preferred"/> goes first, which the driver reports as the
    /// monitor's preferred mode; sizes taught since come next, then <see cref="CommonSizes"/>.
    /// </summary>
    internal static XDocument Settings(int count, DisplayMode? preferred, IEnumerable<DisplayMode> taught)
    {
        var sizes = new List<(int Width, int Height)>();
        void Add(int width, int height)
        {
            if (width > 0 && height > 0 && !sizes.Contains((width, height)))
            {
                sizes.Add((width, height));
            }
        }

        if (preferred is { } p)
        {
            Add(p.Width, p.Height);
        }

        foreach (DisplayMode mode in taught)
        {
            Add(mode.Width, mode.Height);
        }

        foreach ((int width, int height) in CommonSizes)
        {
            Add(width, height);
        }

        // Every option spelled out rather than left to the driver's defaults: one it read from a registry value
        // somebody else set would otherwise decide how these displays behave. Logging stays off -- the driver
        // would write its log into this folder, as LocalService, without limit.
        return new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XComment(" Written by DeskPair each time a display is added, removed or taught a size; edits do not survive. "),
            new XElement("vdd_settings",
                new XElement("monitors", new XElement("count", Math.Max(1, count))),
                new XElement("gpu", new XElement("friendlyname", "default")),
                new XElement("resolutions", sizes.Select(s =>
                    new XElement("resolution",
                        new XElement("width", s.Width),
                        new XElement("height", s.Height),
                        new XElement("refresh_rate", 60)))),
                new XElement("options",
                    new XElement("CustomEdid", "false"),
                    new XElement("PreventSpoof", "false"),
                    new XElement("EdidCeaOverride", "false"),
                    new XElement("HardwareCursor", "true"),
                    new XElement("SDR10bit", "false"),
                    new XElement("HDRPlus", "false"),
                    new XElement("logging", "false"),
                    new XElement("debuglogging", "false"))));
    }

    internal static void WriteSettings(string dataDir, int count, DisplayMode? preferred, IEnumerable<DisplayMode> taught) =>
        Settings(count, preferred, taught).Save(Path.Combine(ConfigDirectory(dataDir), SettingsFile));
}
