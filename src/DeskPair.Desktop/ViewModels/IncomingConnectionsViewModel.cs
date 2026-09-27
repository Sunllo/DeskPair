using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskPair.Desktop.Localization;
using DeskPair.Desktop.Services;
using DeskPair.Protocol.Ipc;

namespace DeskPair.Desktop.ViewModels;

/// <summary>
/// The notice that floats at the bottom of the main window while someone is connecting to this computer. The
/// engine broadcasts a connection the moment the peer identifies itself, well before it is allowed in, so the
/// local user sees who is knocking without the connection manager having to appear.
/// </summary>
public partial class IncomingConnectionsViewModel : ObservableObject
{
    /// <summary>How long an established connection stays on the strip before it fades out of the way.</summary>
    public static readonly TimeSpan ConnectedLinger = TimeSpan.FromSeconds(6);

    private readonly TimeProvider _time;
    private readonly Action<Action> _post;

    public IncomingConnectionsViewModel(HostLink host, Action<Action>? post = null, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _post = post ?? (action => Avalonia.Threading.Dispatcher.UIThread.Post(action));
        host.Pushed += m => _post(() => OnPushed(m));
        host.ConnectedChanged += connected =>
        {
            if (!connected)
            {
                _post(Clear);
            }
        };
    }

    public ObservableCollection<IncomingConnection> Items { get; } = [];

    public bool HasItems => Items.Count > 0;

    /// <summary>Raised when a connection first appears, so a hidden window can say so in the notification area.</summary>
    public event Action<string>? Arrived;

    [RelayCommand]
    private void Dismiss(IncomingConnection? item)
    {
        if (item is not null)
        {
            Remove(item.ConnId);
        }
    }

    private void OnPushed(IpcMessage m)
    {
        switch (m.UnionCase)
        {
            case IpcMessage.UnionOneofCase.ConnectionOpened:
                ConnectionOpened o = m.ConnectionOpened;
                IncomingConnection item = Items.FirstOrDefault(i => i.ConnId == o.ConnId) ?? Add(o.ConnId);
                if (o.PeerId.Length > 0 || o.PeerName.Length > 0)
                {
                    item.Who = o.PeerName.Length > 0 ? o.PeerName : Spaced(o.PeerId);
                }
                else if (o.RemoteAddress.Length > 0 && !item.Identified)
                {
                    // The very first event arrives before the caller has said who it is; show where it is
                    // calling from rather than a bare placeholder, and let Identified replace it.
                    item.Who = Readable(o.RemoteAddress);
                }

                item.Identified |= o.PeerId.Length > 0 || o.PeerName.Length > 0;

                if (o.PeerPlatform.Length > 0)
                {
                    item.Platform = o.PeerPlatform;
                }

                if (o.Authorized && !item.IsAuthorized)
                {
                    item.IsAuthorized = true;
                    _ = LingerAsync(item);
                }

                break;
            case IpcMessage.UnionOneofCase.ConnectionClosed:
                Remove(m.ConnectionClosed.ConnId);
                break;
        }
    }

    private IncomingConnection Add(int connId)
    {
        var item = new IncomingConnection(connId);
        Items.Add(item);
        OnPropertyChanged(nameof(HasItems));
        Arrived?.Invoke(item.Text);
        return item;
    }

    /// <summary>An established session no longer needs the strip; the connection manager takes over from here.</summary>
    private async Task LingerAsync(IncomingConnection item)
    {
        await Task.Delay(ConnectedLinger, _time).ConfigureAwait(false);
        _post(() => Remove(item.ConnId));
    }

    private void Remove(int connId)
    {
        if (Items.FirstOrDefault(i => i.ConnId == connId) is { } item)
        {
            Items.Remove(item);
            OnPropertyChanged(nameof(HasItems));
        }
    }

    private void Clear()
    {
        Items.Clear();
        OnPropertyChanged(nameof(HasItems));
    }

    private static string Spaced(string id) =>
        id.Length == 9 && id.All(char.IsAsciiDigit) ? $"{id[..3]} {id[3..6]} {id[6..]}" : id;

    /// <summary>
    /// The address as somebody would write it, rather than as a socket reports it.
    ///
    /// A dual-stack listener hands back every IPv4 caller as an IPv4-mapped IPv6 address, so what reached
    /// the screen was "::ffff:203.0.113.243" -- which reads as a different and more alarming kind of
    /// address than the one it is, and which nobody would recognise as their own machine. The port goes
    /// too: it is the ephemeral one the caller happened to get, and it says nothing about who they are.
    /// </summary>
    internal static string Readable(string address)
    {
        string text = address.Trim();
        if (System.Net.IPEndPoint.TryParse(text, out System.Net.IPEndPoint? endPoint))
        {
            text = endPoint.Address.ToString();
        }

        if (System.Net.IPAddress.TryParse(text, out System.Net.IPAddress? parsed) && parsed.IsIPv4MappedToIPv6)
        {
            return parsed.MapToIPv4().ToString();
        }

        return text;
    }
}

/// <summary>One line on the strip: who is connecting, and whether they are in yet.</summary>
public sealed partial class IncomingConnection(int connId) : ObservableObject
{
    public int ConnId { get; } = connId;

    /// <summary>Who is calling: the caller's name, then its id, falling back to the address it called from.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Text))]
    public partial string Who { get; set; } = Strings.Get("incoming.unknown");

    /// <summary>Set once the caller has named itself, so a later address does not overwrite the name.</summary>
    public bool Identified { get; set; }

    /// <summary>What the caller is running, for the logo beside the notice. Empty shows none.</summary>
    [ObservableProperty]
    public partial string Platform { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Text))]
    public partial bool IsAuthorized { get; set; }

    public string Text => Strings.Format(IsAuthorized ? "incoming.connected" : "incoming.verifying", Who);
}
