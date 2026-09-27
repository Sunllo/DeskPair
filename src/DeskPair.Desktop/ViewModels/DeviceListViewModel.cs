using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskPair.Core.Config;
using DeskPair.Core.Transport;
using DeskPair.Desktop.Localization;
using DeskPair.Desktop.Services;

namespace DeskPair.Desktop.ViewModels;

/// <summary>
/// The saved device list, sorted into the groups the user made. Whether each device can be reached is asked in
/// the background while the page is on screen: peer ids of the rendezvous server, addresses by a short probe.
/// </summary>
public partial class DeviceListViewModel : ObservableObject
{
    /// <summary>How often the list re-checks who is reachable while it is on screen.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    private readonly TimeProvider _time;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<Dictionary<string, PeerOnlineState>>> _presence;
    private readonly Action<Action> _post;
    private readonly string? _path;
    private CancellationTokenSource? _polling;
    private DeviceBook _book;

    /// <summary><paramref name="path"/> is where the book is read and written; null means the user's own file.</summary>
    public DeviceListViewModel(
        DeviceBook? book = null,
        Func<IReadOnlyList<string>, CancellationToken, Task<Dictionary<string, PeerOnlineState>>>? presence = null,
        Action<Action>? post = null,
        TimeProvider? time = null,
        string? path = null)
    {
        _path = path;
        _book = book ?? DeviceBook.Load(path);
        _time = time ?? TimeProvider.System;
        _post = post ?? (action => Avalonia.Threading.Dispatcher.UIThread.Post(action));
        // The same directory the sessions use: presence goes through the rendezvous server, so an empty
        // address here means every device shows as offline for the same reason nothing could be dialled.
        _presence = presence ?? (async (targets, ct) =>
            await new PeerPresence(
                App.Config.ToPeerSettings(App.Version, null, await App.Directory.GetAsync(App.Config.PortalServer, ct)),
                _time).QueryAsync(targets, ct));
        Rebuild();
    }

    /// <summary>The groups as they are shown: each holds the devices that matched the search.</summary>
    public ObservableCollection<DeviceGroupViewModel> Groups { get; } = [];

