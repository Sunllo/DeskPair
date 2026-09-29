using Google.Protobuf;
using Microsoft.Extensions.Logging;
using DeskPair.Protocol.Media;
using DeskPair.Core.Transport.Udp;
using System.Net;
using DeskPair.Core.Audio;
using DeskPair.Core.Clipboard;
using DeskPair.Core.FileTransfer;
using DeskPair.Core.Services;
using DeskPair.Core.Transport;
using DeskPair.Core.Video;
using DeskPair.Platform.Abstractions.Audio;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Abstractions.Clipboard;
using DeskPair.Platform.Abstractions.Codec;
using DeskPair.Protocol;
using DeskPair.Protocol.Crypto;
using DeskPair.Protocol.Framing;
using DeskPair.Protocol.Messages;
using DeskPair.Protocol.Rendezvous;

namespace DeskPair.Core.Session.Controller;

public enum ControllerSessionState
{
    Disconnected,
    Connecting,
    AwaitingLogin,
    LoggingIn,
    AwaitingApproval,
    Authorized,
    Closed,
}

public sealed record LoginResult(bool Success, LoginError? Error, PeerInfo? PeerInfo)
{
    public static LoginResult Ok(PeerInfo info) => new(true, null, info);
    public static LoginResult Failed(LoginError error) => new(false, error, null);
}

/// <summary>UI-facing notifications; every call arrives on a pump thread, marshal to the UI as needed.</summary>
public interface IControllerCallbacks
{
    void OnStateChanged(ControllerSessionState state);

    void OnPeerInfo(PeerInfo info);

    void OnChat(ChatMessage message);

    void OnPermission(PermissionInfo info);

    void OnClosed(string reason);

    void OnRoundTrip(TimeSpan rtt);

    /// <summary>
    /// The host has gone quiet, or has started answering again. Not a close: the session is still open and
    /// still has most of its read timeout left to recover in, and most stalls do recover. It is what lets
    /// the window say "waiting" instead of showing a frozen picture and no explanation.
    /// </summary>
    void OnStalled(bool stalled)
    {
    }

    /// <summary>Raised for every received video packet before decoding.</summary>
    void OnVideoPacket(int display, uint seq, bool isKeyFrame)
    {
    }

    /// <summary>A decoded frame; valid only for the duration of the call.</summary>
    void OnVideoFrame(int display, in DecodedFrame frame)
    {
    }

    /// <summary>
    /// The encoded frame exactly as it arrived, before decoding; valid only for the duration of the call.
    /// Recording uses this so the file costs no re-encoding.
    /// </summary>
    void OnVideoEncoded(int display, ReadOnlySpan<byte> frame, bool key, long ptsMs, int width, int height, Platform.Abstractions.Codec.VideoCodec codec)
    {
    }

    /// <summary>Decoded interleaved samples in the last announced format; valid only for the duration of the call.</summary>
    void OnAudioPcm(ReadOnlySpan<float> interleaved, long ptsMs)
    {
    }

    void OnCursorShape(CursorData shape)
    {
    }

    void OnCursorId(ulong id)
    {
    }

    void OnCursorPosition(CursorPosition position)
    {
    }

    void OnDisplaySwitched(int display)
    {
    }

    /// <summary>
    /// The host's answer to <see cref="ControllerSession.SubscribeDisplaysAsync"/>: the displays it now streams
    /// to this viewer and the focus, or -- with <c>Failure</c> set -- the set it kept and why it refused.
    /// </summary>
    void OnDisplaySubscription(DisplaySubscription subscription)
    {
    }

    /// <summary>
    /// The host's displays changed shape -- somebody (perhaps this viewer) switched a resolution. The
    /// session's <see cref="ControllerSession.PeerInfo"/> is already updated; <c>Failure</c> is set when this
    /// viewer's own request was refused, and says why.
    /// </summary>
    void OnDisplaysChanged(DisplaysChanged info)
    {
    }

    void OnAudioFormat(AudioStreamFormat format)
    {
    }

    /// <summary>
    /// A terminal opened, produced output, ended or failed. Output is acknowledged to the host by the
    /// session as soon as this returns, so a slow consumer should keep its own buffer rather than block here.
    /// </summary>
    void OnTerminal(TerminalResponse response)
    {
    }

    void OnMessage(Message message)
    {
    }
}

public sealed record ControllerSessionOptions(string MyId, string MyName, string MyPlatform, string Version, ConnType ConnType = ConnType.ConnRemote)
{
    public SessionOptions SessionOptions { get; init; } = new();

    /// <summary>Decoders for incoming video; null leaves frames undecoded (they are still acknowledged).</summary>
    public IVideoDecoderFactory? Decoders { get; init; }

    /// <summary>Sink for decoded audio; null discards audio.</summary>
    public IAudioPlayback? AudioPlayback { get; init; }

    /// <summary>Local file system for transfers; defaults to the real one.</summary>
    public IFileSystem? FileSystem { get; init; }

    /// <summary>Local clipboard to keep in sync with the host; null disables clipboard sync.</summary>
    public IClipboard? Clipboard { get; init; }

    public IFileTransferPolicy? FileTransferPolicy { get; init; }

    /// <summary>Where files fetched for a paste are kept. The default is the per-user cache location.</summary>
    public ClipboardStaging? ClipboardStaging { get; init; }

    /// <summary>Advertise support for lossless tile patches (RDP-style refinement); on by default.</summary>
    public bool LosslessTiles { get; init; } = true;

    /// <summary>
    /// The codec to ask the host for. Only a request: the host encodes it if it can and if every other viewer
    /// on that display can decode it. Null asks for nothing and lets the host choose.
    /// </summary>
    public Platform.Abstractions.Codec.VideoCodec? PreferredCodec { get; init; }

    /// <summary>Accept a UDP media channel (video with FEC) when the host offers one; on by default.</summary>
    public bool UdpMedia { get; init; } = true;

    /// <summary>Tests: fraction of incoming media datagrams to drop, and the seed for reproducibility.</summary>
    public double MediaDebugLoss { get; init; }

    public int MediaDebugLossSeed { get; init; } = 1;

    /// <summary>Tests: wraps the media socket after candidate gathering (e.g. a simulated bottleneck).</summary>
    public Func<IDatagramSocket, IDatagramSocket>? MediaSocketWrapper { get; init; }
}

/// <summary>Controller side of one session: connect, handshake, log in, then pump messages to callbacks.</summary>
public sealed class ControllerSession : IAsyncDisposable
{
    private readonly ControllerSessionOptions _options;
    private readonly IControllerCallbacks _callbacks;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<string> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<int, (Platform.Abstractions.Codec.VideoCodec Codec, IVideoDecoder Decoder)> _decoders = new();

