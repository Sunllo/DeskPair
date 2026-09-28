using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskPair.Core.Config;
using DeskPair.Desktop.Localization;
using DeskPair.Desktop.Services;

namespace DeskPair.Desktop.ViewModels;

public partial class HomeViewModel : ObservableObject
{
    private readonly HostLink _host;
    private readonly string? _devicesPath;

    /// <summary><paramref name="devicesPath"/> is where the device list lives; null means the user's own file.</summary>
    public HomeViewModel(HostLink host, string? devicesPath = null)
    {
        _host = host;
        _devicesPath = devicesPath;
        Id = host.Id;
        TempPassword = host.TempPassword;
        IsServiceConnected = host.IsConnected;
        IsHostOwner = host.IsOwner;
        RemoteId = string.Empty;
        PermanentPassword = string.Empty;
        Notice = string.Empty;
        host.ConnectedChanged += c => Dispatcher.UIThread.Post(() => IsServiceConnected = c);
        host.OwnerChanged += owner => Dispatcher.UIThread.Post(() => IsHostOwner = owner);
        host.IdChanged += id => Dispatcher.UIThread.Post(() => Id = id);
        ServerState = host.ServerState;
        host.ServerStateChanged += state => Dispatcher.UIThread.Post(() => ServerState = state);
        host.TempPasswordChanged += pw => Dispatcher.UIThread.Post(() => TempPassword = pw);
        host.ConfigChanged += _ => Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(ConnectLinkText)));
        host.PasswordStateChanged += state => Dispatcher.UIThread.Post(() =>
        {
            IsTempPasswordEnabled = state.TemporaryEnabled;
            IsTempPasswordPinned = state.TemporaryPinned;

            // The code carries the link password, so it has to be redrawn when that changes — which is
            // every time somebody connects with it. This is what makes a photographed code stale.
            OnPropertyChanged(nameof(ConnectLinkText));
            OnPropertyChanged(nameof(HasConnectLink));
        });
        if (host.PasswordState is { } current)
        {
            IsTempPasswordEnabled = current.TemporaryEnabled;
            IsTempPasswordPinned = current.TemporaryPinned;
        }

        ReloadRecent();
    }

    public ObservableCollection<RecentPeer> Recent { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IdText))]
    [NotifyPropertyChangedFor(nameof(ConnectLinkText))]
    [NotifyPropertyChangedFor(nameof(HasConnectLink))]
    public partial string Id { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TempPasswordText))]
    public partial string TempPassword { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TempPasswordText))]
    public partial bool IsTempPasswordEnabled { get; set; } = true;

    /// <summary>True when the user chose the password themselves, so it is not replaced by failed attempts.</summary>
    [ObservableProperty]
    public partial bool IsTempPasswordPinned { get; set; }


    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanManagePasswords))]
    [NotifyPropertyChangedFor(nameof(IsRefusedByHost))]
    public partial bool IsServiceConnected { get; set; }

    /// <summary>
    /// Whether the engine shows this account the password and lets it change it. It does not for somebody who is
    /// neither at this computer's own screen nor an administrator of it -- another account over remote desktop, say.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanManagePasswords))]
    [NotifyPropertyChangedFor(nameof(IsRefusedByHost))]
    public partial bool IsHostOwner { get; set; }

    public bool CanManagePasswords => IsServiceConnected && IsHostOwner;

    /// <summary>The engine is running and said no, which is not the same thing as it not running.</summary>
    public bool IsRefusedByHost => IsServiceConnected && !IsHostOwner;

    /// <summary>
    /// Whether the signalling server is answering the engine. Shown beside the id, because an id on a
    /// machine the server cannot hear is an id nobody can dial -- and "no network" is the usual reason.
    /// The engine registers again by itself the moment the network comes back; this only says so.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ServerStateText))]
    [NotifyPropertyChangedFor(nameof(IsServerUnreachable))]
    public partial Core.Transport.RendezvousLinkState ServerState { get; set; }

    public bool IsServerUnreachable => ServerState == Core.Transport.RendezvousLinkState.Unreachable;

    public string ServerStateText => ServerState switch
    {
        Core.Transport.RendezvousLinkState.Registered => Strings.Get("home.serverRegistered"),
        Core.Transport.RendezvousLinkState.Unreachable => Strings.Get("home.serverUnreachable"),
        _ => Strings.Get("home.serverConnecting"),
    };

    [ObservableProperty]
    public partial string RemoteId { get; set; }

    [ObservableProperty]
    public partial string PermanentPassword { get; set; }

    // ASCII only, as the text changes: a permanent password with a character in it that a viewer's
    // keyboard cannot produce is a machine nobody can get into. See PasswordText.
    partial void OnPermanentPasswordChanged(string value)
    {
        string ascii = Services.PasswordText.Ascii(value);
        if (ascii != value)
        {
            PermanentPassword = ascii;
        }
    }

    /// <summary>
    /// A notice goes to the one place that shows messages and takes them away again. This screen used to
    /// keep its own line of text, set when something happened and cleared when the next thing happened --
    /// which, if nothing else happens, is never.
    /// </summary>
    partial void OnNoticeChanged(string value) => Services.Toasts.Current.Show(value, false);

    [ObservableProperty]
    public partial string Notice { get; set; }

    [ObservableProperty]
    public partial RecentPeer? SelectedRecent { get; set; }

    public string IdText => Id.Length == 0 ? "—" : Id.Length == 9 ? $"{Id[..3]} {Id[3..6]} {Id[6..]}" : Id;

    /// <summary>
    /// The pairing code for the phone apps, as a link. Empty until this desk has an id to share.
    /// </summary>
    /// <remarks>
    /// It carries the rendezvous server and its public key as well as the id, because those are what a phone
    /// cannot get any other way: an SPKI is ninety-one bytes and nobody is typing it with their thumbs.
    /// The password in it is the host's one-time link password, never the one printed on this screen, and
    /// the first connection to use it spends it — see <see cref="Core.Config.ConnectLink"/>.
    /// </remarks>
    public string ConnectLinkText
    {
        get
        {
            if (Id.Length == 0 || PeerIdentityStore.IsLocal(Id))
            {
                // A LAN-xxxx id means no rendezvous server has assigned one, so there is nothing a phone
                // somewhere else could dial. Showing a code for it would be an invitation to fail.
                return string.Empty;
            }

            return new ConnectLink
            {
                Id = Id,
                RendezvousServer = App.Config.RendezvousServer,
                ServerPublicKeyBase64 = App.Config.ServerPublicKeyBase64,
                DeviceName = _host.Config?.DeviceName ?? string.Empty,
                Password = _host.PasswordState?.LinkPassword ?? string.Empty,
            }.ToString();
        }
    }

    public bool HasConnectLink => ConnectLinkText.Length > 0;

    public string TempPasswordText => !IsTempPasswordEnabled
        ? Strings.Get("home.passwordDisabled")
        : TempPassword.Length == 0 ? "—" : TempPassword;

    public bool HasRecent => Recent.Count > 0;

    partial void OnIdChanged(string value) => OnPropertyChanged(nameof(IdText));

    partial void OnSelectedRecentChanged(RecentPeer? value)
    {
        if (value is not null)
        {
            RemoteId = value.Id;
        }
    }

    /// <summary>Puts a recent connection into the saved list, where it can be named and put in a group.</summary>
    [RelayCommand]
    private void AddToDevices(RecentPeer? peer)
    {
        if (peer is null)
        {
            return;
        }

        DeviceBook book = DeviceBook.Load(_devicesPath);
        if (book.Devices.Any(d => string.Equals(d.Target, peer.Id, StringComparison.OrdinalIgnoreCase)))
        {
            Notice = Strings.Get("home.alreadyInDevices");
            return;
        }

        try
        {
            book.With(new SavedDevice
            {
                Target = peer.Id,
                Alias = peer.Name,
                LastConnected = peer.LastConnected,
                Platform = peer.Platform,
            }).Save(_devicesPath);
            Notice = Strings.Get("home.addedToDevices");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Notice = Strings.Format("settings.saveFailed", e.Message);
        }
    }

    [RelayCommand]
    private void Connect()
    {
        string target = RemoteId.Replace(" ", string.Empty).Trim();
        if (target.Length > 0)
        {
            App.OpenRemoteSession(target);
        }
    }

    [RelayCommand]
    private void Terminal()
    {
        string target = RemoteId.Replace(" ", string.Empty).Trim();
        if (target.Length > 0)
        {
            App.OpenTerminal(target);
        }
    }

    [RelayCommand]
    private void FileTransfer()
    {
        string target = RemoteId.Replace(" ", string.Empty).Trim();
        if (target.Length > 0)
        {
            App.OpenFileTransfer(target);
        }
    }

    [RelayCommand]
    private async Task RegeneratePasswordAsync()
    {
        try
        {
            await _host.RotateTemporaryPasswordAsync();
            Notice = Strings.Get("home.passwordRegenerated");
        }
        catch (Exception e)
        {
            Notice = e.Message;
        }
    }

    /// <summary>Turns temporary passwords off or on; the permanent password keeps working either way.</summary>
    [RelayCommand]
    private async Task ToggleTempPasswordEnabledAsync()
    {
        if (_host.Config is not { } config)
        {
            return;
        }

        try
        {
            await _host.SaveConfigAsync(config with { TemporaryPasswordEnabled = !IsTempPasswordEnabled });
        }
        catch (Exception e)
        {
            Notice = e.Message;
        }
    }

    [RelayCommand]
    private async Task CopyIdAsync()
    {
        await App.CopyToClipboardAsync(Id);
        Notice = Strings.Get("home.copied");
    }

    [RelayCommand]
    private async Task CopyPasswordAsync()
    {
        await App.CopyToClipboardAsync(TempPassword);
        Notice = Strings.Get("home.copied");
    }

    [RelayCommand]
    private async Task SetPermanentPasswordAsync()
    {
        try
        {
            if (await _host.SetPermanentPasswordAsync(PermanentPassword))
            {
                Notice = Strings.Get("home.passwordSet");
                PermanentPassword = string.Empty;
            }
        }
        catch (Exception e)
        {
            Notice = e.Message;
        }
    }

    public void ReloadRecent()
    {
        Recent.Clear();
        foreach (RecentPeer peer in App.Config.Recent)
        {
            Recent.Add(peer);
        }

        OnPropertyChanged(nameof(HasRecent));
    }
}