    /// <summary>Every group that exists, for the picker in the device editor.</summary>
    public ObservableCollection<string> KnownGroups { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDevices))]
    public partial string Search { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsEditorOpen { get; set; }

    /// <summary>The target the editor started from; empty while adding, set while editing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeviceEditorTitle))]
    public partial string EditingTarget { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DraftTarget { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DraftAlias { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DraftGroup { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DraftNote { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsGroupEditorOpen { get; set; }

    /// <summary>The group the editor started from; empty while adding, set while renaming.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GroupEditorTitle))]
    public partial string EditingGroup { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DraftGroupName { get; set; } = string.Empty;

    /// <summary>
    /// A notice goes to the one place that shows messages and takes them away again. This screen used to
    /// keep its own line of text, set when something happened and cleared when the next thing happened --
    /// which, if nothing else happens, is never.
    /// </summary>
    partial void OnNoticeChanged(string value) => Services.Toasts.Current.Show(value, true);

    [ObservableProperty]
    public partial string Notice { get; set; } = string.Empty;

    public string DeviceEditorTitle => Strings.Get(EditingTarget.Length > 0 ? "devices.editDevice" : "devices.add");

    public string GroupEditorTitle => Strings.Get(EditingGroup.Length > 0 ? "devices.renameGroup" : "devices.addGroup");

    public bool HasDevices => Groups.Count > 0;

    /// <summary>
    /// Whether this install belongs to an account, which is what the list belongs to.
    ///
    /// The list is the account's: it lives in the portal's database, folders and all, and syncs to
    /// whatever else the person signs in on. Without an account there is nowhere for it to live beyond
    /// this one machine, so rather than quietly keeping a local copy that will surprise somebody later,
    /// the page says what it needs. The phones say the same thing on the same screen.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUseList))]
    public partial bool IsSignedIn { get; set; }

    /// <summary>What the page shows the list for, as against showing the invitation to sign in.</summary>
    public bool CanUseList => IsSignedIn;

    /// <summary>Re-reads the book and starts polling; called when the page becomes visible.</summary>
    public void Activate()
    {
        if (_polling is not null)
        {
            return;
        }

        // A session that ran while this page was hidden may have updated "last connected".
        _book = DeviceBook.Load(_path);
        Rebuild();

        _polling = new CancellationTokenSource();
        _ = PollAsync(_polling.Token);
    }

    /// <summary>Stops polling; called when the page is left, so a hidden list costs nothing.</summary>
    public void Deactivate()
    {
        _polling?.Cancel();
        _polling?.Dispose();
        _polling = null;
    }

    [RelayCommand]
    private void BeginAdd()
    {
        EditingTarget = string.Empty;
        DraftTarget = string.Empty;
        DraftAlias = string.Empty;
        DraftGroup = string.Empty;
        DraftNote = string.Empty;
        Notice = string.Empty;
        IsEditorOpen = true;
    }

    [RelayCommand]
    private void BeginEdit(DeviceRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        EditingTarget = row.Target;
        DraftTarget = row.Target;
        DraftAlias = row.Alias;
        DraftGroup = row.Group;
        DraftNote = row.Note;
        Notice = string.Empty;
        IsEditorOpen = true;
    }

    /// <summary>Fills the group box from the list of groups that already exist.</summary>
    [RelayCommand]
    private void PickGroup(string? name) => DraftGroup = name ?? string.Empty;

    [RelayCommand]
    private void BeginAddGroup()
    {
        EditingGroup = string.Empty;
        DraftGroupName = string.Empty;
        Notice = string.Empty;
        IsGroupEditorOpen = true;
    }

    [RelayCommand]
    private void BeginRenameGroup(DeviceGroupViewModel? group)
    {
        if (group is null || !group.CanEdit)
        {
            return;
        }

        EditingGroup = group.Name;
        DraftGroupName = group.Name;
        Notice = string.Empty;
        IsGroupEditorOpen = true;
    }

    [RelayCommand]
    private void SaveGroup()
    {
        string name = DraftGroupName.Trim();
        if (name.Length == 0)
        {
            Notice = Strings.Get("devices.groupRequired");
            return;
        }

        Store(EditingGroup.Length > 0 ? _book.RenameGroup(EditingGroup, name) : _book.WithGroup(name));
        IsGroupEditorOpen = false;
    }

    [RelayCommand]
    private void CancelGroupEdit()
    {
        IsGroupEditorOpen = false;
        Notice = string.Empty;
    }

    /// <summary>Folds a group away, or opens it. The list is already showing it, so nothing is rebuilt.</summary>
    [RelayCommand]
    private void ToggleGroup(DeviceGroupViewModel? group)
    {
        if (group is null)
        {
            return;
        }

        group.IsExpanded = !group.IsExpanded;
        Persist(_book.WithCollapsed(Key(group), !group.IsExpanded));
    }

    /// <summary>Removing a group keeps its devices; they move to "ungrouped".</summary>
    [RelayCommand]
    private void DeleteGroup(DeviceGroupViewModel? group)
    {
        if (group is { CanEdit: true })
        {
            Store(_book.WithoutGroup(group.Name));
        }
    }

    /// <summary>
    /// Moves a device into a group, which is what dropping it on one means. Dropping it back where it already
    /// is costs nothing; dropping it on "ungrouped" clears its group.
    /// </summary>
    public void MoveToGroup(string target, DeviceGroupViewModel? group)
    {
        SavedDevice? device = _book.Devices.FirstOrDefault(d => string.Equals(d.Target, target, StringComparison.OrdinalIgnoreCase));
        if (device is null || group is null)
        {
            return;
        }

        string name = group.CanEdit ? group.Name : string.Empty;
        if (string.Equals(device.Group, name, StringComparison.CurrentCultureIgnoreCase))
        {
            return;
        }

        // Dropping into a folded group opens it, or the device would look as though it had gone nowhere.
        Store(_book.With(device with { Group = name }).WithCollapsed(name, false));
    }

    /// <summary>True while a device could usefully be dropped on this group.</summary>
    public bool CanDropOn(string target, DeviceGroupViewModel? group)
    {
        SavedDevice? device = _book.Devices.FirstOrDefault(d => string.Equals(d.Target, target, StringComparison.OrdinalIgnoreCase));
        return device is not null
            && group is not null
            && !string.Equals(device.Group, group.CanEdit ? group.Name : string.Empty, StringComparison.CurrentCultureIgnoreCase);
    }

    [RelayCommand]
    private void SaveDevice()
    {
        string target = DraftTarget.Trim();
        if (target.Length == 0)
        {
            Notice = Strings.Get("devices.targetRequired");
            return;
        }

        // Retargeting an existing row moves it, rather than leaving the old target behind as a second device.
        DeviceBook book = EditingTarget.Length > 0 && !string.Equals(EditingTarget, target, StringComparison.OrdinalIgnoreCase)
            ? _book.Without(EditingTarget)
            : _book;
        DateTimeOffset lastConnected = book.Devices
            .FirstOrDefault(d => string.Equals(d.Target, target, StringComparison.OrdinalIgnoreCase))?.LastConnected ?? default;
        Store(book
            .WithGroup(DraftGroup.Trim())
            .With(new SavedDevice
            {
                Target = target,
                Alias = DraftAlias.Trim(),
                Group = DraftGroup.Trim(),
                Note = DraftNote.Trim(),
                LastConnected = lastConnected,
            }));
        IsEditorOpen = false;
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditorOpen = false;
        Notice = string.Empty;
    }

    [RelayCommand]
    private void DeleteDevice(DeviceRowViewModel? row)
    {
        if (row is not null)
        {
            Store(_book.Without(row.Target));
        }
    }

    [RelayCommand]
    private void Connect(DeviceRowViewModel? row)
    {
        if (row is not null)
        {
            App.OpenRemoteSession(row.Target);
        }
    }

    [RelayCommand]
    private void OpenFiles(DeviceRowViewModel? row)
    {
        if (row is not null)
        {
            App.OpenFileTransfer(row.Target);
        }
    }

    [RelayCommand]
    private void OpenTerminal(DeviceRowViewModel? row)
    {
        if (row is not null)
        {
            App.OpenTerminal(row.Target);
        }
    }

    partial void OnSearchChanged(string value) => Rebuild();

    private void Store(DeviceBook book)
    {
        Persist(book);
        Rebuild();
        Refresh();
    }

    /// <summary>Writes the book without rebuilding the list, for a change the list is already showing.</summary>
    private void Persist(DeviceBook book)
    {
        _book = book;
        try
        {
            _book.Save(_path);
            Notice = string.Empty;

            // Tell the account about it shortly. Null when this install is not linked, or when the list is
            // a test's own file rather than the user's.
            if (_path is null)
            {
                App.BookSync?.Nudge();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Notice = Strings.Format("settings.saveFailed", e.Message);
        }
    }

    /// <summary>Rebuilds the grouped view from the book, keeping the online state already known for each target.</summary>
    private void Rebuild()
    {
        Dictionary<string, PeerOnlineState> known = Groups
            .SelectMany(g => g.Devices)
            .ToDictionary(d => d.Target, d => d.State, StringComparer.OrdinalIgnoreCase);
        string search = Search.Trim();
        string ungrouped = Strings.Get("devices.ungrouped");
        Groups.Clear();
        KnownGroups.Clear();
        foreach (string name in _book.Groups)
        {
            KnownGroups.Add(name);
        }

        List<SavedDevice> matching = _book.Devices.Where(d => Matches(d, search)).ToList();

        // A group the user made shows even while it is empty, or it would look as if the button did nothing.
        // While searching, only groups with a match are worth the space.
        IEnumerable<string> names = search.Length == 0
            ? _book.Groups.Append(ungrouped)
            : matching.Select(d => d.Group.Length > 0 ? d.Group : ungrouped).Distinct(StringComparer.CurrentCultureIgnoreCase);

        foreach (string name in names.Distinct(StringComparer.CurrentCultureIgnoreCase))
        {
            bool isUngrouped = string.Equals(name, ungrouped, StringComparison.CurrentCultureIgnoreCase);
            var rows = new DeviceGroupViewModel(name, canEdit: !isUngrouped)
            {
                IsExpanded = !_book.IsCollapsed(isUngrouped ? string.Empty : name),
            };
            IEnumerable<SavedDevice> members = matching.Where(d => isUngrouped
                ? d.Group.Length == 0
                : string.Equals(d.Group, name, StringComparison.CurrentCultureIgnoreCase));
            foreach (SavedDevice device in members.OrderBy(d => d.Alias.Length > 0 ? d.Alias : d.Target, StringComparer.CurrentCulture))
            {
                rows.Devices.Add(new DeviceRowViewModel(device)
                {
                    State = known.TryGetValue(device.Target, out PeerOnlineState state) ? state : PeerOnlineState.Unknown,
                });
            }

            // "Ungrouped" is not a group the user made, so it appears only when something is in it.
            if (rows.Devices.Count > 0 || rows.CanEdit)
            {
                Groups.Add(rows);
            }
        }

        OnPropertyChanged(nameof(HasDevices));
    }

    /// <summary>What a group is stored under: its name, or the empty string for "ungrouped".</summary>
    private static string Key(DeviceGroupViewModel group) => group.CanEdit ? group.Name : string.Empty;

    private static bool Matches(SavedDevice device, string search) =>
        search.Length == 0
        || device.Target.Contains(search, StringComparison.OrdinalIgnoreCase)
        || device.Alias.Contains(search, StringComparison.CurrentCultureIgnoreCase)
        || device.Group.Contains(search, StringComparison.CurrentCultureIgnoreCase)
        || device.Note.Contains(search, StringComparison.CurrentCultureIgnoreCase);

    private void Refresh()
    {
        if (_polling is { IsCancellationRequested: false } source)
        {
            _ = QueryOnceAsync(source.Token);
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await QueryOnceAsync(ct).ConfigureAwait(false);
            try
            {
                await Task.Delay(PollInterval, _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task QueryOnceAsync(CancellationToken ct)
    {
        List<string> targets = _book.Devices.Select(d => d.Target).ToList();
        if (targets.Count == 0)
        {
            return;
        }

        Dictionary<string, PeerOnlineState> states;
        try
        {
            states = await _presence(targets, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return;
        }

        _post(() =>
        {
            foreach (DeviceRowViewModel row in Groups.SelectMany(g => g.Devices))
            {
                if (states.TryGetValue(row.Target, out PeerOnlineState state))
                {
                    row.State = state;
                }
            }
        });
    }
}

/// <summary>One named group with the devices in it.</summary>
public sealed partial class DeviceGroupViewModel(string name, bool canEdit) : ObservableObject
{
    public string Name { get; } = name;

    /// <summary>False for "ungrouped", which is where devices with no group land rather than a real group.</summary>
    public bool CanEdit { get; } = canEdit;

    public ObservableCollection<DeviceRowViewModel> Devices { get; } = [];

    public bool IsEmpty => Devices.Count == 0;

    /// <summary>A device is being dragged over this group, so it should look like it will accept the drop.</summary>
    [ObservableProperty]
    public partial bool IsDropTarget { get; set; }

    /// <summary>False while the group is folded away; its devices are hidden but it still takes drops.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleGlyph))]
    [NotifyPropertyChangedFor(nameof(ToggleHint))]
    public partial bool IsExpanded { get; set; } = true;

    public string ToggleGlyph => IsExpanded ? "\u25be" : "\u25b8";

    public string ToggleHint => Strings.Get(IsExpanded ? "devices.collapse" : "devices.expand");

    /// <summary>How many devices are in here, worth saying while the group is folded.</summary>
    public string CountText => Strings.Format("devices.groupCount", Devices.Count);
}

/// <summary>One saved device as the list shows it: what to call it, and whether it answers right now.</summary>
public partial class DeviceRowViewModel(SavedDevice device) : ObservableObject
{
    public string Target { get; } = device.Target;

    public string Alias { get; } = device.Alias;

    public string Group { get; } = device.Group;

    public string Note { get; } = device.Note;

    public string Title { get; } = device.Alias.Length > 0 ? device.Alias : Format(device.Target);

    public string Subtitle { get; } = device.Alias.Length > 0 ? Format(device.Target) : string.Empty;

    /// <summary>What the device reported the last time it was reached, for the logo. Empty shows none.</summary>
    public string Platform { get; } = device.Platform;

    public string LastConnectedText { get; } = device.LastConnected == default
        ? Strings.Get("devices.never")
        : Strings.Format("devices.lastConnected", device.LastConnected.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    [NotifyPropertyChangedFor(nameof(StateColour))]
    [NotifyPropertyChangedFor(nameof(CanConnect))]
    public partial PeerOnlineState State { get; set; }

    /// <summary>
    /// Offline is the one answer that disables the buttons. Unknown does not: "the server did not answer"
    /// is not "the machine is off", and a direct address on a LAN with no server is unknown for ever.
    /// </summary>
    public bool CanConnect => State != PeerOnlineState.Offline;

    /// <summary>True while this row is the one being dragged, so it can fade where it came from.</summary>
    [ObservableProperty]
    public partial bool IsDragging { get; set; }

    public string StateText => State switch
    {
        PeerOnlineState.Online => Strings.Get("devices.online"),
        PeerOnlineState.Offline => Strings.Get("devices.offline"),
        _ => Strings.Get("devices.unknown"),
    };

    public string StateColour => State switch
    {
        PeerOnlineState.Online => "#4CAF50",
        PeerOnlineState.Offline => "#6B7280",
        _ => "#C9A227",
    };

    /// <summary>A nine-digit id reads as three groups, the way it is shown everywhere else.</summary>
    private static string Format(string target) =>
        target.Length == 9 && target.All(char.IsAsciiDigit) ? $"{target[..3]} {target[3..6]} {target[6..]}" : target;
}
