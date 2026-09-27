using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace DeskPair.Desktop.Services;

/// <summary>
/// A computer the user saved, by id or by address. Groups are plain names so the list can be sorted into
/// whatever the user needs (site, customer, room); an empty group means "ungrouped".
/// </summary>
public sealed record SavedDevice
{
    /// <summary>What to connect to: a peer id, or an address like 192.168.1.5[:21118].</summary>
    public string Target { get => field; init => field = value ?? string.Empty; } = string.Empty;

    /// <summary>The name the user gave it; empty falls back to the name the desk reports.</summary>
    public string Alias { get => field; init => field = value ?? string.Empty; } = string.Empty;

    public string Group { get => field; init => field = value ?? string.Empty; } = string.Empty;

    public string Note { get => field; init => field = value ?? string.Empty; } = string.Empty;

    public DateTimeOffset LastConnected { get; init; }

    /// <summary>
    /// What it was running the last time it was reached ("Windows", "Ubuntu 24.04", ...), for the logo in the
    /// list. Empty until it has been connected to once, and never typed by the user: the device says it.
    /// </summary>
    public string Platform { get => field; init => field = value ?? string.Empty; } = string.Empty;
}

/// <summary>The saved device list, kept beside the other controller settings.</summary>
public sealed record DeviceBook
{
    [JsonConstructor]
    public DeviceBook()
    {
    }

    public List<SavedDevice> Devices { get => field; init => field = value ?? []; } = [];

    /// <summary>
    /// Group names the user made. A group is kept here as well as on its devices, so one can be created before
    /// there is anything in it and survives the last device leaving it.
    /// </summary>
    public List<string> GroupNames { get => field; init => field = value ?? []; } = [];

    /// <summary>
    /// Groups the user has folded away. Kept here so a list stays how it was left; the empty string is the
    /// "ungrouped" section, whose shown name is a translation and so is no good as a key.
    /// </summary>
    public List<string> CollapsedGroups { get => field; init => field = value ?? []; } = [];

    /// <summary>
    /// The portal revision this book has been brought up to date with. 0 means it has never synced.
    /// </summary>
    public long SyncRev { get; init; }

    /// <summary>
    /// The book as the portal last confirmed it, which is what makes a sync a diff rather than a guess.
    ///
    /// Keeping a whole second copy rather than a dirty flag on each entry is a deliberate trade of a little
    /// disk for a great deal of correctness: deletions fall out of the comparison for free (in here, not in
    /// <see cref="Devices"/>), nothing has to remember to mark an edit, and <see cref="SavedDevice"/> needed
    /// no new fields at all. A book of five hundred machines costs perhaps a hundred kilobytes.
    ///
    /// Empty while this installation is not linked to an account, so an unlinked user pays nothing.
    /// </summary>
    public List<SavedDevice> Synced { get => field; init => field = value ?? []; } = [];

    /// <summary>Group names as the portal last confirmed them; the counterpart of <see cref="Synced"/>.</summary>
    public List<string> SyncedGroupNames { get => field; init => field = value ?? []; } = [];

    public static string DefaultPath => Path.Combine(Path.GetDirectoryName(DesktopConfig.DefaultPath)!, "devices.json");

