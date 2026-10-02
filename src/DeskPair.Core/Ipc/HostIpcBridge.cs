using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using DeskPair.Core.Config;
using DeskPair.Core.Services;
using DeskPair.Core.Session.Host;
using DeskPair.Protocol.Ipc;
using DeskPair.Protocol.Messages;

namespace DeskPair.Core.Ipc;

/// <summary>Well-known IPC client roles.</summary>
public static class IpcRoles
{
    public const string Ui = "ui";
    public const string ConnectionManager = "cm";
    public const string Tray = "tray";
}

/// <summary>
/// The host service's side of the IPC contract: answers UI requests against the runtime and forwards
/// session events to the connection manager. Also the runtime's <see cref="IConnectionApprover"/>:
/// approval requests are pushed to the CM and answered by its decision, and rejected when no CM is present.
/// </summary>
public sealed class HostIpcBridge : IIpcHostBridge, IConnectionApprover
{
    private readonly HostConfigStore _configStore;
    private HostConfig _applied;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<ApprovalDecision>> _approvals = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<ElevationDecision>> _elevations = new();
    private HostRuntime? _runtime;
    private IpcServer? _server;

    private static ConnectionHistoryEntry ToEntry(Session.Host.ConnectionRecord record)
    {
        var entry = new ConnectionHistoryEntry
        {
            Id = record.Id,
            StartedUtcMs = record.StartedUtc.ToUnixTimeMilliseconds(),
            EndedUtcMs = record.EndedUtc?.ToUnixTimeMilliseconds() ?? 0,
            PeerId = record.PeerId,
            PeerName = record.PeerName,
            PeerPlatform = record.PeerPlatform,
            Address = record.Address,
            AddressReported = record.AddressReported,
            Transport = record.Transport,
            Kind = record.Kind,
            Authenticated = record.Authenticated,
            Reason = record.Reason,
            TerminalOpens = record.TerminalOpens,
            TerminalIdentity = record.TerminalIdentity,
        };
        entry.Granted.AddRange(record.Granted);
        return entry;
    }

    public HostIpcBridge(HostConfigStore configStore, ILogger log)
    {
        _configStore = configStore;
        _applied = configStore.Load();
        _log = log;
    }

    /// <summary>Raised when the UI saves configuration; the service decides whether a restart is needed.</summary>
    public event Action<HostConfig>? ConfigChanged;

    /// <summary>
    /// The record of who has connected, for the app to show. Null when the engine keeps none, and then the
    /// request answers with an empty list rather than an error -- the page says "nothing recorded", which is
    /// the truth.
    /// </summary>
    public Session.Host.ConnectionJournal? Journal { get; set; }

    /// <summary>
    /// Starts a connection manager process when a connection arrives and none is attached. Approval
    /// requests wait up to <see cref="ConnectionManagerStartTimeout"/> for it to connect before failing closed.
    /// </summary>
    public Func<CancellationToken, Task>? ConnectionManagerLauncher { get; set; }

    public TimeSpan ConnectionManagerStartTimeout { get; set; } = TimeSpan.FromSeconds(8);

    private Task? _launching;

    /// <summary>Called once the runtime and server exist (they need this object first).</summary>
    public void Attach(HostRuntime runtime, IpcServer server)
    {
        _runtime = runtime;
        _server = server;
        runtime.Rendezvous.IdAssigned += id => _ = Push(new IpcMessage { IdChanged = new IdChanged { Id = id } });
        runtime.Rendezvous.StateChanged += state => _ = Push(new IpcMessage { ServerState = new ServerState { State = (int)state } });
        runtime.Passwords.TemporaryPasswordChanged += pw => _ = Push(new IpcMessage { TempPassword = new TempPassword { Password = pw } });
        runtime.Passwords.StateChanged += () => _ = Push(PasswordStateOf(runtime));
    }

