using System.Net;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using DeskPair.Protocol.Media;
using DeskPair.Core.Transport.Udp;
using DeskPair.Core.Session.Host.Auth;
using DeskPair.Core.Transport;
using DeskPair.Protocol;
using DeskPair.Protocol.Crypto;
using DeskPair.Protocol.Framing;
using DeskPair.Protocol.Messages;

namespace DeskPair.Core.Session.Host;

/// <summary>
/// One incoming connection: handshake → authentication (→ approval) → authorized message loop → close.
/// Message semantics after authorization live in the handlers registered on the runtime's dispatcher.
/// </summary>
public sealed class HostSession
{
    private readonly IPeerTransport _transport;
    private readonly HostRuntime _runtime;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<string> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private FramedStream? _stream;
    private SessionMessagePump? _pump;
    private CancellationTokenRegistration _runtimeCancellation;
    private MediaKeys? _mediaKeys;
    private UdpMediaChannel? _media;
    private TaskCompletionSource<MediaChannelAnswer>? _mediaAnswer;
    private uint _mediaChannelId;

    /// <summary>How the peer reached us (direct listener, LAN offer, punched TCP or relay).</summary>
    public TransportKind TransportKind => _transport.Kind;

    /// <summary>
    /// The address the allowlist judges this connection by: what the socket shows, except on a relayed
    /// connection where the socket is the relay's and only the rendezvous server knows the peer. Set by
    /// <see cref="HostRuntime.Accept"/>; null when nothing said.
    /// </summary>
    internal IPAddress? AllowlistAddress { get; set; }

    internal HostSession(int connectionId, IPeerTransport transport, HostRuntime runtime, ILogger log, TimeProvider time)
    {
        _transport = transport;
        _runtime = runtime;
        _log = log;
        _time = time;
        Context = new HostSessionContext(connectionId, runtime, SendAsync, TrySendVideo, CloseAsync);
    }

    /// <summary>Relay server (host[:port]) this session came through, if any; offered as a UDP relay candidate.</summary>
    public string RelayServer { get; internal set; } = string.Empty;

    /// <summary>The rendezvous server's permission to use <see cref="RelayServer"/>; handed to the controller with the media offer.</summary>
    public Protocol.Rendezvous.RelayTicket? RelayTicket { get; internal set; }

    /// <summary>Video frames delivered over the UDP media channel (diagnostics/tests).</summary>
    public long MediaFramesSent { get; private set; }

    public UdpMediaChannel? MediaChannel => _media;

    private bool TrySendVideo(Message frame)
    {
        if (_media is { IsReady: true } media && frame.UnionCase == Message.UnionOneofCase.VideoFrame && frame.VideoFrame.Frame is { } encoded)
        {
            VideoFrame vf = frame.VideoFrame;
            // A frame the pacer had to drop is not reported as a failure: the viewer sees the gap, and its own
            // rate-limited refresh request (only when the reference chain is really broken) decides on a keyframe.
            // Reporting it here would add a second keyframe request for the same loss.
            media.TrySendVideo(vf.Display, (byte)vf.Codec, encoded.Seq, encoded.PtsMs, encoded.Key, (int)vf.Width, (int)vf.Height, encoded.Data.Span, TimeSpan.FromSeconds(1.0 / 60));
            MediaFramesSent++;
            return true;
        }

        return _pump?.TrySendVideo(frame) ?? false;
    }

    public HostSessionContext Context { get; }

    public Task<string> Completion => _closed.Task;