    public static DeviceBook Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                return FromJson(File.ReadAllText(path));
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
        }

        return new DeviceBook();
    }

    /// <summary>Reads over the defaults, so a file written by an older build keeps working.</summary>
    public static DeviceBook FromJson(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject stored)
        {
            return new DeviceBook();
        }

        JsonObject merged = JsonSerializer.SerializeToNode(new DeviceBook(), DeviceBookJson.Default.DeviceBook)!.AsObject();
        foreach (KeyValuePair<string, JsonNode?> item in stored)
        {
            merged[item.Key] = item.Value?.DeepClone();
        }

        return merged.Deserialize(DeviceBookJson.Default.DeviceBook) ?? new DeviceBook();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, DeviceBookJson.Default.DeviceBook));
    }

    /// <summary>Adds a device, or updates the one with the same target; the target is the identity here.</summary>
    public DeviceBook With(SavedDevice device)
    {
        string target = device.Target.Trim();
        if (target.Length == 0)
        {
            return this;
        }

        var list = Devices.Where(d => !Same(d.Target, target)).ToList();
        list.Add(device with { Target = target });
        return this with { Devices = list };
    }

    public DeviceBook Without(string target) => this with { Devices = Devices.Where(d => !Same(d.Target, target)).ToList() };

    public bool IsCollapsed(string name) => CollapsedGroups.Any(g => Same(g, name));

    /// <summary>Folds a group away, or opens it again.</summary>
    public DeviceBook WithCollapsed(string name, bool collapsed)
    {
        string group = name.Trim();
        if (IsCollapsed(group) == collapsed)
        {
            return this;
        }

        return this with
        {
            CollapsedGroups = collapsed
                ? [.. CollapsedGroups, group]
                : CollapsedGroups.Where(g => !Same(g, group)).ToList(),
        };
    }

    /// <summary>Creates an empty group; a name that already exists (in any casing) changes nothing.</summary>
    public DeviceBook WithGroup(string name)
    {
        string group = name.Trim();
        if (group.Length == 0 || Groups.Any(g => Same(g, group)))
        {
            return this;
        }

        return this with { GroupNames = [.. GroupNames, group] };
    }

    /// <summary>Removes a group; its devices are not deleted, they become ungrouped.</summary>
    public DeviceBook WithoutGroup(string name)
    {
        string group = name.Trim();
        return this with
        {
            GroupNames = GroupNames.Where(g => !Same(g, group)).ToList(),
            CollapsedGroups = CollapsedGroups.Where(g => !Same(g, group)).ToList(),
            Devices = Devices.Select(d => Same(d.Group, group) ? d with { Group = string.Empty } : d).ToList(),
        };
    }

    /// <summary>Renames a group, taking its devices with it. Renaming onto an existing name merges the two.</summary>
    public DeviceBook RenameGroup(string from, string to)
    {
        string old = from.Trim();
        string renamed = to.Trim();
        if (old.Length == 0 || renamed.Length == 0 || Same(old, renamed))
        {
            return this;
        }

        List<string> names = GroupNames.Where(g => !Same(g, old) && !Same(g, renamed)).ToList();
        names.Add(renamed);
        List<string> collapsed = CollapsedGroups.Where(g => !Same(g, old) && !Same(g, renamed)).ToList();
        if (IsCollapsed(old))
        {
            collapsed.Add(renamed);
        }

        return this with
        {
            GroupNames = names,
            CollapsedGroups = collapsed,
            Devices = Devices.Select(d => Same(d.Group, old) ? d with { Group = renamed } : d).ToList(),
        };
    }

    /// <summary>Records that a connection to this device succeeded; unsaved devices are left alone.</summary>
    public DeviceBook Touched(string target, DateTimeOffset when, string? platform = null)
    {
        SavedDevice? device = Devices.FirstOrDefault(d => Same(d.Target, target));
        if (device is null)
        {
            return this;
        }

        // A device that reports nothing keeps whatever it last said rather than losing its logo.
        string keep = string.IsNullOrEmpty(platform) ? device.Platform : platform;
        return With(device with { LastConnected = when, Platform = keep });
    }

    /// <summary>
    /// Every group the list should show: the ones the user made, plus any a device names without having been
    /// created first (a book written by an older build, or a group typed straight into the device editor).
    /// </summary>
    [JsonIgnore]
    public IEnumerable<string> Groups => GroupNames
        .Concat(Devices.Select(d => d.Group))
        .Where(g => g.Length > 0)
        .Distinct(StringComparer.CurrentCultureIgnoreCase)
        .Order(StringComparer.CurrentCulture);

    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(DeviceBook))]
internal sealed partial class DeviceBookJson : JsonSerializerContext
{
}
