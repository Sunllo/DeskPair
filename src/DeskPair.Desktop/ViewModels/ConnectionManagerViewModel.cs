using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskPair.Desktop.Localization;
using DeskPair.Desktop.Services;
using DeskPair.Protocol.Ipc;
using DeskPair.Protocol.Messages;
using DeskPair.Protocol.Rendezvous;

namespace DeskPair.Desktop.ViewModels;

/// <summary>One inbound connection as seen by the local user.</summary>
public sealed partial class CmConnection : ObservableObject
{
    private readonly ConnectionManagerViewModel _owner;

    public CmConnection(ConnectionManagerViewModel owner, int connId)
    {
        _owner = owner;
        ConnId = connId;
        PeerId = string.Empty;
        PeerName = string.Empty;
        PeerPlatform = string.Empty;
        Address = string.Empty;
        Description = string.Empty;
        Chat = new ChatViewModel(text => owner.SendChatAsync(connId, text));
        Keyboard = Clipboard = Audio = Files = true;
    }

    public int ConnId { get; }

    public ChatViewModel Chat { get; }

    [ObservableProperty]
    public partial string PeerId { get; set; }

    [ObservableProperty]
    public partial string PeerName { get; set; }

    /// <summary>What the caller is running, for the logo beside its name. Empty shows none.</summary>
    [ObservableProperty]
    public partial string PeerPlatform { get; set; }

    [ObservableProperty]
    public partial string Address { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStandardPermissions))]
    public partial bool IsFileTransfer { get; set; }

    /// <summary>A request for a shell rather than for the desktop.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStandardPermissions), nameof(CanAccept))]
    public partial bool IsTerminal { get; set; }

    /// <summary>
    /// Whether the shell is allowed. Unticked by default even when the settings allow terminals, so
    /// accepting one takes two deliberate acts: ticking this, then accepting. Unticking it later ends
    /// every shell the connection has open.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAccept))]
    public partial bool Terminal { get; set; }

    /// <summary>The account the shell would run as, as the host engine reports it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TerminalLabel))]
    public partial string TerminalIdentity { get; set; } = string.Empty;

    public string TerminalLabel => Strings.Format("cm.allowTerminal", TerminalIdentity.Length > 0 ? TerminalIdentity : "?");

    public bool ShowStandardPermissions => !IsFileTransfer && !IsTerminal;

    public bool CanAccept => !IsTerminal || Terminal;

    [ObservableProperty]
    public partial bool IsPending { get; set; }

    [ObservableProperty]
    public partial bool IsAuthorized { get; set; }

    [ObservableProperty]
    public partial string Description { get; set; }

    [ObservableProperty]
    public partial bool Keyboard { get; set; }

    [ObservableProperty]
    public partial bool Clipboard { get; set; }

    [ObservableProperty]
    public partial bool Audio { get; set; }

    [ObservableProperty]
    public partial bool Files { get; set; }

    [ObservableProperty]
    public partial bool ChatOpen { get; set; }

    /// <summary>This connection has asked to see and drive the secure desktop (a UAC prompt) and is waiting for the person here.</summary>
    [ObservableProperty]
    public partial bool IsElevationPending { get; set; }

    /// <summary>The words of the elevation prompt, naming the device that asked.</summary>
    [ObservableProperty]
    public partial string ElevationText { get; set; } = string.Empty;

    /// <summary>Phase 3: this host can offer "always allow listed devices" (it has a permanent password and the device is listed).</summary>
    [ObservableProperty]
    public partial bool ElevationCanInstall { get; set; }

    /// <summary>Phase 3: the person ticked "always allow listed devices" in the elevation prompt.</summary>
    [ObservableProperty]
    public partial bool ElevationPermanent { get; set; }

    [RelayCommand]
    private Task AllowElevationAsync() => _owner.DecideElevationAsync(this, allow: true);

    [RelayCommand]
    private Task DenyElevationAsync() => _owner.DecideElevationAsync(this, allow: false);

    public string Header => PeerName.Length > 0 ? $"{PeerName} ({PeerId})" : PeerId.Length > 0 ? PeerId : Address;

    public string PeerIdSpaced => PeerId.Length == 9 ? $"{PeerId[..3]} {PeerId[3..6]} {PeerId[6..]}" : PeerId;

    partial void OnPeerIdChanged(string value)
    {
        OnPropertyChanged(nameof(Header));
        OnPropertyChanged(nameof(PeerIdSpaced));
    }

    partial void OnPeerNameChanged(string value) => OnPropertyChanged(nameof(Header));

    partial void OnKeyboardChanged(bool value) => _owner.SetPermission(ConnId, Permission.PermKeyboard, value);

    partial void OnClipboardChanged(bool value) => _owner.SetPermission(ConnId, Permission.PermClipboard, value);

    partial void OnAudioChanged(bool value) => _owner.SetPermission(ConnId, Permission.PermAudio, value);

    partial void OnFilesChanged(bool value) => _owner.SetPermission(ConnId, Permission.PermFile, value);

    partial void OnTerminalChanged(bool value) => _owner.SetPermission(ConnId, Permission.PermTerminal, value);

    [RelayCommand]
    private Task AcceptAsync() => _owner.DecideAsync(this, accept: true);

    [RelayCommand]
    private Task RejectAsync() => _owner.DecideAsync(this, accept: false);

    [RelayCommand]
    private Task CloseAsync() => _owner.CloseAsync(this);
}