    /// <summary>Codecs this machine has already been found unable to decode, so it is logged once, not per frame.</summary>
    private readonly HashSet<Platform.Abstractions.Codec.VideoCodec> _undecodable = [];
    private readonly Dictionary<int, FrameCompositor> _compositors = new();
    private readonly VideoQueue _videoQueue = new();
    private Task? _videoLoop;
    private MediaKeys? _mediaKeys;
    private UdpMediaChannel? _media;
    private string _relayServer = string.Empty;
    private RelayTicket? _relayTicket;
    private string _rendezvousServer = string.Empty;
    /// <summary>
    /// Per display, the newest lossless tile patch: a video frame older than it is decoded but not shown. Per
    /// display because each display numbers its frames from one; written on the video thread, cleared on the
    /// message thread when a display starts over.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, uint> _lastTileSeq = new();

    /// <summary>
    /// Set once this viewer has asked for a set of displays. From then on the host's SwitchDisplay only says
    /// which display has the focus; letting it narrow the streams back to one would black out the others.
    /// </summary>
    private bool _speaksSubscription;

    /// <summary>The displays the host last confirmed streaming, for a UDP channel that opens afterwards.</summary>
    private byte[]? _subscribed;
    /// <summary>
    /// How many frames in a row a decoder may swallow before we decide the stream is undecodable. A
    /// hardware decoder buffers a frame or two by design, so this is comfortably above that.
    /// </summary>
    private const int DecoderSilenceLimit = 12;

    private readonly HashSet<int> _awaitingKeyFrame = new();

    /// <summary>
    /// Frames in a row a display's decoder has swallowed without handing one back. A pipelined decoder
    /// (Media Foundation with DXVA, VideoToolbox) legitimately buffers input and answers "need more input"
    /// for a frame or two, so a single empty answer is not a failure; only a run of them means the stream
    /// really is undecodable and a keyframe is worth asking for.
    /// </summary>
    private readonly Dictionary<int, int> _decoderSilent = new();
    private long _lastTileRefresh;
    private uint _pendingPass;
    private long _pendingPassSince;
    private int _lastVideoDisplay;
    private IPeerTransport? _transport;
    private FramedStream? _stream;
    private SessionMessagePump? _pump;
    private AuthChallenge? _challenge;
    private Task? _loop;
    private ControllerSessionState _state;
    private OpusDecoderWrapper? _audioDecoder;
    private FileTransferEngine? _files;
    private readonly ClipboardSync _clipboardSync = new();
    private Task? _clipboardLoop;
    private bool _clipboardEnabled = true;

    // The host says so if file transfer is off; until it does, assume it is on, as with the clipboard.
    private bool _fileEnabled = true;
    private readonly ClipboardPromiseRouter _promises;
    private readonly LocalFileOffer _offer;

    public ControllerSession(ControllerSessionOptions options, IControllerCallbacks callbacks, ILogger log, TimeProvider? time = null)
    {
        _options = options;
        _callbacks = callbacks;
        _log = log;
        _time = time ?? TimeProvider.System;
        // The default staging root is shared with the in-process engine, which sweeps anything a crashed run
        // left behind when it starts; there is nothing for a session to sweep on its own.
        _promises = new ClipboardPromiseRouter(options.ClipboardStaging ?? new ClipboardStaging(log), log);
        _offer = new LocalFileOffer(log);
    }

    public ControllerSessionState State
    {
        get => _state;
        private set
        {
            if (_state != value)
            {
                _state = value;
                _callbacks.OnStateChanged(value);
            }
        }
    }

    public PeerInfo? PeerInfo { get; private set; }

    public AuthChallenge? Challenge => _challenge;

    public TransportKind? TransportKind => _transport?.Kind;

    public Task<string> Completion => _closed.Task;

    public long VideoFramesReceived { get; private set; }

    public long VideoFramesDecoded { get; private set; }


    public long TileUpdatesReceived { get; private set; }


    public long TilesReceived { get; private set; }

    public long VideoBytesReceived { get; private set; }

    public long TileBytesReceived { get; private set; }

    /// <summary>Video frames discarded because decoding fell behind the network; each one costs a keyframe request.</summary>
    public long VideoFramesDroppedLate { get; private set; }

    /// <summary>Video frames that arrived over the UDP media channel.</summary>
    public long VideoFramesReceivedUdp { get; private set; }

    /// <summary>Tile updates refused because the picture they would patch is not intact (lost frame, decode failure).</summary>
    public long TilesRejectedBrokenReference { get; private set; }

    public long KeyFramesReceived { get; private set; }

    /// <summary>Keyframe requests this side sent (decode failures, lost frames, transport changes).</summary>
    public long RefreshRequests { get; private set; }

    /// <summary>The UDP media path in use, or null while video rides TCP.</summary>
    public MediaPath? MediaPath => _media is { IsDead: false } m ? m.Path : null;

    public UdpMediaChannel? MediaChannel => _media;

    /// <summary>Smoothed decode + composite + present time per frame, milliseconds.</summary>
    public double DecodeMs { get; private set; }

    /// <summary>Frames handed to the viewer per second over the last five-second window.</summary>
    public double DecodedFps { get; private set; }

    private long _decodeWindowStart;
    private int _decodeWindowCount;
    private double _decodeWindowMs;

    public long AudioFramesReceived { get; private set; }

    /// <summary>File transfer for this session; available once authorized.</summary>
    public FileTransferEngine Files => _files ?? throw new InvalidOperationException("Not authorized yet.");

