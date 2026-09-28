using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskPair.Core.Config;
using System.Net.Http;
using DeskPair.Desktop.Localization;
using DeskPair.Desktop.Services;
using DeskPair.Desktop.ViewModels.Settings;

namespace DeskPair.Desktop.ViewModels;

/// <summary>
/// The settings screen: eight tabs, no Save button. Each tab reports its changes, and this class writes them a
/// moment later so a change is stored without the user having to confirm it. Host settings go to the service
/// over IPC and only when they actually differ, which keeps a keystroke from becoming a round trip.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    /// <summary>How long to wait after the last change before writing, so typing is one save, not ten.</summary>
    public static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);

    private readonly HostLink _host;
    private readonly ISettingsSection[] _sections;
    private readonly Action<Action> _post;
    private readonly TimeProvider _time;
    private ITimer? _timer;
    private string? _sentHostJson;

    /// <summary>Who this install belongs to, and word when that changes.</summary>
    public event Action<Core.Portal.LinkState>? AccountLinkChanged
    {
        add => Settings.AccountSettingsViewModel.LinkChanged += value;
        remove => Settings.AccountSettingsViewModel.LinkChanged -= value;
    }

    /// <summary>Asked once when a window opens, so a page that depends on it does not start out wrong.</summary>
    public static Task<Core.Portal.LinkState> CurrentAccountAsync() =>
        Settings.AccountSettingsViewModel.CurrentAsync();

    public SettingsViewModel(HostLink host, Action<Action>? post = null, TimeProvider? time = null)
    {
        _host = host;
        _post = post ?? (action => Dispatcher.UIThread.Post(action));
        _time = time ?? TimeProvider.System;
        Security = new SecuritySettingsViewModel(host);
        Network = new NetworkSettingsViewModel();
        // Account is in the list but not in the row of tabs: it owns the portal address, which is saved
        // like any other setting, while what it is for lives in the sign-in window.
        _sections = [General, Display, Audio, Input, Security, Network, Account, About];

        // One computer, one name. The account tab used to ask for it again on its own line, which is how a
        // machine ended up called one thing to visitors and another in the account's device list.
        Account.DeviceName = () => General.DeviceName;

        // And the portal it signs in to, which is a network address and lives on the Network tab.
        Account.PortalServer = () => Network.PortalServer;

        IsHostAvailable = host.Config is not null;
        IsHostRefused = host.IsConnected && !host.IsOwner;
        LoadAll();
        foreach (ISettingsSection section in _sections)
        {
            section.Changed += OnSectionChanged;
        }

        host.ConfigChanged += _ => _post(() =>
        {
            IsHostAvailable = true;

            // Our own saves come back as a snapshot; reloading from those would fight whatever is being typed.
            if (_host.Config?.ToJson() != _sentHostJson)
            {
                LoadAll();
            }

            NeedsRestart |= _host.LastSaveNeedsRestart;
        });
        host.ConnectedChanged += connected => _post(() =>
        {
            IsHostAvailable = connected && host.Config is not null;
            IsHostRefused = connected && !host.IsOwner;
        });
        host.OwnerChanged += owner => _post(() =>
        {
            // A refusal takes the link's configuration away, so the page stops offering to change it.
            IsHostAvailable = host.IsConnected && host.Config is not null;
            IsHostRefused = host.IsConnected && !owner;
        });
        host.PasswordStateChanged += _ => _post(LoadAll);
    }

    public GeneralSettingsViewModel General { get; } = new();

    public DisplaySettingsViewModel Display { get; } = new();

    public AudioSettingsViewModel Audio { get; } = new();

    public InputSettingsViewModel Input { get; } = new();

    public SecuritySettingsViewModel Security { get; }

    public NetworkSettingsViewModel Network { get; }

    public AccountSettingsViewModel Account { get; } = new();

    public AboutSettingsViewModel About { get; } = new();

    /// <summary>False while the host service is not reachable; the host half of every tab is disabled.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHostOffline))]
    public partial bool IsHostAvailable { get; set; }

    /// <summary>
    /// The engine is running but will not show this account its settings: it is neither at this computer's own screen
    /// nor an administrator of it. The host half of every tab is disabled, as when it is not running, for another reason.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHostOffline))]
    public partial bool IsHostRefused { get; set; }

    /// <summary>What the banner says "not running" for: unavailable, and not because the engine said no.</summary>
    public bool IsHostOffline => !IsHostAvailable && !IsHostRefused;

    /// <summary>Sticky: some settings only reach the engine when it restarts, and the banner says so until it does.</summary>
    [ObservableProperty]
    public partial bool NeedsRestart { get; set; }

    /// <summary>Briefly true after a save, so the screen can acknowledge it.</summary>
    [ObservableProperty]
    public partial bool RecentlySaved { get; set; }

    /// <summary>
    /// A notice goes to the one place that shows messages and takes them away again. This screen used to
    /// keep its own line of text, set when something happened and cleared when the next thing happened --
    /// which, if nothing else happens, is never.
    /// </summary>
    partial void OnNoticeChanged(string value) => Services.Toasts.Current.Show(value, true);

    [ObservableProperty]
    public partial string Notice { get; set; } = string.Empty;

    /// <summary>Downloads the signing key from the rendezvous server's HTTP port (21114 by default).</summary>
    public static async Task<string> FetchKeyFromServerAsync(string server, CancellationToken ct)
    {
        (string host, _) = Core.Transport.TcpConnector.ParseHostPort(server, Protocol.ProtocolConstants.RendezvousPort);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        string key = await http.GetStringAsync($"http://{host}:{Protocol.ProtocolConstants.HttpApiPort}/key", ct);
        key = PeerSettings.CleanBase64(key);
        Convert.FromBase64String(key); // validate
        return key;
    }

    /// <summary>
    /// The fingerprint a person compares: the first sixteen hex digits of the SPKI's SHA-256, in groups of
    /// four, the same digits the rendezvous server prints at start-up.
    /// </summary>
    public static string KeyFingerprint(string base64)
    {
        string hex = Protocol.Crypto.IdentityKey.Fingerprint(Convert.FromBase64String(PeerSettings.CleanBase64(base64)));
        return string.Join(' ', Enumerable.Range(0, 4).Select(i => hex.Substring(i * 4, 4)));
    }

    /// <summary>Writes whatever is on screen now; the debounce timer calls this, tests can call it directly.</summary>
    public async Task SaveAsync()
    {
        DesktopConfig desktop = App.Config;
        foreach (ISettingsSection section in _sections)
        {
            desktop = section.Apply(desktop);
        }

        App.Config = desktop;
        try
        {
            App.Config.Save();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Notice = Strings.Format("settings.saveFailed", e.Message);
            return;
        }

        if (IsHostAvailable && _host.Config is { } current)
        {
            HostConfig host = current;
            foreach (ISettingsSection section in _sections)
            {
                host = section.Apply(host);
            }

            string json = host.ToJson();
            if (json != current.ToJson())
            {
                try
                {
                    _sentHostJson = json;
                    await _host.SaveConfigAsync(host);
                    NeedsRestart |= _host.LastSaveNeedsRestart;
                }
                catch (Exception e)
                {
                    Notice = e.Message;
                    return;
                }
            }
        }

        Notice = string.Empty;
        RecentlySaved = true;
        _post(() => _ = ClearSavedAsync());
    }

    private void OnSectionChanged()
    {
        _timer ??= _time.CreateTimer(_ => _post(() => _ = SaveAsync()), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _timer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
    }

    private async Task ClearSavedAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(2), _time);
        RecentlySaved = false;
    }

    private void LoadAll()
    {
        foreach (ISettingsSection section in _sections)
        {
            section.Load(App.Config, _host.Config);
        }
    }
}