/// <summary>The connection manager (<c>--cm</c>): approvals, live permission switches, chat and transfer progress.</summary>
public partial class ConnectionManagerViewModel : ObservableObject
{
    private readonly HostLink _host;
    private bool _applyingPermission;

    public ConnectionManagerViewModel(HostLink host)
    {
        _host = host;
        host.Pushed += m => Dispatcher.UIThread.Post(() => OnPushed(m));
        host.ConnectedChanged += connected => Dispatcher.UIThread.Post(() =>
        {
            if (!connected)
            {
                Connections.Clear();
                OnPropertyChanged(nameof(HasConnections));
                EngineLost?.Invoke();
            }
        });
    }

    /// <summary>The host engine went away; a connection manager without an engine has nothing to manage.</summary>
    public event Action? EngineLost;

    public event Action<CmConnection>? ApprovalRequested;

    /// <summary>A session became authorized; the panel should come to the front so the local user notices.</summary>
    public event Action<CmConnection>? ConnectionAuthorized;

    /// <summary>Collapsed to the arrow handle in the corner; the card is still there, just out of the way.</summary>
    [ObservableProperty]
    public partial bool IsCollapsed { get; set; }

    public ObservableCollection<CmConnection> Connections { get; } = [];

    public ObservableCollection<string> Transfers { get; } = [];

    public bool HasConnections => Connections.Count > 0;

    [RelayCommand]
    private void ToggleCollapsed() => IsCollapsed = !IsCollapsed;

    [RelayCommand]
    private void Collapse() => IsCollapsed = true;

    public async Task DecideAsync(CmConnection c, bool accept)
    {
        ApprovalDecision decision = Decision(c, accept);
        c.IsPending = false;
        await _host.SendAsync(new IpcMessage { ApprovalDecision = decision });
        if (!decision.Accept)
        {
            Remove(c.ConnId);
        }
    }

    /// <summary>
    /// The person here allows or refuses a request to see and drive the secure desktop. Allowing raises the
    /// helper, which shows the real UAC they still complete; the connection stays either way, so a refusal only
    /// clears the prompt.
    /// </summary>
    public async Task DecideElevationAsync(CmConnection c, bool allow)
    {
        c.IsElevationPending = false;
        await _host.SendAsync(new IpcMessage
        {
            ElevationDecision = new ElevationDecision { ConnId = c.ConnId, Allow = allow, Permanent = allow && c.ElevationPermanent },
        });
    }