    /// <summary>Connects and runs the handshake; afterwards <see cref="Challenge"/> describes how to log in.</summary>
    public async Task ConnectAsync(PeerConnector connector, string target, CancellationToken ct)
    {
        State = ControllerSessionState.Connecting;
        PeerConnection connection = await connector.ConnectAsync(target, _options.MyId, _options.ConnType, ct).ConfigureAwait(false);
        _transport = connection.Transport;
        _relayServer = connection.RelayServer;
        _relayTicket = connection.RelayTicket;
        _rendezvousServer = connection.RendezvousServer;
        _stream = new FramedStream(_transport.Stream, FramedStreamOptions.Peer, _time);
        try
        {
            (ControllerHello hello, Handshake.ControllerState hs) = Handshake.BeginController(_options.Version);
            using (hs)
            {
                await _stream.SendAsync(new Message { ControllerHello = hello }, ct).ConfigureAwait(false);
                Message reply = await ReceiveAsync(ProtocolConstants.HelloTimeout, ct).ConfigureAwait(false)
                    ?? throw new ProtocolException("Host closed during handshake.");
                if (reply.UnionCase != Message.UnionOneofCase.HostHello)
                {
                    throw new HandshakeException($"Expected HostHello, got {reply.UnionCase}.");
                }

                using SessionKeys keys = Handshake.FinishController(reply.HostHello, hs, connection.Verifier);
                _stream.EnableEncryption(keys);
                _mediaKeys = keys.ExportMediaKeys();
            }

            Message challenge = await ReceiveAsync(ProtocolConstants.HelloTimeout, ct).ConfigureAwait(false)
                ?? throw new ProtocolException("Host closed before the challenge.");
            if (challenge.UnionCase != Message.UnionOneofCase.AuthChallenge)
            {
                throw new ProtocolException($"Expected AuthChallenge, got {challenge.UnionCase}.");
            }

            _challenge = challenge.AuthChallenge;
            State = ControllerSessionState.AwaitingLogin;
        }
        catch
        {
            await CloseAsync("connect failed").ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Sends a login with the given password (null for click-approval only) and waits for the verdict.</summary>
    public async Task<LoginResult> LoginAsync(string? password, CancellationToken ct)
    {
        if (State is not (ControllerSessionState.AwaitingLogin or ControllerSessionState.LoggingIn) || _challenge is null || _stream is null)
        {
            throw new InvalidOperationException("Not ready to log in.");
        }

        State = ControllerSessionState.LoggingIn;
        SessionOptions sessionOptions = _options.SessionOptions.Clone();
        sessionOptions.SupportedDecoding = DescribeDecoding();
        var request = new LoginRequest
        {
            MyId = _options.MyId,
            MyName = _options.MyName,
            MyPlatform = _options.MyPlatform,
            Version = _options.Version,
            ConnType = _options.ConnType,
            Options = sessionOptions,
            SessionId = (ulong)Random.Shared.NextInt64(),
            Media = new MediaCapabilities { Udp = _options.UdpMedia && _options.Decoders is not null, MaxDatagram = ProtocolConstants.MaxUdpDatagramBytes, FecVersion = 1 },
        };
        if (!string.IsNullOrEmpty(password))
        {
            // The host names the derivation; the ceiling is this side's, because a challenge arrives before
            // anything about the host is trusted and "a billion rounds" must not be an order it can give.
            if (_challenge.KdfIterations > PasswordProof.MaxIterations)
            {
                throw new ProtocolException($"The host asks for {_challenge.KdfIterations} password rounds; this client does at most {PasswordProof.MaxIterations}.");
            }

            request.PasswordProof = ByteString.CopyFrom(PasswordProof.ComputeProof(password, _challenge.Salt.Span, _challenge.Challenge.Span, _challenge.Kdf, (int)_challenge.KdfIterations));
        }

        await _stream.SendAsync(new Message { LoginRequest = request }, ct).ConfigureAwait(false);

        TimeSpan wait = ProtocolConstants.LoginTimeout;
        while (true)
        {
            Message reply = await ReceiveAsync(wait, ct).ConfigureAwait(false)
                ?? throw new ProtocolException("Host closed during login.");
            if (reply.UnionCase != Message.UnionOneofCase.LoginResponse)
            {
                throw new ProtocolException($"Expected LoginResponse, got {reply.UnionCase}.");
            }

            LoginResponse response = reply.LoginResponse;
            if (response.WaitingForApproval && response.UnionCase == LoginResponse.UnionOneofCase.None)
            {
                State = ControllerSessionState.AwaitingApproval;
                wait = ProtocolConstants.ApprovalTimeout + TimeSpan.FromSeconds(10);
                continue;
            }

            if (response.UnionCase == LoginResponse.UnionOneofCase.Error)
            {
                State = ControllerSessionState.AwaitingLogin;
                return LoginResult.Failed(response.Error);
            }

            PeerInfo = response.PeerInfo;
            _callbacks.OnPeerInfo(PeerInfo);
            StartPump();
            State = ControllerSessionState.Authorized;
            return LoginResult.Ok(PeerInfo);
        }
    }

    private void StartPump()
    {
        _pump = new SessionMessagePump(_stream!, _log, _time);
        _pump.Stalled += stalled =>
        {
            IsStalled = stalled;
            _log.LogInformation(stalled ? "Nothing from the host for {Seconds:F0} s" : "The host is answering again", ProtocolConstants.SessionStalledAfter.TotalSeconds);
            _callbacks.OnStalled(stalled);
        };
        _files = new FileTransferEngine(_options.FileSystem ?? LocalFileSystem.Instance, (m, p, ct) => _pump.SendAsync(m, p, ct), _log, _options.FileTransferPolicy, _time);

        // The host may read exactly what this machine has copied, and nothing else. Without a guard the
        // engine answers whatever path the peer names, and a host could walk the whole controller.
        _files.IsPathAllowed = _offer.Allows;
        _pump.Start();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        _videoLoop = Task.Factory.StartNew(() => VideoLoopAsync(_cts.Token), _cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
        if (_options.Clipboard is not null)
        {
            _clipboardLoop = Task.Run(() => ClipboardLoopAsync(_options.Clipboard, _cts.Token));
        }
    }

    private async Task ClipboardLoopAsync(IClipboard clipboard, CancellationToken ct)
    {
        try
        {
            await foreach (IReadOnlyList<ClipboardItem> items in clipboard.Changes.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (!_clipboardEnabled)
                {
                    continue;
                }

                IReadOnlyList<ClipboardItem> outgoing = await WithCopiedFilesAsync(clipboard, items, ct).ConfigureAwait(false);
                MultiClipboards? wire = ClipboardSync.ToWire(outgoing);
                if (wire is null || !_clipboardSync.ShouldSend(wire.ContentHash.Span))
                {
                    continue;
                }

                _clipboardSync.MarkSent(wire.ContentHash.ToByteArray());
                await SendAsync(new Message { Clipboard = wire }, ct, MessagePriority.Bulk).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Clipboard loop ended");
        }
    }

    /// <summary>
    /// Adds a listing of whatever files the user copied, if the clipboard can tell us and the host will let
    /// us send them. Only names and sizes go out; the bytes wait until the host pastes.
    /// </summary>
    private async ValueTask<IReadOnlyList<ClipboardItem>> WithCopiedFilesAsync(
        IClipboard clipboard,
        IReadOnlyList<ClipboardItem> items,
        CancellationToken ct)
    {
        if (!_fileEnabled || clipboard is not IFilePromiseClipboard promiser)
        {
            _offer.Withdraw();
            return items;
        }

        try
        {
            IReadOnlyList<string> paths = await promiser.ReadCopiedFilePathsAsync(ct).ConfigureAwait(false);
            ClipboardItem? listing = _offer.Offer(paths, _options.FileSystem ?? LocalFileSystem.Instance);
            return listing is null ? items : [.. items, listing];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.LogDebug(e, "Could not read what was copied; sending the rest of the clipboard");
            _offer.Withdraw();
            return items;
        }
    }

    private async ValueTask ApplyClipboardAsync(MultiClipboards clipboards, CancellationToken ct)
    {
        if (_options.Clipboard is null)
        {
            return;
        }

        byte[] hash = clipboards.ContentHash.Length == 32 ? clipboards.ContentHash.ToByteArray() : ClipboardSync.Hash(clipboards);
        if (!_clipboardSync.ShouldApply(hash))
        {
            return;
        }

        _clipboardSync.MarkApplied(hash);
        IReadOnlyList<ClipboardItem> items = ClipboardSync.FromWire(clipboards);

        // The host copied something. If files are among it, they are a promise we keep by fetching through
        // this session's own transfer engine — the same code the host runs in the other direction.
        await _promises.WriteAsync(_options.Clipboard, items, _files, _fileEnabled, ct).ConfigureAwait(false);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (Message message in _pump!.Incoming.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await HandleAsync(message, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            _log.LogError(e, "Controller loop crashed");
        }

        string reason = _pump!.Completion.IsCompletedSuccessfully ? _pump.Completion.Result : "closed";
        await CloseAsync(reason).ConfigureAwait(false);
    }

    private async ValueTask HandleAsync(Message message, CancellationToken ct)
    {
        switch (message.UnionCase)
        {
            case Message.UnionOneofCase.VideoFrame:
                await ReceiveVideoAsync(message, message.VideoFrame.Display, message.VideoFrame.Frame?.Seq ?? 0, message.VideoFrame.Frame?.Key ?? false, ct).ConfigureAwait(false);
                break;
            case Message.UnionOneofCase.TileUpdate:
                await ReceiveVideoAsync(message, message.TileUpdate.Display, message.TileUpdate.Seq, false, ct).ConfigureAwait(false);
                break;
            case Message.UnionOneofCase.CursorData:
                _callbacks.OnCursorShape(message.CursorData);
                break;
            case Message.UnionOneofCase.CursorId:
                _callbacks.OnCursorId(message.CursorId.Id);
                break;
            case Message.UnionOneofCase.CursorPosition:
                _callbacks.OnCursorPosition(message.CursorPosition);
                break;
            case Message.UnionOneofCase.AudioFrame:
                HandleAudio(message.AudioFrame);
                break;
            case Message.UnionOneofCase.FileAction:
            case Message.UnionOneofCase.FileResponse:
                if (_files is not null)
                {
                    await _files.HandleAsync(message, ct).ConfigureAwait(false);
                }

                break;
            case Message.UnionOneofCase.TerminalResponse:
                _callbacks.OnTerminal(message.TerminalResponse);
                if (message.TerminalResponse.UnionCase == TerminalResponse.UnionOneofCase.Output)
                {
                    // Consumed by the callback; refill the host's credit so the shell's output keeps flowing.
                    TerminalOutput output = message.TerminalResponse.Output;
                    await SendAsync(new Message { TerminalAction = new TerminalAction { Ack = new TerminalAck { Id = output.Id, Bytes = (uint)output.Data.Length } } }, ct).ConfigureAwait(false);
                }

                break;
            case Message.UnionOneofCase.Clipboard:
                await ApplyClipboardAsync(message.Clipboard, ct).ConfigureAwait(false);
                break;
            case Message.UnionOneofCase.TestDelay when message.TestDelay.FromController:
                long now = _time.GetUtcNow().ToUnixTimeMilliseconds();
                _callbacks.OnRoundTrip(TimeSpan.FromMilliseconds(Math.Max(0, now - message.TestDelay.TimeMs)));
                break;
            case Message.UnionOneofCase.TestDelay:
                await SendAsync(new Message { TestDelay = new TestDelay { TimeMs = message.TestDelay.TimeMs, FromController = false, LastDelayMs = message.TestDelay.LastDelayMs } }, ct).ConfigureAwait(false);
                break;
            case Message.UnionOneofCase.Misc:
                await HandleMiscAsync(message, ct).ConfigureAwait(false);
                break;
            default:
                _callbacks.OnMessage(message);
                break;
        }
    }

    private async ValueTask HandleMiscAsync(Message message, CancellationToken ct)
    {
        Misc misc = message.Misc;
        switch (misc.UnionCase)
        {
            case Misc.UnionOneofCase.Chat:
                _callbacks.OnChat(misc.Chat);
                break;
            case Misc.UnionOneofCase.PermissionInfo:
                if (misc.PermissionInfo.Permission == Permission.PermFile)
                {
                    _fileEnabled = misc.PermissionInfo.Enabled;
                    if (!_fileEnabled)
                    {
                        // Anything already promised can no longer be fetched.
                        _promises.Supersede();
                    }
                }

                if (misc.PermissionInfo.Permission == Permission.PermClipboard)
                {
                    _clipboardEnabled = misc.PermissionInfo.Enabled;
                }

                _callbacks.OnPermission(misc.PermissionInfo);
                break;
            case Misc.UnionOneofCase.SwitchDisplay:
                if (!_speaksSubscription)
                {
                    // A new display is a new sequence space, for the UDP assembler and for the tile watermark alike.
                    _media?.Streams.Expect((byte)misc.SwitchDisplay.Display);
                    _lastTileSeq.Clear();
                }

                _callbacks.OnDisplaySwitched(misc.SwitchDisplay.Display);
                break;
            case Misc.UnionOneofCase.DisplaySubscription:
                if (misc.DisplaySubscription.Failure.Length == 0)
                {
                    byte[] now = [.. misc.DisplaySubscription.Displays.Where(d => d is >= 0 and <= byte.MaxValue).Select(d => (byte)d)];
                    foreach (int display in _lastTileSeq.Keys.Where(d => !now.Contains((byte)d)))
                    {
                        _lastTileSeq.TryRemove(display, out _);
                    }

                    _subscribed = now;
                    _media?.Streams.Expect(now);
                }

                _callbacks.OnDisplaySubscription(misc.DisplaySubscription);
                break;
            case Misc.UnionOneofCase.DisplaysChanged:
                if (PeerInfo is { } known)
                {
                    known.Displays.Clear();
                    known.Displays.AddRange(misc.DisplaysChanged.Displays);
                    known.CurrentDisplay = misc.DisplaysChanged.CurrentDisplay;
                }

                _callbacks.OnDisplaysChanged(misc.DisplaysChanged);
                break;
            case Misc.UnionOneofCase.AudioFormat:
                await ConfigureAudioAsync(new AudioStreamFormat((int)misc.AudioFormat.SampleRate, (int)misc.AudioFormat.Channels), ct).ConfigureAwait(false);
                break;
            case Misc.UnionOneofCase.CloseReason:
                await CloseAsync($"peer: {misc.CloseReason.Reason}").ConfigureAwait(false);
                break;
            case Misc.UnionOneofCase.MediaOffer:
                await OnMediaOfferAsync(misc.MediaOffer, ct).ConfigureAwait(false);
                break;
            case Misc.UnionOneofCase.MediaReady:
                if (_media is { } media && misc.MediaReady.ChannelId == media.ChannelId)
                {
                    media.MarkPeerReady();
                }

                break;
            case Misc.UnionOneofCase.MediaClose:
                await CloseMediaAsync($"peer: {misc.MediaClose.Reason}", notifyPeer: false).ConfigureAwait(false);
                break;
            default:
                _callbacks.OnMessage(message);
                break;
        }
    }

    /// <summary>
    /// Message thread: account the packet, acknowledge it at once (the ack measures the network, not our
    /// decoder), then hand it to the video thread. Video frames that the video thread cannot keep up with
    /// are dropped there; tile updates never are.
    /// </summary>
    private async ValueTask ReceiveVideoAsync(Message message, int display, uint seq, bool key, CancellationToken ct)
    {
        if (message.UnionCase == Message.UnionOneofCase.VideoFrame)
        {
            VideoFramesReceived++;
            VideoBytesReceived += message.VideoFrame.Frame?.Data.Length ?? 0;
        }
        else
        {
            TileUpdatesReceived++;
            TileBytesReceived += message.TileUpdate.CalculateSize();
        }

        _callbacks.OnVideoPacket(display, seq, key);
        await SendAsync(new Message { Misc = new Misc { VideoAck = new VideoAck { Display = display, Seq = seq } } }, ct).ConfigureAwait(false);
        if (_videoQueue.Enqueue(message, out int droppedDisplay))
        {
            VideoFramesDroppedLate++;
            await RefreshVideoAsync(droppedDisplay, ct).ConfigureAwait(false);
        }
    }

    private async Task VideoLoopAsync(CancellationToken ct)
    {
        try
        {
            Thread.CurrentThread.Name ??= "video-decode";
            _decodeWindowStart = _time.GetTimestamp();
            while (!ct.IsCancellationRequested)
            {
                Message message = await _videoQueue.DequeueAsync(ct).ConfigureAwait(false);
                long t0 = _time.GetTimestamp();
                if (message.UnionCase == Message.UnionOneofCase.VideoFrame)
                {
                    // Tiles (TCP) and video (UDP) share one sequence: a video frame older than the last tile patch is
                    // decoded for reference continuity but not shown, or it would overwrite lossless pixels with stale ones.
                    bool present = message.VideoFrame.Frame is null || message.VideoFrame.Frame.Seq >= _lastTileSeq.GetValueOrDefault(message.VideoFrame.Display);
                    await HandleVideoAsync(message.VideoFrame, present, ct).ConfigureAwait(false);
                }
                else
                {
                    _lastTileSeq.AddOrUpdate(message.TileUpdate.Display, message.TileUpdate.Seq, (_, seen) => Math.Max(seen, message.TileUpdate.Seq));
                    await HandleTileUpdateAsync(message.TileUpdate, ct).ConfigureAwait(false);
                }

                double ms = _time.GetElapsedTime(t0).TotalMilliseconds;
                DecodeMs = DecodeMs == 0 ? ms : 0.9 * DecodeMs + 0.1 * ms;
                _decodeWindowCount++;
                _decodeWindowMs += ms;
                if (_time.GetElapsedTime(_decodeWindowStart) >= TimeSpan.FromSeconds(5))
                {
                    double seconds = _time.GetElapsedTime(_decodeWindowStart).TotalSeconds;
                    DecodedFps = _decodeWindowCount / seconds;
                    UdpMediaChannel? media = _media;
                    _log.LogInformation("Video: {Fps:F1} decoded fps, {Ms:F2} ms per frame, {Late} dropped late, queue {Queue}, keyframes {Keyframes}, refreshes {Refreshes}{Udp}",
                        DecodedFps, _decodeWindowMs / Math.Max(1, _decodeWindowCount), VideoFramesDroppedLate, _videoQueue.Count, KeyFramesReceived, RefreshRequests,
                        media is null ? string.Empty : $", udp {media.Path?.Kind} loss {media.Link.LossPermille}‰ given up {media.Streams.FramesGivenUp} recovered {media.Streams.ShardsRecovered} rejected {media.DatagramsRejected} rtt {media.LastRttMs:F0} ms");
                    _decodeWindowStart = _time.GetTimestamp();
                    _decodeWindowCount = 0;
                    _decodeWindowMs = 0;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            _log.LogError(e, "Video loop crashed");
        }
    }

    private async ValueTask HandleVideoAsync(VideoFrame vf, bool present, CancellationToken ct)
    {
        _lastVideoDisplay = vf.Display;
        if (vf.Frame?.Key == true)
        {
            KeyFramesReceived++;
        }

        Platform.Abstractions.Codec.VideoCodec? codec = VideoService.FromWire(vf.Codec);
        if (codec is { } wire && vf.Frame is not null)
        {
            // Before decoding, and whether or not this frame will be shown: a recording needs every frame to keep
            // its reference chain, even the ones the local decoder rejects.
            _callbacks.OnVideoEncoded(vf.Display, vf.Frame.Data.Span, vf.Frame.Key, vf.Frame.PtsMs, (int)vf.Width, (int)vf.Height, wire);
        }

        if (_options.Decoders is not null && codec is not null && vf.Frame is not null)
        {
            if (!_decoders.TryGetValue(vf.Display, out (Platform.Abstractions.Codec.VideoCodec Codec, IVideoDecoder Decoder) entry) || entry.Codec != codec.Value)
            {
                if (entry.Decoder is not null)
                {
                    await entry.Decoder.DisposeAsync().ConfigureAwait(false);
                }

                try
                {
                    entry = (codec.Value, _options.Decoders.Create(codec.Value, GpuApi.None, 0));
                }
                catch (NotSupportedException e)
                {
                    // Nothing here can decode this codec. Said once, and then the frames are dropped: the
                    // lossless tile path does not go through a decoder, so a picture still arrives.
                    if (_undecodable.Add(codec.Value))
                    {
                        _log.LogError(e, "No decoder for {Codec}; falling back to lossless tiles only", codec.Value);
                    }

                    return;
                }

                _decoders[vf.Display] = entry;
            }

            try
            {
                if (!present)
                {
                    // The decoder is about to overwrite the buffer the canvas may still borrow; keep the shown picture.
                    Compositor(vf.Display).DetachFromDecoder();
                }

                if (entry.Decoder.TryDecode(vf.Frame.Data.Span, vf.Frame.Key, out DecodedFrame frame))
                {
                    VideoFramesDecoded++;
                    _decoderSilent[vf.Display] = 0; // the pipeline is producing again
                    if (vf.Frame.Key)
                    {
                        _awaitingKeyFrame.Remove(vf.Display);
                    }

                    if (!present)
                    {
                        // decoded for reference state only
                    }
                    else if (frame.Format == PixelFormat.Bgra32 && !frame.IsGpuSurface)
                    {
                        DecodedFrame composed = Compositor(vf.Display).ApplyVideo(in frame);
                        _callbacks.OnVideoFrame(vf.Display, in composed);
                    }
                    else
                    {
                        _callbacks.OnVideoFrame(vf.Display, in frame);
                    }
                }
                else if (!vf.Frame.Key)
                {
                    // No picture this time. That is normal while a pipelined decoder fills up, so only treat a
                    // run of silent frames as a broken stream — asking for a keyframe on the first one makes the
                    // host answer every delta with a full frame, which collapses the frame rate.
                    _decoderSilent.TryGetValue(vf.Display, out int silent);
                    _decoderSilent[vf.Display] = ++silent;
                    if (silent >= DecoderSilenceLimit)
                    {
                        _log.LogDebug("Decoder returned no picture for {Count} frames on display {Display}; requesting a keyframe", silent, vf.Display);
                        _decoderSilent[vf.Display] = 0;
                        _awaitingKeyFrame.Add(vf.Display);
                        await RefreshVideoAsync(vf.Display, ct).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception e)
            {
                _log.LogWarning(e, "Decoding frame {Seq} failed; requesting a keyframe", vf.Frame.Seq);
                _decoderSilent[vf.Display] = 0;
                _awaitingKeyFrame.Add(vf.Display);
                await RefreshVideoAsync(vf.Display, ct).ConfigureAwait(false);
            }
        }
    }

    // ---------------------------------------------------------------- UDP media channel

    private async ValueTask OnMediaOfferAsync(MediaChannelOffer offer, CancellationToken ct)
    {
        if (!_options.UdpMedia || _mediaKeys is null || _options.Decoders is null)
        {
            await SendAsync(new Message { Misc = new Misc { MediaAnswer = new MediaChannelAnswer { ChannelId = offer.ChannelId, Accept = false } } }, ct).ConfigureAwait(false);
            return;
        }

        await CloseMediaAsync("new offer", notifyPeer: false).ConfigureAwait(false);
        UdpDatagramSocket socket = UdpDatagramSocket.Create(0, _options.MediaDebugLoss, _options.MediaDebugLossSeed);
        try
        {
            List<MediaCandidate> candidates = await CandidateGatherer.GatherAsync(socket, _rendezvousServer, TimeSpan.FromMilliseconds(600), ct).ConfigureAwait(false);
            IDatagramSocket channelSocket = _options.MediaSocketWrapper is { } wrap ? wrap(socket) : socket;
            var channel = new UdpMediaChannel(channelSocket, _mediaKeys, offer.ChannelId, isHost: false, _log, _time);
            channel.PathVerified += path => _ = SendAsync(new Message { Misc = new Misc { MediaReady = new MediaChannelReady { ChannelId = offer.ChannelId, Path = path.Kind, Remote = CandidateGatherer.Candidate(path.Remote, path.Kind, 0).Addr } } }, CancellationToken.None);
            channel.Dead += reason => _ = CloseMediaAsync($"channel dead: {reason}", notifyPeer: true);
            channel.RefreshRequested += stream => _ = RefreshVideoAsync(stream, CancellationToken.None);
            channel.FrameReceived += OnMediaFrame;
            if (_subscribed is { } subscribed)
            {
                // Opened after the host confirmed a set: without this it would take one stream at a time.
                channel.Streams.Expect(subscribed);
            }

            channel.Start();
            _media = channel;

            var answer = new MediaChannelAnswer { ChannelId = offer.ChannelId, Accept = true };
            answer.Candidates.AddRange(candidates);
            await SendAsync(new Message { Misc = new Misc { MediaAnswer = answer } }, ct).ConfigureAwait(false);

            string relayServer = string.IsNullOrEmpty(offer.RelayServer) ? _relayServer : offer.RelayServer;
            IPEndPoint? relay = offer.RelayToken.Length == RelayDatagram.TokenBytes ? await Host.HostSession.ResolveRelayAsync(relayServer, ct).ConfigureAwait(false) : null;
            // The host's ticket names the relay it offers; this side's own is for the case where an older
            // host sent none but the rendezvous gave this side one for the same relay.
            RelayTicket? ticket = offer.RelayTicket ?? _relayTicket;
            channel.StartProbing(offer.Candidates, relay, relay is null ? null : offer.RelayToken.ToByteArray(), ticket);
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "UDP media channel setup failed; staying on TCP");
            if (_media is null)
            {
                socket.Dispose();
            }
        }
    }

    /// <summary>Receive thread of the media channel: wrap the frame as a VideoFrame message and queue it for decoding.</summary>
    private void OnMediaFrame(AssembledFrame frame)
    {
        try
        {
            var vf = new VideoFrame
            {
                Codec = (Protocol.Messages.VideoCodec)frame.Codec,
                Display = frame.Stream,
                Width = (uint)frame.Width,
                Height = (uint)frame.Height,
                Frame = new EncodedVideoFrame { Data = ByteString.CopyFrom(frame.Data), Key = frame.KeyFrame, PtsMs = frame.PtsMs, Seq = frame.FrameSeq },
            };
            VideoFramesReceived++;
            VideoFramesReceivedUdp++;
            VideoBytesReceived += frame.Length;
            _callbacks.OnVideoPacket(vf.Display, vf.Frame.Seq, vf.Frame.Key);
            if (_videoQueue.Enqueue(new Message { VideoFrame = vf }, out int droppedDisplay))
            {
                VideoFramesDroppedLate++;
                _ = RefreshVideoAsync(droppedDisplay, CancellationToken.None);
            }
        }
        finally
        {
            frame.Return();
        }
    }

    private async Task CloseMediaAsync(string reason, bool notifyPeer)
    {
        UdpMediaChannel? channel = Interlocked.Exchange(ref _media, null);
        if (channel is null)
        {
            return;
        }

        _log.LogInformation("UDP media channel closed ({Reason}); video back on TCP", reason);
        if (notifyPeer && State == ControllerSessionState.Authorized)
        {
            try
            {
                await SendAsync(new Message { Misc = new Misc { MediaClose = new MediaChannelClose { ChannelId = channel.ChannelId, Reason = reason } } }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        channel.SendClose(reason);
        await channel.DisposeAsync().ConfigureAwait(false);
        _ = RefreshVideoAsync(_lastVideoDisplay, CancellationToken.None);
    }

    private FrameCompositor Compositor(int display)
    {
        if (!_compositors.TryGetValue(display, out FrameCompositor? c))
        {
            c = new FrameCompositor();
            _compositors[display] = c;
        }

        return c;
    }

    /// <summary>Lossless tile patch: applied on the CPU canvas regardless of whether a video decoder exists.</summary>
    private async ValueTask HandleTileUpdateAsync(TileUpdate update, CancellationToken ct)
    {
        bool broken = _awaitingKeyFrame.Contains(update.Display) || (_media is { } media && media.Streams.IsBroken((byte)update.Display));
        if (broken)
        {
            // The picture underneath is not the one the host diffed against; patching it would scatter new squares
            // over stale content. Refuse and make sure a keyframe is on its way.
            TilesRejectedBrokenReference += update.Tiles.Count;
            if (_time.GetElapsedTime(_lastTileRefresh) >= TimeSpan.FromSeconds(1))
            {
                _lastTileRefresh = _time.GetTimestamp();
                await RefreshVideoAsync(update.Display, ct).ConfigureAwait(false);
            }

            return;
        }

        FrameCompositor compositor = Compositor(update.Display);
        bool applied;
        try
        {
            applied = compositor.ApplyTiles(update);
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Applying tile update {Seq} failed; requesting a refresh", update.Seq);
            applied = false;
        }

        if (applied)
        {
            TilesReceived += update.Tiles.Count;
            // A refinement round is shown as one picture: hold its batches until the round completes (or 150 ms passes),
            // so text sharpens in one step instead of square by square.
            bool show = true;
            if (update.Refinement && update.Pass != 0)
            {
                if (update.Pass != _pendingPass)
                {
                    _pendingPass = update.Pass;
                    _pendingPassSince = _time.GetTimestamp();
                }

                show = update.PassComplete || _time.GetElapsedTime(_pendingPassSince) >= TimeSpan.FromMilliseconds(150);
                if (show)
                {
                    _pendingPassSince = _time.GetTimestamp();
                }
            }

            if (show)
            {
                DecodedFrame composed = compositor.Current;
                _callbacks.OnVideoFrame(update.Display, in composed);
            }
        }
        else
        {
            await RefreshVideoAsync(update.Display, ct).ConfigureAwait(false);
        }
    }

    private async ValueTask ConfigureAudioAsync(AudioStreamFormat format, CancellationToken ct)
    {
        _audioDecoder?.Dispose();
        _audioDecoder = new OpusDecoderWrapper(format);
        _callbacks.OnAudioFormat(format);
        if (_options.AudioPlayback is not null)
        {
            await _options.AudioPlayback.ConfigureAsync(format, ct).ConfigureAwait(false);
        }
    }

    private void HandleAudio(AudioFrame af)
    {
        AudioFramesReceived++;
        if (_audioDecoder is null)
        {
            return; // nothing announced the format yet; decoding without it would be guesswork
        }

        try
        {
            ReadOnlySpan<float> pcm = _audioDecoder.Decode(af.Opus.Span);
            _callbacks.OnAudioPcm(pcm, af.PtsMs);
            _options.AudioPlayback?.Enqueue(pcm);
        }
        catch (Exception e)
        {
            _log.LogDebug(e, "Audio decode failed");
        }
    }

    public ValueTask SendAsync(Message message, CancellationToken ct = default, MessagePriority priority = MessagePriority.Control) =>
        _pump?.SendAsync(message, priority, ct) ?? ValueTask.CompletedTask;

    public ValueTask SendChatAsync(string text, CancellationToken ct = default) =>
        SendAsync(new Message { Misc = new Misc { Chat = new ChatMessage { Text = text, SentAtMs = _time.GetUtcNow().ToUnixTimeMilliseconds() } } }, ct);

    /// <summary>True while nothing has arrived from the host for long enough to be worth telling the user.</summary>
    public bool IsStalled { get; private set; }

    /// <summary>Probe the host; the reply arrives through <see cref="IControllerCallbacks.OnRoundTrip"/>.</summary>
    public ValueTask SendPingAsync(CancellationToken ct = default) =>
        SendAsync(new Message { TestDelay = new TestDelay { TimeMs = _time.GetUtcNow().ToUnixTimeMilliseconds(), FromController = true } }, ct);

    public ValueTask SendMouseAsync(MouseEvent mouse, CancellationToken ct = default) =>
        SendAsync(new Message { MouseEvent = mouse }, ct, MessagePriority.Input);

    public ValueTask SendKeyAsync(KeyEvent key, CancellationToken ct = default) =>
        SendAsync(new Message { KeyEvent = key }, ct, MessagePriority.Input);

    public ValueTask SwitchDisplayAsync(int display, CancellationToken ct = default) =>
        SendAsync(new Message { Misc = new Misc { SwitchDisplay = new SwitchDisplay { Display = display } } }, ct);

    /// <summary>
    /// Asks the host to plug in a display that does not exist, at <paramref name="resolution"/> when given, able to take
    /// <paramref name="sizes"/> later on -- this viewer's screens, say, for a window moved to one or maximised there. The
    /// answer is <see cref="IControllerCallbacks.OnDisplaysChanged"/>: the new list with the display marked virtual, or why not.
    /// </summary>
    public ValueTask AddVirtualDisplayAsync(Resolution? resolution = null, IEnumerable<Resolution>? sizes = null, CancellationToken ct = default)
    {
        var request = new VirtualDisplayRequest { Action = VirtualDisplayRequest.Types.Action.VdAdd, Resolution = resolution };
        if (sizes is not null)
        {
            request.Sizes.AddRange(sizes);
        }

        return SendAsync(new Message { Misc = new Misc { VirtualDisplayRequest = request } }, ct);
    }

    /// <summary>Asks the host to unplug a display that was plugged in from here.</summary>
    public ValueTask RemoveVirtualDisplayAsync(int display, CancellationToken ct = default) =>
        SendAsync(new Message { Misc = new Misc { VirtualDisplayRequest = new VirtualDisplayRequest { Action = VirtualDisplayRequest.Types.Action.VdRemove, Display = display } } }, ct);

    /// <summary>True when the host understands <see cref="SubscribeDisplaysAsync"/>; an older one ignores it.</summary>
    public bool HostSupportsMultiDisplay => PeerInfo?.MultiDisplay == true;

    /// <summary>
    /// Asks for every display in <paramref name="displays"/> at once, with <paramref name="focus"/> the one that has
    /// this viewer's attention. The whole set every time, never a change to it. The answer comes back through
    /// <see cref="IControllerCallbacks.OnDisplaySubscription"/>; from this call on, SwitchDisplay from the host
    /// only moves the focus.
    /// </summary>
    public ValueTask SubscribeDisplaysAsync(IReadOnlyCollection<int> displays, int focus, CancellationToken ct = default)
    {
        _speaksSubscription = true;
        var request = new DisplaySubscription { Focus = focus };
        request.Displays.AddRange(displays);
        return SendAsync(new Message { Misc = new Misc { DisplaySubscription = request } }, ct);
    }

    /// <summary>Asks the host to switch a display to one of its advertised modes, or back to its original when <paramref name="resolution"/> is null.</summary>
    public ValueTask SetResolutionAsync(int display, Resolution? resolution, CancellationToken ct = default) =>
        SendAsync(new Message { Misc = new Misc { DisplayResolution = new DisplayResolution { Display = display, Resolution = resolution } } }, ct);

    /// <summary>Asks for a shell. The answer is <see cref="IControllerCallbacks.OnTerminal"/> with Opened or Error.</summary>
    public ValueTask OpenTerminalAsync(int id, int columns, int rows, CancellationToken ct = default) =>
        SendAsync(new Message { TerminalAction = new TerminalAction { Open = new TerminalOpen { Id = id, Columns = (uint)Math.Max(0, columns), Rows = (uint)Math.Max(0, rows) } } }, ct);

    /// <summary>Bytes typed at the shell; at input priority so a Ctrl+C is never queued behind a file block.</summary>
    public ValueTask SendTerminalInputAsync(int id, ReadOnlyMemory<byte> data, CancellationToken ct = default) =>
        SendAsync(new Message { TerminalAction = new TerminalAction { Input = new TerminalInput { Id = id, Data = ByteString.CopyFrom(data.Span) } } }, ct, MessagePriority.Input);

    public ValueTask ResizeTerminalAsync(int id, int columns, int rows, CancellationToken ct = default) =>
        SendAsync(new Message { TerminalAction = new TerminalAction { Resize = new TerminalResize { Id = id, Columns = (uint)Math.Max(0, columns), Rows = (uint)Math.Max(0, rows) } } }, ct);

    public ValueTask SignalTerminalAsync(int id, TerminalSignal.Types.Kind signal, CancellationToken ct = default) =>
        SendAsync(new Message { TerminalAction = new TerminalAction { Signal = new TerminalSignal { Id = id, Signal = signal } } }, ct);

    public ValueTask CloseTerminalAsync(int id, CancellationToken ct = default) =>
        SendAsync(new Message { TerminalAction = new TerminalAction { Close = new TerminalClose { Id = id } } }, ct);

    public ValueTask RefreshVideoAsync(int display, CancellationToken ct = default)
    {
        RefreshRequests++;
        return SendAsync(new Message { Misc = new Misc { RefreshVideo = new RefreshVideo { Display = display } } }, ct);
    }

    public ValueTask SetOptionsAsync(SessionOptions options, CancellationToken ct = default)
    {
        // Always restate what we can decode, so a UI that only sets quality/cursor/audio cannot drop the tile path.
        options.SupportedDecoding ??= DescribeDecoding();
        return SendAsync(new Message { Misc = new Misc { Options = options } }, ct);
    }

    /// <summary>
    /// What this machine can actually decode. It used to claim H.264 and nothing else, which was true of what
    /// it would accept but said nothing about H.265 or AV1 — so a host with an H.265 encoder had no way to
    /// learn that this viewer could read it, and a viewer on Windows N with no decoder at all still claimed
    /// H.264. Both are answered by asking the factory.
    /// </summary>
    private SupportedDecoding DescribeDecoding()
    {
        SupportedCodecs decodable = _options.Decoders?.Probe() ?? SupportedCodecs.None;
        SupportedDecoding decoding = CodecWire.ToDecoding(decodable, _options.LosslessTiles);
        if (_options.PreferredCodec is { } prefer && decodable.Supports(prefer))
        {
            decoding.Prefer = VideoService.ToWire(prefer);
        }

        return decoding;
    }

    public async Task CloseAsync(string reason)
    {
        if (!_closed.TrySetResult(reason))
        {
            return;
        }

        if (_pump is not null && _stream is not null && _stream.IsEncrypted)
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

        await _cts.CancelAsync().ConfigureAwait(false);
        await CloseMediaAsync(reason, notifyPeer: false).ConfigureAwait(false);
        _mediaKeys?.Dispose();
        _videoQueue.Complete();
        if (_videoLoop is not null)
        {
            try
            {
                await _videoLoop.ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        if (_pump is not null)
        {
            await _pump.DisposeAsync().ConfigureAwait(false);
        }

        if (_stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        else if (_transport is not null)
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
        }

        foreach ((_, IVideoDecoder decoder) in _decoders.Values)
        {
            await decoder.DisposeAsync().ConfigureAwait(false);
        }

        _decoders.Clear();
        _audioDecoder?.Dispose();
        if (_files is not null)
        {
            await _files.DisposeAsync().ConfigureAwait(false);
        }

        State = ControllerSessionState.Closed;
        _callbacks.OnClosed(reason);
    }

    private async Task<Message?> ReceiveAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        cts.CancelAfter(timeout);
        try
        {
            using Frame? frame = await _stream!.ReceiveAsync(cts.Token).ConfigureAwait(false);
            return frame is null ? null : Message.Parser.ParseFrom(frame.Payload.Span);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ProtocolException("Timed out waiting for the host.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync("disposed").ConfigureAwait(false);
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        _cts.Dispose();
    }

    /// <summary>
    /// Bounded hand-off from the message thread to the video thread. When the decoder falls behind, the oldest
    /// queued video frame is discarded (never a keyframe, never a tile update) and the caller asks for a refresh.
    /// </summary>
    private sealed class VideoQueue
    {
        private const int Capacity = 3;
        private readonly LinkedList<Message> _items = new();
        private readonly SemaphoreSlim _signal = new(0);
        private readonly object _lock = new();
        private bool _completed;

        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _items.Count;
                }
            }
        }

        /// <summary>Returns true when a frame had to be dropped; <paramref name="droppedDisplay"/> names its display.</summary>
        public bool Enqueue(Message message, out int droppedDisplay)
        {
            droppedDisplay = -1;
            bool dropped = false;
            lock (_lock)
            {
                if (_completed)
                {
                    return false;
                }

                _items.AddLast(message);
                int videoFrames = 0;
                foreach (Message m in _items)
                {
                    if (m.UnionCase == Message.UnionOneofCase.VideoFrame)
                    {
                        videoFrames++;
                    }
                }

                if (videoFrames > Capacity)
                {
                    for (LinkedListNode<Message>? n = _items.First; n is not null; n = n.Next)
                    {
                        if (n.Value.UnionCase == Message.UnionOneofCase.VideoFrame && n.Value.VideoFrame.Frame?.Key != true)
                        {
                            droppedDisplay = n.Value.VideoFrame.Display;
                            _items.Remove(n);
                            dropped = true;
                            break;
                        }
                    }
                }
            }

            if (!dropped)
            {
                _signal.Release();
            }

            return dropped;
        }

        public async ValueTask<Message> DequeueAsync(CancellationToken ct)
        {
            while (true)
            {
                await _signal.WaitAsync(ct).ConfigureAwait(false);
                lock (_lock)
                {
                    if (_items.First is { } first)
                    {
                        _items.RemoveFirst();
                        return first.Value;
                    }

                    if (_completed)
                    {
                        throw new OperationCanceledException();
                    }
                }
            }
        }

        public void Complete()
        {
            lock (_lock)
            {
                _completed = true;
            }

            _signal.Release();
        }
    }
}