    public void AttachFileModule(HostFileModule files)
    {
        files.Progress += (conn, s) => _ = Push(new IpcMessage
        {
            FileJobProgress = new FileJobProgress
            {
                ConnId = conn,
                JobId = s.Id,
                FileNum = s.FileIndex,
                TotalFiles = s.TotalFiles,
                TotalBytes = (ulong)s.TotalBytes,
                TransferredBytes = (ulong)s.TransferredBytes,
                State = s.State.ToString(),
                Error = s.Error ?? string.Empty,
            },
        }, IpcRoles.ConnectionManager);
    }

    // ---- IIpcHostBridge ----

    public async Task<IpcMessage?> HandleAsync(IpcMessage request, IpcClientInfo client, CancellationToken ct)
    {
        HostRuntime runtime = _runtime ?? throw new InvalidOperationException("Bridge not attached.");

        // Before anything is read or written, and by the one fact about the caller that the caller did
        // not choose. The table says what each command means; IpcCaller says who is asking. Neither the
        // hello's role nor possession of the token is consulted here: a role is a word a client says
        // about itself, and the token sits in ProgramData where the Users group can read it.
        if (IpcAuthorities.For(request.UnionCase) == IpcAuthority.Owner && !client.Caller.IsOwner)
        {
            string because = client.Caller.Because.Length == 0 ? "the caller could not be identified" : client.Caller.Because;
            _log.LogWarning(
                "Refused {Case} from IPC client {Id} ({User}): {Because}",
                request.UnionCase,
                client.Id,
                client.Caller.Name.Length == 0 ? "unidentified" : client.Caller.Name,
                because);

            // Said, not left unanswered: a window that hears nothing waits out its timeout, takes the silence for a
            // lost engine, and reconnects to ask again -- every twelve seconds, for as long as it is open.
            return new IpcMessage { Refused = new IpcRefused { Reason = because } };
        }

        switch (request.UnionCase)
        {
            case IpcMessage.UnionOneofCase.Ping:
                return new IpcMessage { Pong = new Pong() };
            case IpcMessage.UnionOneofCase.GetId:
                return new IpcMessage { IdChanged = new IdChanged { Id = runtime.Identity.Id } };
            case IpcMessage.UnionOneofCase.GetServerState:
                return new IpcMessage { ServerState = new ServerState { State = (int)runtime.Rendezvous.State } };
            case IpcMessage.UnionOneofCase.GetTempPassword:
                return new IpcMessage { TempPassword = new TempPassword { Password = runtime.Passwords.TemporaryPassword } };
            case IpcMessage.UnionOneofCase.SetPermanentPassword:
                await runtime.Passwords.SetPermanentAsync(request.SetPermanentPassword.Password, ct).ConfigureAwait(false);
                return new IpcMessage { PasswordAck = new PasswordAck { Ok = true } };
            case IpcMessage.UnionOneofCase.GetPasswordState:
                return PasswordStateOf(runtime);
            case IpcMessage.UnionOneofCase.RotateTemporaryPassword:
                await runtime.Passwords.SetTemporaryAsync(null, ct).ConfigureAwait(false);
                return PasswordStateOf(runtime);
            case IpcMessage.UnionOneofCase.SetTemporaryPassword:
                try
                {
                    await runtime.Passwords.SetTemporaryAsync(request.SetTemporaryPassword.Password, ct).ConfigureAwait(false);
                    return new IpcMessage { PasswordAck = new PasswordAck { Ok = true } };
                }
                catch (ArgumentException e)
                {
                    return new IpcMessage { PasswordAck = new PasswordAck { Ok = false, Error = e.Message } };
                }
            case IpcMessage.UnionOneofCase.ConnectionHistoryRequest:
            {
                var answer = new ConnectionHistory();
                if (Journal is { } journal)
                {
                    if (request.ConnectionHistoryRequest.Clear)
                    {
                        journal.Clear();
                    }

                    int limit = request.ConnectionHistoryRequest.Limit > 0 ? request.ConnectionHistoryRequest.Limit : 500;
                    answer.Entries.AddRange(journal.Read(limit).Select(ToEntry));
                }

                return new IpcMessage { ConnectionHistory = answer };
            }

            case IpcMessage.UnionOneofCase.GetConfig:
                return new IpcMessage { ConfigSnapshot = new ConfigSnapshot { Json = _configStore.Load().ToJson() } };
            case IpcMessage.UnionOneofCase.SetConfig:
                HostConfig config = HostConfig.FromJson(request.SetConfig.Json);
                bool restart = HostConfig.RequiresEngineRestart(_applied, config);
                _configStore.Save(config);
                _applied = config;
                ConfigChanged?.Invoke(config);
                var snapshot = new ConfigSnapshot { Json = config.ToJson(), RestartRequired = restart };
                // Everyone but the client that asked for the change: it already has the answer below, and an echo
                // would overwrite the settings screen while the user is still typing in it.
                await Push(new IpcMessage { ConfigSnapshot = snapshot.Clone() }, except: client).ConfigureAwait(false);
                return new IpcMessage { ConfigSnapshot = snapshot };
            case IpcMessage.UnionOneofCase.ApprovalDecision:
                // Only the connection manager decides; every other client sees the request but cannot answer
                // it. This is not a defence against a hostile client -- the token is in a file any process of
                // this user can read -- it keeps the decision in the one window the user is looking at.
                if (client.Role == IpcRoles.ConnectionManager
                    && _approvals.TryRemove(request.ApprovalDecision.ConnId, out TaskCompletionSource<ApprovalDecision>? tcs))
                {
                    tcs.TrySetResult(request.ApprovalDecision);
                }

                return null;
            case IpcMessage.UnionOneofCase.ElevationDecision:
                // As with ApprovalDecision: only the connection manager answers, keeping the most security-relevant
                // prompt in the one window the person is looking at.
                if (client.Role == IpcRoles.ConnectionManager
                    && _elevations.TryRemove(request.ElevationDecision.ConnId, out TaskCompletionSource<ElevationDecision>? elev))
                {
                    elev.TrySetResult(request.ElevationDecision);
                }

                return null;
            case IpcMessage.UnionOneofCase.PermissionChange:
                FindSession(request.PermissionChange.ConnId)?.Context.Permissions.SetOverride(request.PermissionChange.Permission, request.PermissionChange.Enabled);
                return null;
            case IpcMessage.UnionOneofCase.Chat when !request.Chat.FromPeer:
                HostSession? target = FindSession(request.Chat.ConnId);
                if (target is not null)
                {
                    await target.Context.SendAsync(new Message { Misc = new Misc { Chat = new ChatMessage { Text = request.Chat.Text } } }, ct: ct).ConfigureAwait(false);
                }

                return null;
            case IpcMessage.UnionOneofCase.CloseConnection:
                HostSession? closing = FindSession(request.CloseConnection.ConnId);
                if (closing is not null)
                {
                    await closing.CloseAsync("closed by local user").ConfigureAwait(false);
                }

                return null;
            case IpcMessage.UnionOneofCase.DesktopSharingRequest:
            {
                if (DesktopSharing is not { } consent || client.Caller.Uid is not { } uid)
                {
                    return new IpcMessage { DesktopSharingState = new DesktopSharingState() };
                }

                if (!request.DesktopSharingRequest.Ask)
                {
                    return new IpcMessage { DesktopSharingState = new DesktopSharingState { State = (int)await consent.SharingStateAsync(uid, ct).ConfigureAwait(false) } };
                }

                // As long as the person takes to answer: the answer goes back on its own, and this client's other
                // requests are not held up behind it.
                _ = AskSharingAsync(consent, uid, client, request.RequestId);
                return null;
            }

            default:
                _log.LogDebug("Unhandled IPC request {Case} from {Role}", request.UnionCase, client.Role);
                return null;
        }
    }