    /// <summary>
    /// What an answer to a request says. A terminal request that is accepted without its box ticked is a
    /// refusal: the accept button is disabled then, and this is the rule that button stands for.
    /// </summary>
    public static ApprovalDecision Decision(CmConnection c, bool accept)
    {
        accept &= c.CanAccept;
        var decision = new ApprovalDecision { ConnId = c.ConnId, Accept = accept };
        if (accept)
        {
            if (c.Keyboard)
            {
                decision.Granted.Add(Permission.PermKeyboard);
            }

            if (c.Clipboard)
            {
                decision.Granted.Add(Permission.PermClipboard);
            }

            if (c.Audio)
            {
                decision.Granted.Add(Permission.PermAudio);
            }

            if (c.Files)
            {
                decision.Granted.Add(Permission.PermFile);
            }

            // Named or not granted at all: the engine gives a shell only to an acceptance that names it.
            if (c.IsTerminal && c.Terminal)
            {
                decision.Granted.Add(Permission.PermTerminal);
            }
        }

        return decision;
    }

    public async Task CloseAsync(CmConnection c)
    {
        await _host.SendAsync(new IpcMessage { CloseConnection = new CloseConnection { ConnId = c.ConnId } });
        Remove(c.ConnId);
    }

    public Task SendChatAsync(int connId, string text) => _host.SendAsync(new IpcMessage { Chat = new ChatRelay { ConnId = connId, Text = text, FromPeer = false } });

    public void SetPermission(int connId, Permission permission, bool enabled)
    {
        if (_applyingPermission)
        {
            return;
        }

        CmConnection? c = Connections.FirstOrDefault(x => x.ConnId == connId);
        if (c is { IsAuthorized: true })
        {
            _ = _host.SendAsync(new IpcMessage { PermissionChange = new PermissionChange { ConnId = connId, Permission = permission, Enabled = enabled } });
        }
    }