    public async Task RunAsync(CancellationToken ct)
    {
        // Released explicitly in ShutdownAsync before _cts is disposed, so a late runtime shutdown
        // cannot cancel an already-disposed source.
        _runtimeCancellation = ct.Register(() => _cts.Cancel());
        CancellationToken token = _cts.Token;
        string reason = "closed";
        try
        {
            _stream = new FramedStream(_transport.Stream, FramedStreamOptions.Peer, _time);
            IPAddress source = (_transport.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;
            Context.Peer = Context.Peer with { RemoteEndPoint = _transport.RemoteEndPoint };
            _runtime.Approver.Notify(new HostSessionEvent(Context.ConnectionId, HostEventKind.Opened, source.ToString()));

            await HandshakeAsync(token).ConfigureAwait(false);
            if (!await AuthenticateAsync(source, token).ConfigureAwait(false))
            {
                reason = "login failed";
                return;
            }

            reason = await RunAuthorizedAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            reason = _closed.Task.IsCompletedSuccessfully ? _closed.Task.Result : "cancelled";
        }
        catch (HandshakeException e)
        {
            _log.LogWarning("Handshake with {Remote} failed: {Reason}", _transport.RemoteEndPoint, e.Message);
            reason = "handshake failed";
        }
        catch (Exception e) when (e is ProtocolException or IOException or InvalidProtocolBufferException)
        {
            _log.LogDebug(e, "Session {Id} ended with a transport error", Context.ConnectionId);
            reason = e.Message;
        }
        catch (Exception e)
        {
            _log.LogError(e, "Session {Id} crashed", Context.ConnectionId);
            reason = "internal error";
        }
        finally
        {
            await ShutdownAsync(reason).ConfigureAwait(false);
        }
    }

    private async Task HandshakeAsync(CancellationToken ct)
    {
        Context.State = HostSessionState.Handshake;
        Message hello = await ReceiveAsync(ProtocolConstants.HelloTimeout, ct).ConfigureAwait(false)
            ?? throw new ProtocolException("Peer closed before hello.");
        if (hello.UnionCase != Message.UnionOneofCase.ControllerHello)
        {
            throw new HandshakeException($"Expected ControllerHello, got {hello.UnionCase}.");
        }

        HostHello reply;
        SessionKeys keys;
        try
        {
            (reply, keys) = Handshake.RespondHost(hello.ControllerHello, _runtime.Identity.Id, _runtime.Identity.Key, _runtime.Settings.Version);
        }
        catch (HandshakeVersionException e)
        {
            // Owed an answer: the hello that says which side has to update. Sent in plaintext, since no
            // keys exist, and the controller reads it before it would have checked a signature.
            await _stream!.SendAsync(new Message { HostHello = e.Refusal }, ct).ConfigureAwait(false);
            throw;
        }

        await _stream!.SendAsync(new Message { HostHello = reply }, ct).ConfigureAwait(false);
        _stream.EnableEncryption(keys);
        _mediaKeys = keys.ExportMediaKeys();
        keys.Dispose();
    }

    private async Task<bool> AuthenticateAsync(IPAddress source, CancellationToken ct)
    {
        Context.State = HostSessionState.Authenticating;
        HostPolicy policy = _runtime.Policy;
        byte[] challenge = PasswordProof.NewChallenge();
        await _stream!.SendAsync(new Message
        {
            AuthChallenge = new AuthChallenge
            {
                Salt = ByteString.CopyFrom(_runtime.Passwords.Salt),
                Challenge = ByteString.CopyFrom(challenge),
                ApproveMode = policy.ApproveMode,
                RequiresTotp = false,
                Kdf = _runtime.Passwords.Kdf,
                KdfIterations = (uint)_runtime.Passwords.Iterations,
            },
        }, ct).ConfigureAwait(false);

        for (int attempt = 1; attempt <= policy.MaxLoginAttemptsPerConnection; attempt++)
        {
            Message msg = await ReceiveAsync(ProtocolConstants.LoginTimeout, ct).ConfigureAwait(false)
                ?? throw new ProtocolException("Peer closed during login.");
            if (msg.UnionCase == Message.UnionOneofCase.Misc && msg.Misc.UnionCase == Misc.UnionOneofCase.CloseReason)
            {
                return false;
            }

            if (msg.UnionCase != Message.UnionOneofCase.LoginRequest)
            {
                throw new ProtocolException($"Expected LoginRequest, got {msg.UnionCase}.");
            }

            LoginRequest login = msg.LoginRequest;
            Context.Peer = new PeerDescriptor(login.MyId, login.MyName, login.MyPlatform, login.Version, _transport.RemoteEndPoint);
            _runtime.Approver.Notify(new HostSessionEvent(Context.ConnectionId, HostEventKind.Identified));
            Context.ConnType = login.ConnType;
            Context.Options = login.Options ?? new SessionOptions();
            Context.PeerMedia = login.Media;

            // The other half of the allowlist. The first half ran before the handshake and could only see an
            // address; now the peer has named itself, an "id:" entry can be honoured -- and a list of ids
            // alone, which had to let every address through to get this far, can finally refuse.
            if (!_runtime.Allowlist.Allows(AllowlistAddress, login.MyId))
            {
                _log.LogInformation("Peer {Peer} from {Address} is not in the allowlist", login.MyId, AllowlistAddress?.ToString() ?? "an unknown address");
                await SendLoginErrorAsync(LoginError.Types.Code.RejectedByUser, "This computer does not accept connections from you.", 0, ct).ConfigureAwait(false);
                return false;
            }

            TimeSpan blocked = _runtime.FailureTracker.CheckBlocked(source);
            if (blocked > TimeSpan.Zero)
            {
                await SendLoginErrorAsync(LoginError.Types.Code.TooManyAttempts, "Too many failed attempts.", (uint)blocked.TotalMilliseconds, ct).ConfigureAwait(false);
                return false;
            }

            PasswordMatch match = login.PasswordProof.IsEmpty ? PasswordMatch.None : _runtime.Passwords.Verify(challenge, login.PasswordProof.Span, LoginFailureTracker.BucketOf(source));
            bool passwordOk = match != PasswordMatch.None;
            bool needsClick = policy.ApproveMode switch
            {
                ApproveMode.ApprovePassword => false,
                ApproveMode.ApproveClick => !(passwordOk && policy.AllowPasswordInClickMode),
                ApproveMode.ApproveBoth => true,
                _ => true,
            };
            bool needsPassword = policy.ApproveMode != ApproveMode.ApproveClick;

            if (needsPassword && !passwordOk)
            {
                _runtime.FailureTracker.RecordFailure(source);
                _log.LogWarning("Wrong password from {Peer} ({Remote}), attempt {Attempt}", login.MyId, source, attempt);
                await Task.Delay(policy.FailedLoginDelay, _time, ct).ConfigureAwait(false);
                await SendLoginErrorAsync(LoginError.Types.Code.WrongPassword, "Wrong password.", 0, ct).ConfigureAwait(false);
                continue;
            }

            if (needsClick)
            {
                await _stream.SendAsync(new Message { LoginResponse = new LoginResponse { WaitingForApproval = true } }, ct).ConfigureAwait(false);
                Context.State = HostSessionState.AwaitingApproval;
                bool accepted;
                using (var approvalCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    approvalCts.CancelAfter(policy.ApprovalTimeout);
                    try
                    {
                        accepted = await _runtime.Approver.RequestAsync(Context.Summary, approvalCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        await SendLoginErrorAsync(LoginError.Types.Code.ApprovalTimeout, "The remote user did not respond.", 0, ct).ConfigureAwait(false);
                        return false;
                    }
                }

                if (!accepted)
                {
                    await SendLoginErrorAsync(LoginError.Types.Code.RejectedByUser, "The remote user rejected the connection.", 0, ct).ConfigureAwait(false);
                    return false;
                }

                Context.AuthenticatedWith = PasswordMatchKind.Approval;
            }
            else
            {
                Context.AuthenticatedWith = match == PasswordMatch.Permanent ? PasswordMatchKind.Permanent : PasswordMatchKind.Temporary;
            }

            if (match == PasswordMatch.Link)
            {
                // The QR code has now been used, so it stops working: a new link password is issued and the
                // code on the desk redraws itself. Here rather than in Verify, so that a scan the user then
                // refuses at the prompt does not spend the code they are holding up for somebody else.
                _runtime.Passwords.SpendLink();
            }

            _runtime.FailureTracker.RecordSuccess(source);
            await AuthorizeAsync(ct).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    private async Task AuthorizeAsync(CancellationToken ct)
    {
        Context.Permissions.ApplyOptions(Context.Options);

        // The options sent at login used to reach the permission set and nothing else, so anything keyed off
        // them — the encoder's quality, whether this viewer wants cursor positions — ran on defaults until the
        // viewer happened to change a setting.
        _runtime.OptionsUpdated?.Invoke(Context.ConnectionId, Context.Options);
        HostPolicy policy = _runtime.Policy;
        var info = new PeerInfo
        {
            Username = policy.UserName,
            Hostname = policy.HostName,
            Platform = policy.Platform,
            Version = _runtime.Settings.Version,
            CurrentDisplay = 0,
            Encoding = _runtime.EncodingProvider?.Invoke() ?? new SupportedEncoding(),
            MultiDisplay = _runtime.SupportsMultiDisplay,
        };
        info.Displays.AddRange(_runtime.DescribeDisplays());
        info.Granted.AddRange(Context.Permissions.Granted);

        await _stream!.SendAsync(new Message { LoginResponse = new LoginResponse { PeerInfo = info } }, ct).ConfigureAwait(false);
        foreach (Permission p in Enum.GetValues<Permission>())
        {
            await _stream.SendAsync(new Message { Misc = new Misc { PermissionInfo = new PermissionInfo { Permission = p, Enabled = Context.Permissions.Has(p) } } }, ct).ConfigureAwait(false);
        }

        Context.State = HostSessionState.Authorized;
        _log.LogInformation("Session {Id}: {Peer} ({Name}) authorized via {How}, type {Type}", Context.ConnectionId, Context.Peer.Id, Context.Peer.Name, Context.AuthenticatedWith, Context.ConnType);
        _runtime.Approver.Notify(new HostSessionEvent(Context.ConnectionId, HostEventKind.Authorized, Context.Peer.Id));
    }

    private async Task<string> RunAuthorizedAsync(CancellationToken ct)
    {
        _pump = new SessionMessagePump(_stream!, _log, _time);
        _pump.Start();
        Context.Permissions.Changed += OnPermissionChanged;
        await _runtime.OnSessionAuthorizedAsync(this, ct).ConfigureAwait(false);
        Context.MediaMisc = OnMediaMiscAsync;
        if (_runtime.Settings.UdpMedia && Context.ConnType == Protocol.Rendezvous.ConnType.ConnRemote && Context.PeerMedia?.Udp == true && _mediaKeys is not null)
        {
            _ = OfferMediaAsync(ct);
        }

        Task delayProbe = TestDelayLoopAsync(ct);
        try
        {
            await foreach (Message message in _pump.Incoming.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (!_runtime.Scope.IsAllowed(Context, message))
                {
                    _log.LogWarning("Session {Id}: {Case} not allowed in state {State}/{Type}; closing", Context.ConnectionId, DescribeCase(message), Context.State, Context.ConnType);
                    await CloseAsync("scope violation").ConfigureAwait(false);
                    break;
                }

                await _runtime.Dispatcher.DispatchAsync(Context, message, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            Context.Permissions.Changed -= OnPermissionChanged;
            await _runtime.OnSessionClosingAsync(this).ConfigureAwait(false);
        }

        string reason = await _pump.Completion.ConfigureAwait(false);
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await delayProbe.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        return reason;
    }

    private async Task TestDelayLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(ProtocolConstants.TestDelayInterval, _time);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            await SendAsync(new Message { TestDelay = new TestDelay { TimeMs = _time.GetUtcNow().ToUnixTimeMilliseconds(), FromController = false, LastDelayMs = (uint)LastRoundTrip.TotalMilliseconds } }, MessagePriority.Control, ct).ConfigureAwait(false);
        }
    }

    public TimeSpan LastRoundTrip { get; internal set; }

    // ---------------------------------------------------------------- UDP media channel

    private async Task OfferMediaAsync(CancellationToken ct)
    {
        UdpDatagramSocket? socket = null;
        UdpMediaChannel? channel = null;
        try
        {
            socket = UdpDatagramSocket.Create();
            List<MediaCandidate> candidates = await CandidateGatherer.GatherAsync(socket, _runtime.Settings.RendezvousServer, TimeSpan.FromMilliseconds(600), ct).ConfigureAwait(false);
            uint channelId = ++_mediaChannelId;
            channel = new UdpMediaChannel(socket, _mediaKeys!, channelId, isHost: true, _log, _time);
            // With a ticket the token is derived from it, so the relay can tie the bind to the permission
            // it came with; without one (a direct connection with no rendezvous) it is random as before.
            byte[] token = RelayTicket is { } ticket
                ? RelayTickets.UdpToken(ticket)
                : System.Security.Cryptography.RandomNumberGenerator.GetBytes(RelayDatagram.TokenBytes);
            var answer = new TaskCompletionSource<MediaChannelAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
            _mediaAnswer = answer;
            channel.PathVerified += path =>
            {
                Context.MediaPath = path;
                _ = SendAsync(new Message { Misc = new Misc { MediaReady = new MediaChannelReady { ChannelId = channelId, Path = path.Kind, Remote = CandidateGatherer.Candidate(path.Remote, path.Kind, 0).Addr } } }, MessagePriority.Control, CancellationToken.None);
            };
            channel.Dead += reason => _ = CloseMediaAsync($"channel dead: {reason}", notifyPeer: true);
            long lastStats = _time.GetTimestamp();
            channel.FeedbackReceived += (feedback, rtt) =>
            {
                if (_runtime.MediaBitrateCeilingBps?.Invoke(Context.ConnectionId) is > 0 and var ceiling)
                {
                    channel.BandwidthCeilingBps = ceiling;
                }

                // A viewer watching several displays reports each one, but the link's numbers once: the other
                // reports carry copies that would otherwise be counted again.
                bool link = feedback.CarriesLinkFields;
                if (link && rtt is { } sample)
                {
                    _runtime.RecordRoundTrip(Context.ConnectionId, sample);
                }

                if (link && _time.GetElapsedTime(lastStats) >= TimeSpan.FromSeconds(5))
                {
                    lastStats = _time.GetTimestamp();
                    Qos.Gcc.GccController gcc = channel.Gcc;
                    _log.LogInformation("Session {Id}: udp {Kind} sent {Frames} frames / {MB:F1} MB, dropped {Dropped} at the pacer, viewer loss {Loss}‰ given up {GivenUp} recovered {Recovered}, rtt {Rtt:F0} ms, refresh requests {Refreshes}, gcc {Target:F2} Mb/s {State} (delay {Delay:F2}, loss {LossBased:F2}, acked {Acked:F2} Mb/s, ceiling {Ceiling:F1}, probes {Probes}, last probe {LastProbe})",
                        Context.ConnectionId, channel.Path?.Kind, channel.FramesSent, channel.BytesSent / 1e6, channel.FramesDropped, feedback.LossPermille, feedback.FramesGivenUp, feedback.ShardsRecovered, channel.LastRttMs, Context.RefreshRequests,
                        gcc.TargetBps / 1e6, gcc.State, gcc.DelayBasedBps / 1e6, Math.Min(gcc.LossBasedBps, 999e6) / 1e6, (gcc.AckedBps ?? 0) / 1e6, channel.BandwidthCeilingBps / 1e6, channel.ProbesSent, channel.LastProbe);
                }

                _runtime.MediaFeedbackReported?.Invoke(Context.ConnectionId, feedback.Stream, feedback.HighestDecodableFrameSeq, feedback.FramesGivenUp, link ? feedback.LossPermille / 1000.0 : null);
                if (link)
                {
                    channel.Planner.ReportLoss(feedback.LossPermille / 1000.0);
                }
            };
            channel.BandwidthEstimated += bps => _runtime.BandwidthEstimated?.Invoke(Context.ConnectionId, bps);
            channel.Start();
            _media = channel;

            var offer = new MediaChannelOffer
            {
                ChannelId = channelId,
                RelayServer = RelayServer,
                RelayToken = ByteString.CopyFrom(token),
                RelayTicket = RelayTicket,
                TimeoutMs = (uint)ProtocolConstants.MediaOfferTimeout.TotalMilliseconds,
                KeepaliveMs = (uint)ProtocolConstants.MediaKeepalive.TotalMilliseconds,
            };
            offer.Candidates.AddRange(candidates);
            await SendAsync(new Message { Misc = new Misc { MediaOffer = offer } }, MessagePriority.Control, ct).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ProtocolConstants.MediaOfferTimeout);
            MediaChannelAnswer reply = await answer.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            if (!reply.Accept || reply.ChannelId != channelId)
            {
                await CloseMediaAsync("controller declined", notifyPeer: false).ConfigureAwait(false);
                return;
            }

            IPEndPoint? relay = await ResolveRelayAsync(RelayServer, ct).ConfigureAwait(false);
            channel.StartProbing(reply.Candidates, relay, relay is null ? null : token, RelayTicket);
            await Task.Delay(ProtocolConstants.MediaOfferTimeout, _time, ct).ConfigureAwait(false);
            if (channel.Path is null)
            {
                await CloseMediaAsync("no UDP path verified", notifyPeer: true).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            if (!ct.IsCancellationRequested)
            {
                _log.LogInformation("Session {Id}: media offer timed out; staying on TCP", Context.ConnectionId);
            }

            await CloseMediaAsync("offer timed out", notifyPeer: false).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Session {Id}: UDP media channel failed; staying on TCP", Context.ConnectionId);
            await CloseMediaAsync("error", notifyPeer: false).ConfigureAwait(false);
            if (channel is null)
            {
                socket?.Dispose();
            }
        }
    }

    internal static async Task<IPEndPoint?> ResolveRelayAsync(string relayServer, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(relayServer))
        {
            return null;
        }

        try
        {
            (string host, int port) = TcpConnector.ParseHostPort(relayServer, ProtocolConstants.RelayUdpPort);
            IPAddress[] addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            IPAddress? ip = addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
            return ip is null ? null : new IPEndPoint(ip, port);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private ValueTask OnMediaMiscAsync(Misc misc)
    {
        switch (misc.UnionCase)
        {
            case Misc.UnionOneofCase.MediaAnswer:
                _mediaAnswer?.TrySetResult(misc.MediaAnswer);
                break;
            case Misc.UnionOneofCase.MediaReady:
                if (_media is { } m && misc.MediaReady.ChannelId == m.ChannelId)
                {
                    m.MarkPeerReady();
                    _log.LogInformation("Session {Id}: video now on UDP ({Kind})", Context.ConnectionId, m.Path?.Kind);
                }

                break;
            case Misc.UnionOneofCase.MediaClose:
                return new ValueTask(CloseMediaAsync($"peer: {misc.MediaClose.Reason}", notifyPeer: false));
        }

        return ValueTask.CompletedTask;
    }

    private async Task CloseMediaAsync(string reason, bool notifyPeer)
    {
        UdpMediaChannel? channel = Interlocked.Exchange(ref _media, null);
        Context.MediaPath = null;
        if (channel is null)
        {
            return;
        }

        _log.LogInformation("Session {Id}: UDP media channel closed ({Reason}); video back on TCP", Context.ConnectionId, reason);
        _runtime.MediaChannelClosed?.Invoke(Context.ConnectionId);
        if (notifyPeer && Context.State == HostSessionState.Authorized)
        {
            try
            {
                await SendAsync(new Message { Misc = new Misc { MediaClose = new MediaChannelClose { ChannelId = channel.ChannelId, Reason = reason } } }, MessagePriority.Control, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        channel.SendClose(reason);
        await channel.DisposeAsync().ConfigureAwait(false);
    }

    private void OnPermissionChanged(Permission permission, bool enabled)
    {
        _ = SendAsync(new Message { Misc = new Misc { PermissionInfo = new PermissionInfo { Permission = permission, Enabled = enabled } } }, MessagePriority.Control, CancellationToken.None);
        _runtime.Approver.Notify(new HostSessionEvent(Context.ConnectionId, HostEventKind.PermissionChanged, $"{permission}={enabled}"));
    }

    private ValueTask SendAsync(Message message, MessagePriority priority, CancellationToken ct)
    {
        if (_pump is not null)
        {
            return _pump.SendAsync(message, priority, ct);
        }

        return _stream is null ? ValueTask.CompletedTask : _stream.SendAsync(message, ct);
    }

    public async Task CloseAsync(string reason)
    {
        if (!_closed.TrySetResult(reason))
        {
            return;
        }

        Context.State = HostSessionState.Closing;
        if (_pump is not null)
        {
            await _pump.CloseAsync(reason).ConfigureAwait(false);
        }

        try
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Closing the pump let the session run to its end meanwhile, and its shutdown disposes this source: there is
            // nothing left to cancel. It threw out of HostRuntime.DisposeAsync instead, and the sessions after this one
            // were not closed (a CI run, as a test bed went down).
        }
    }

    private async Task ShutdownAsync(string reason)
    {
        _closed.TrySetResult(reason);
        Context.State = HostSessionState.Closing;
        if (_stream is not null && _stream.IsEncrypted && Context.State != HostSessionState.Closed)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _stream.SendAsync(new Message { Misc = new Misc { CloseReason = new CloseReason { Reason = reason } } }, cts.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        await CloseMediaAsync(reason, notifyPeer: false).ConfigureAwait(false);
        _mediaKeys?.Dispose();
        if (_pump is not null)
        {
            await _pump.DisposeAsync().ConfigureAwait(false);
        }

        if (_stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        else
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
        }

        Context.State = HostSessionState.Closed;
        _log.LogInformation("Session {Id} closed: {Reason}", Context.ConnectionId, reason);
        _runtime.Approver.Notify(new HostSessionEvent(Context.ConnectionId, HostEventKind.Closed, reason));
        _runtime.RaiseSessionEnded(this, reason);
        await _runtimeCancellation.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }

    private async Task<Message?> ReceiveAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using Frame? frame = await _stream!.ReceiveAsync(cts.Token).ConfigureAwait(false);
            return frame is null ? null : Message.Parser.ParseFrom(frame.Payload.Span);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ProtocolException("Timed out waiting for the peer.");
        }
    }

    private Task SendLoginErrorAsync(LoginError.Types.Code code, string message, uint retryAfterMs, CancellationToken ct) =>
        _stream!.SendAsync(new Message { LoginResponse = new LoginResponse { Error = new LoginError { Code = code, Message = message, RetryAfterMs = retryAfterMs } } }, ct).AsTask();

    private static string DescribeCase(Message m) => m.UnionCase == Message.UnionOneofCase.Misc ? $"Misc.{m.Misc.UnionCase}" : m.UnionCase.ToString();
}