    private async Task AskSharingAsync(DeskPair.Platform.Abstractions.Capture.IDesktopSharingConsent consent, uint uid, IpcClientInfo client, uint requestId)
    {
        var answer = new DesktopSharingState();
        try
        {
            (DeskPair.Platform.Abstractions.Capture.DesktopSharingOutcome outcome, string? detail) =
                await consent.AskSharingAsync(uid, CancellationToken.None).ConfigureAwait(false);
            answer.Outcome = (int)outcome + 1;
            answer.Detail = detail ?? string.Empty;
            answer.State = (int)await consent.SharingStateAsync(uid, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Asking uid {Uid} to allow sharing their desktop failed", uid);
            answer.Outcome = (int)DeskPair.Platform.Abstractions.Capture.DesktopSharingOutcome.Failed + 1;
            answer.Detail = e.Message;
        }

        if (_server is { } server)
        {
            await server.SendToAsync(client.Id, new IpcMessage { RequestId = requestId, DesktopSharingState = answer }).ConfigureAwait(false);
        }
    }

    /// <summary>A connection manager that starts late still needs the sessions that are already open.</summary>
    public void ClientConnected(IpcClientInfo client)
    {
        if (client.Role != IpcRoles.ConnectionManager || _runtime is null)
        {
            return;
        }

        foreach (HostSession session in _runtime.Sessions.Where(s => s.Context.State == HostSessionState.Authorized))
        {
            Notify(new HostSessionEvent(session.Context.ConnectionId, HostEventKind.Authorized));
        }
    }

    public void ClientDisconnected(IpcClientInfo client)
    {
        if (client.Role == IpcRoles.ConnectionManager && _server is not null && !_server.HasClient(IpcRoles.ConnectionManager))
        {
            // Nobody left to answer: pending approvals fail closed.
            foreach (KeyValuePair<int, TaskCompletionSource<ApprovalDecision>> kv in _approvals)
            {
                kv.Value.TrySetResult(new ApprovalDecision { ConnId = kv.Key, Accept = false });
            }

            // And pending elevations: no window to say yes in means no.
            foreach (KeyValuePair<int, TaskCompletionSource<ElevationDecision>> kv in _elevations)
            {
                kv.Value.TrySetResult(new ElevationDecision { ConnId = kv.Key, Allow = false });
            }
        }
    }

    /// <summary>
    /// The account a shell would run as, so a terminal request can say "a root terminal" rather than "a
    /// terminal". Set by the engine from its terminal module; null where there is none.
    /// </summary>
    public Func<string>? TerminalIdentity { get; set; }

    /// <summary>The user's permission for the engine to share their desktop without asking, where the platform has one.</summary>
    public DeskPair.Platform.Abstractions.Capture.IDesktopSharingConsent? DesktopSharing { get; set; }

    // ---- IConnectionApprover ----

    public async Task<bool> RequestAsync(ConnectionSummary summary, CancellationToken ct)
    {
        if (_server is null)
        {
            return false;
        }

        if (!_server.HasClient(IpcRoles.ConnectionManager) && !await EnsureConnectionManagerAsync(ct).ConfigureAwait(false))
        {
            _log.LogWarning("Connection {Id} from {Peer} needs approval but no connection manager is running; rejecting", summary.ConnectionId, summary.PeerId);
            return false;
        }

        var tcs = new TaskCompletionSource<ApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        _approvals[summary.ConnectionId] = tcs;
        try
        {
            await _server.BroadcastAsync(new IpcMessage
            {
                ApprovalRequest = new ApprovalRequest
                {
                    ConnId = summary.ConnectionId,
                    PeerId = summary.PeerId,
                    PeerName = summary.PeerName,
                    PeerPlatform = summary.PeerPlatform,
                    ConnType = summary.ConnType,
                    TimeoutMs = (uint)_runtime!.Policy.ApprovalTimeout.TotalMilliseconds,
                    TerminalIdentity = summary.ConnType == Protocol.Rendezvous.ConnType.ConnTerminal ? TerminalIdentity?.Invoke() ?? string.Empty : string.Empty,
                },
            }, ct: ct).ConfigureAwait(false); // every UI sees it; only the connection manager may answer
            using CancellationTokenRegistration reg = ct.Register(() => tcs.TrySetCanceled(ct));
            ApprovalDecision decision = await tcs.Task.ConfigureAwait(false);
            HostSession? session = decision.Accept ? FindSession(summary.ConnectionId) : null;
            if (session is not null && decision.Granted.Count > 0)
            {
                foreach (Permission p in Enum.GetValues<Permission>())
                {
                    session.Context.Permissions.SetOverride(p, decision.Granted.Contains(p));
                }
            }

            // A shell is granted only by name. An acceptance that lists nothing -- or one from a connection
            // manager that has never heard of terminals -- otherwise leaves every permission at the policy,
            // and accepting a terminal has to be a deliberate act, not what happens by default.
            if (session is not null && !decision.Granted.Contains(Permission.PermTerminal))
            {
                session.Context.Permissions.SetOverride(Permission.PermTerminal, false);
            }

            return decision.Accept;
        }
        finally
        {
            _approvals.TryRemove(summary.ConnectionId, out _);
        }
    }

    /// <summary>
    /// Asks the person at the host whether to let a viewer see and drive the secure desktop, the same way a
    /// connection is approved. Wired to <see cref="Services.HostMediaModule.ElevationApprover"/>. No window to ask
    /// in means no, exactly as a connection with no connection manager is rejected.
    /// </summary>
    public async Task<Services.ElevationChoice> RequestElevationAsync(Services.ElevationAsk ask, CancellationToken ct)
    {
        if (_server is null)
        {
            return default;
        }

        if (!_server.HasClient(IpcRoles.ConnectionManager) && !await EnsureConnectionManagerAsync(ct).ConfigureAwait(false))
        {
            _log.LogWarning("Elevation for connection {Id} needs the person at the host, but no connection manager is running; refusing", ask.ConnectionId);
            return default;
        }

        // Phase 3's permanent-for-listed-devices offer: only where the machine has a permanent password (so the
        // unattended service can be installed), and the prompt says whether this device is already on the allowlist.
        bool canInstall = _runtime?.Passwords.HasPermanentPassword ?? false;
        bool listed = _applied.AllowedPeers.Any(entry => entry == "id:" + ask.PeerId);

        var tcs = new TaskCompletionSource<ElevationDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        _elevations[ask.ConnectionId] = tcs;
        try
        {
            await _server.BroadcastAsync(new IpcMessage
            {
                ElevationRequest = new ElevationRequest
                {
                    ConnId = ask.ConnectionId,
                    PeerId = ask.PeerId,
                    PeerName = ask.PeerName,
                    Listed = listed,
                    CanInstall = canInstall,
                },
            }, ct: ct).ConfigureAwait(false); // every UI sees it; only the connection manager may answer
            using CancellationTokenRegistration reg = ct.Register(() => tcs.TrySetCanceled(ct));
            ElevationDecision decision = await tcs.Task.ConfigureAwait(false);
            return new Services.ElevationChoice(decision.Allow, decision.Permanent);
        }
        finally
        {
            _elevations.TryRemove(ask.ConnectionId, out _);
        }
    }

    public void Notify(HostSessionEvent evt)
    {
        // Wait for a valid login before spawning a UI process: a port scan on the direct-access port opens
        // sockets, and that must not start anything.
        if (evt.Kind == HostEventKind.Identified && _server is not null && !_server.HasClient(IpcRoles.ConnectionManager))
        {
            _ = EnsureConnectionManagerAsync(CancellationToken.None); // warm it up while authentication finishes
        }

        HostSession? session = FindSession(evt.ConnectionId);
        IpcMessage? message = evt.Kind switch
        {
            HostEventKind.Opened => new IpcMessage
            {
                ConnectionOpened = new ConnectionOpened { ConnId = evt.ConnectionId, RemoteAddress = evt.Text ?? string.Empty, Authorized = false },
            },
            // The viewer identified itself: the main UI can name it in its "verifying" notice.
            HostEventKind.Identified when session is not null => new IpcMessage
            {
                ConnectionOpened = new ConnectionOpened
                {
                    ConnId = evt.ConnectionId,
                    PeerId = session.Context.Peer.Id,
                    PeerName = session.Context.Peer.Name,
                    RemoteAddress = session.Context.Peer.RemoteEndPoint?.ToString() ?? string.Empty,
                    ConnType = session.Context.ConnType,
                    Authorized = false,
                },
            },
            HostEventKind.Authorized when session is not null => new IpcMessage
            {
                ConnectionOpened = new ConnectionOpened
                {
                    ConnId = evt.ConnectionId,
                    PeerId = session.Context.Peer.Id,
                    PeerName = session.Context.Peer.Name,
                    PeerPlatform = session.Context.Peer.Platform,
                    RemoteAddress = session.Context.Peer.RemoteEndPoint?.ToString() ?? string.Empty,
                    ConnType = session.Context.ConnType,
                    Authorized = true,
                },
            },
            HostEventKind.Closed => new IpcMessage { ConnectionClosed = new ConnectionClosed { ConnId = evt.ConnectionId, Reason = evt.Text ?? string.Empty } },
            HostEventKind.ChatReceived => new IpcMessage { Chat = new ChatRelay { ConnId = evt.ConnectionId, Text = evt.Text ?? string.Empty, FromPeer = true } },
            HostEventKind.PermissionChanged when evt.Text is not null && evt.Text.Split('=') is [string name, string value] && Enum.TryParse(name, out Permission perm) =>
                new IpcMessage { PermissionChange = new PermissionChange { ConnId = evt.ConnectionId, Permission = perm, Enabled = bool.Parse(value) } },
            _ => null,
        };
        if (message is not null)
        {
            _ = Push(message);
        }
    }

    /// <summary>Launches the connection manager (once at a time) and waits for it to attach.</summary>
    private async Task<bool> EnsureConnectionManagerAsync(CancellationToken ct)
    {
        if (_server is null)
        {
            return false;
        }

        if (_server.HasClient(IpcRoles.ConnectionManager))
        {
            return true;
        }

        if (ConnectionManagerLauncher is null)
        {
            return false;
        }

        Task launch;
        lock (_approvals)
        {
            if (_launching is null || _launching.IsCompleted)
            {
                _launching = LaunchAsync();
            }

            launch = _launching;
        }

        try
        {
            await launch.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }

        return _server.HasClient(IpcRoles.ConnectionManager);

        async Task LaunchAsync()
        {
            try
            {
                await ConnectionManagerLauncher(ct).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _log.LogWarning(e, "Could not start the connection manager");
                return;
            }

            long deadline = Environment.TickCount64 + (long)ConnectionManagerStartTimeout.TotalMilliseconds;
            while (!_server.HasClient(IpcRoles.ConnectionManager) && Environment.TickCount64 < deadline)
            {
                await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private HostSession? FindSession(int connectionId) => _runtime?.Sessions.FirstOrDefault(s => s.Context.ConnectionId == connectionId);

    private static IpcMessage PasswordStateOf(HostRuntime runtime) => new()
    {
        PasswordState = new PasswordState
        {
            HasPermanent = runtime.Passwords.HasPermanentPassword,
            TemporaryEnabled = runtime.Passwords.TemporaryEnabled,
            TemporaryPassword = runtime.Passwords.TemporaryEnabled ? runtime.Passwords.TemporaryPassword : string.Empty,
            TemporaryLength = (uint)runtime.Passwords.TemporaryPasswordLength,
            TemporaryPinned = runtime.Passwords.TemporaryPinned,
            LinkPassword = runtime.Passwords.TemporaryEnabled ? runtime.Passwords.LinkPassword : string.Empty,
        },
    };

    private Task Push(IpcMessage message, string? role = null, IpcClientInfo? except = null) =>
        _server?.BroadcastAsync(message, role, except) ?? Task.CompletedTask;
}