    private void OnPushed(IpcMessage m)
    {
        switch (m.UnionCase)
        {
            case IpcMessage.UnionOneofCase.ConnectionOpened:
                ConnectionOpened o = m.ConnectionOpened;
                CmConnection c = GetOrAdd(o.ConnId);
                if (o.PeerId.Length > 0)
                {
                    c.PeerId = o.PeerId;
                }

                if (o.PeerName.Length > 0)
                {
                    c.PeerName = o.PeerName;
                }

                if (o.PeerPlatform.Length > 0)
                {
                    c.PeerPlatform = o.PeerPlatform;
                }

                if (o.RemoteAddress.Length > 0)
                {
                    c.Address = o.RemoteAddress;
                }

                c.IsFileTransfer = o.ConnType == ConnType.ConnFileTransfer;
                c.IsTerminal = o.ConnType == ConnType.ConnTerminal;
                if (o.Authorized && !c.IsAuthorized)
                {
                    bool wasPending = c.IsPending;
                    c.IsAuthorized = true;
                    c.IsPending = false;
                    c.Description = c.IsTerminal ? Strings.Get("terminal.title") : c.IsFileTransfer ? Strings.Get("home.fileTransfer") : Strings.Get("home.controlRemote");
                    if (c.IsTerminal && !wasPending)
                    {
                        // In by password, so the settings decided and the shell is allowed; the switch starts
                        // on, and turning it off is how the person at this desk takes it away.
                        _applyingPermission = true;
                        c.Terminal = true;
                        _applyingPermission = false;
                    }

                    ConnectionAuthorized?.Invoke(c);
                }

                break;
            case IpcMessage.UnionOneofCase.ApprovalRequest:
                ApprovalRequest r = m.ApprovalRequest;
                CmConnection p = GetOrAdd(r.ConnId);
                p.PeerId = r.PeerId;
                p.PeerName = r.PeerName;
                p.PeerPlatform = r.PeerPlatform;
                p.IsFileTransfer = r.ConnType == ConnType.ConnFileTransfer;
                p.IsTerminal = r.ConnType == ConnType.ConnTerminal;
                p.TerminalIdentity = r.TerminalIdentity;
                _applyingPermission = true;
                p.Terminal = false;
                _applyingPermission = false;
                p.IsPending = true;
                p.Description = Strings.Format(
                    p.IsTerminal ? "cm.wantsTerminal" : p.IsFileTransfer ? "cm.wantsFiles" : "cm.wantsToControl",
                    r.PeerName.Length > 0 ? r.PeerName : "?",
                    r.PeerId);
                IsCollapsed = false;
                ApprovalRequested?.Invoke(p);
                break;
            case IpcMessage.UnionOneofCase.ElevationRequest:
                ElevationRequest e = m.ElevationRequest;
                CmConnection ec = GetOrAdd(e.ConnId);
                ec.ElevationText = Strings.Format("cm.elevation.wants", e.PeerName.Length > 0 ? e.PeerName : e.PeerId.Length > 0 ? e.PeerId : "?");
                ec.ElevationCanInstall = e.CanInstall;
                ec.ElevationPermanent = false;
                ec.IsElevationPending = true;
                IsCollapsed = false;
                ApprovalRequested?.Invoke(ec);
                break;
            case IpcMessage.UnionOneofCase.ConnectionClosed:
                Remove(m.ConnectionClosed.ConnId);
                break;
            case IpcMessage.UnionOneofCase.Chat when m.Chat.FromPeer:
                CmConnection? chatTarget = Connections.FirstOrDefault(x => x.ConnId == m.Chat.ConnId);
                if (chatTarget is not null)
                {
                    chatTarget.Chat.Received(m.Chat.Text);
                    chatTarget.ChatOpen = true;
                }
                break;
            case IpcMessage.UnionOneofCase.PermissionChange:
                CmConnection? pc = Connections.FirstOrDefault(x => x.ConnId == m.PermissionChange.ConnId);
                if (pc is not null)
                {
                    _applyingPermission = true;
                    switch (m.PermissionChange.Permission)
                    {
                        case Permission.PermKeyboard: pc.Keyboard = m.PermissionChange.Enabled; break;
                        case Permission.PermClipboard: pc.Clipboard = m.PermissionChange.Enabled; break;
                        case Permission.PermAudio: pc.Audio = m.PermissionChange.Enabled; break;
                        case Permission.PermFile: pc.Files = m.PermissionChange.Enabled; break;
                        case Permission.PermTerminal: pc.Terminal = m.PermissionChange.Enabled; break;
                    }

                    _applyingPermission = false;
                }

                break;
            case IpcMessage.UnionOneofCase.FileJobProgress:
                FileJobProgress f = m.FileJobProgress;
                string line = $"#{f.ConnId}/{f.JobId}: {f.FileNum + 1}/{f.TotalFiles} {FileRow.FormatSize((long)f.TransferredBytes)}/{FileRow.FormatSize((long)f.TotalBytes)} {f.State}{(f.Error.Length > 0 ? " " + f.Error : string.Empty)}";
                string prefix = $"#{f.ConnId}/{f.JobId}:";
                int idx = -1;
                for (int i = 0; i < Transfers.Count; i++)
                {
                    if (Transfers[i].StartsWith(prefix, StringComparison.Ordinal))
                    {
                        idx = i;
                        break;
                    }
                }

                if (idx >= 0)
                {
                    Transfers[idx] = line;
                }
                else
                {
                    Transfers.Insert(0, line);
                }

                break;
        }
    }

    private CmConnection GetOrAdd(int connId)
    {
        CmConnection? c = Connections.FirstOrDefault(x => x.ConnId == connId);
        if (c is null)
        {
            c = new CmConnection(this, connId);
            Connections.Add(c);
            OnPropertyChanged(nameof(HasConnections));
        }

        return c;
    }

    private void Remove(int connId)
    {
        CmConnection? c = Connections.FirstOrDefault(x => x.ConnId == connId);
        if (c is not null)
        {
            Connections.Remove(c);
            OnPropertyChanged(nameof(HasConnections));
        }
    }
}
