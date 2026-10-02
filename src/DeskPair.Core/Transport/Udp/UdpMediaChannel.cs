using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using DeskPair.Core.Qos.Gcc;
using DeskPair.Core.Transport.Udp.Fec;
using DeskPair.Protocol;
using DeskPair.Protocol.Crypto;
using DeskPair.Protocol.Media;
using DeskPair.Protocol.Messages;
using DeskPair.Protocol.Rendezvous;

namespace DeskPair.Core.Transport.Udp;

/// <summary>One side's view of the negotiated UDP path.</summary>
public sealed record MediaPath(MediaCandidate.Types.Kind Kind, IPEndPoint Remote);

/// <summary>
/// The UDP media channel of one session: an encrypted datagram socket that probes candidate paths
/// (direct, reflexive, relayed), keeps the chosen one alive, sends video frames as FEC-protected shards
/// (host) and reassembles them (controller), and carries feedback the other way. Everything that needs
/// ordering or reliability stays on the TCP session; this class only ever deals in datagrams.
/// </summary>
public sealed class UdpMediaChannel : IAsyncDisposable
{
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan PromotionGrace = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FeedbackInterval = TimeSpan.FromMilliseconds(16);
    private static readonly TimeSpan RefreshRateLimit = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan TransportFeedbackInterval = TimeSpan.FromMilliseconds(50);
    private const int SentRing = 4096;
    private const int MaxArrivalsPerReport = 200;
    private static readonly TimeSpan ProbeDuration = TimeSpan.FromMilliseconds(30);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan IdleProbeInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FailedProbeBackoff = TimeSpan.FromSeconds(15);

    /// <summary>How many times the start-up probe is tried, a second apart, before it waits like any other.</summary>
    private const int StartUpProbeAttempts = 3;
    private static readonly TimeSpan StartUpProbeRetry = TimeSpan.FromSeconds(1);
    /// <summary>Default probe ceiling until the host tells the channel what the picture can use.</summary>
    public const double DefaultCeilingBps = 80_000_000;

    private readonly IDatagramSocket _socket;
    private readonly MediaCipher _cipher;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly bool _isHost;
    private readonly FecPlanner _planner = new();
    private readonly MediaPacketizer _packetizer;
    private readonly LinkStats _link;
    private readonly StreamAssemblers _streams;
    private readonly List<FrameAssembler> _reportStreams = new();
    private readonly List<FrameAssembler> _refreshStreams = new();
    private readonly Dictionary<byte, long> _lastRefreshRequest = new();
    private readonly PacedSender _sender;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();
    private readonly Dictionary<IPEndPoint, (MediaCandidate.Types.Kind Kind, uint Priority)> _remotes = new();
    private readonly long[] _sentAt = new long[SentRing];
    private readonly ulong[] _sentSeq = new ulong[SentRing];
    private readonly int[] _sentSize = new int[SentRing];
    private readonly bool[] _sentOnPath = new bool[SentRing];
    private readonly bool[] _sentProbe = new bool[SentRing];
    private readonly GccController _gcc = new();
    private readonly List<TransportFeedback.Arrival> _arrivals = new();
    private readonly List<TransportFeedback.Arrival> _arrivalsOut = new();
    private readonly List<TransportFeedback.Arrival> _reportRead = new();
    private readonly List<PacketResult> _reportResults = new();
    private readonly byte[] _reportBuffer = new byte[TransportFeedback.MaxSize(TransportFeedback.MaxPacketsPerReport)];
    private long _lastTransportFeedback;
    private ulong _highestReportedSeq;
    private ulong _probeSeqLow;
    private ulong _probeSeqHigh;
    private readonly object _probeLock = new();
    private readonly List<PacketResult> _probeResults = new();
    private ProbeCluster? _probe;
    private int _probeClusterId;
    private long _nextProbeAt;
    private bool _initialProbeDone;
    private int _startUpProbeFailures;
    private double _ceilingBps = DefaultCeilingBps;
    private bool _ceilingKnown;
    private long _readyAt;
    private readonly byte[] _datagram = new byte[ProtocolConstants.MaxUdpDatagramBytes];
    private readonly byte[] _plain = new byte[ProtocolConstants.MaxUdpDatagramBytes];
    private Task? _receiveLoop;
    private Task? _timerLoop;
    private IPEndPoint? _relay;
    private byte[]? _relayToken;
    private byte[]? _relayTicket;
    private bool _relayPaired;
    private long _probeStart;
    private long _lastReceive;
    private long _lastSend;
    private long _lastFeedbackReceive;
    private ulong _lastReceivedPacketSeq;
    private long _lastReceivedAt;
    private bool _peerReady;
    private bool _dead;
    private bool _disposed;

    public UdpMediaChannel(IDatagramSocket socket, MediaKeys keys, uint channelId, bool isHost, ILogger log, TimeProvider time)
    {
        _socket = socket;
        _log = log;
        _time = time;
        _isHost = isHost;
        ChannelId = channelId;
        _cipher = new MediaCipher(keys.TxKey, keys.TxIvPrefix, keys.RxKey, keys.RxIvPrefix);
        _packetizer = new MediaPacketizer(_cipher, _planner);
        _link = new LinkStats(time);
        _streams = new StreamAssemblers(time);
        _sender = new PacedSender(socket, () => Path?.Remote, time, (seq, at, size) => RecordSent(seq, at, size, onPath: true));
        _probeSeqLow = _probeSeqHigh = 0;
        _lastReceive = _lastSend = time.GetTimestamp();
    }

    public uint ChannelId { get; }

    public IPEndPoint LocalEndPoint => _socket.LocalEndPoint;

    /// <summary>The verified path (best so far); null while probing.</summary>
    public MediaPath? Path { get; private set; }

    /// <summary>True once our path is verified and the peer reported its own verification: media may flow.</summary>
    public bool IsReady => Path is not null && _peerReady && !_dead;

    public bool IsDead => _dead;

    public FecPlanner Planner => _planner;

    /// <summary>Controller side: what the path as a whole delivered.</summary>
    public LinkStats Link => _link;

    /// <summary>Controller side: one assembler per display being watched.</summary>
    public StreamAssemblers Streams => _streams;

    public long DatagramsReceived { get; private set; }

    public long DatagramsRejected { get; private set; }

    public long FramesSent => _sender.FramesSent;

    public long FramesDropped => _sender.FramesDropped;

    public long BytesSent => _sender.BytesSent;

    public double LastRttMs { get; private set; }

    /// <summary>libwebrtc's default: packets may leave up to 2.5 times faster than the target so bursts drain quickly.</summary>
    public const double PacingFactor = 2.5;

    /// <summary>Host side: the congestion controller fed by the viewer's arrival reports (read it from the estimate event).</summary>
    public GccController Gcc => _gcc;

    /// <summary>
    /// Host side: highest bitrate worth probing for (what the picture can use at the chosen quality). Probing
    /// stops once the estimate reaches it; the estimate itself is capped there too.
    /// </summary>
    public double BandwidthCeilingBps
    {
        get => _ceilingBps;
        set
        {
            _ceilingKnown |= value > 0;
            if (value > 0 && Math.Abs(value - _ceilingBps) > 1)
            {
                _ceilingBps = value;
                _gcc.MaxBps = Math.Max(GccController.DefaultMinBps, value);
            }
        }
    }

    /// <summary>Host side: probe clusters sent and the last probe outcome (diagnostics).</summary>
    public long ProbesSent => _sender.ProbesSent;

    public string LastProbe { get; private set; } = "none";

    /// <summary>Host side: raised on the receive thread after each arrival report with the new GCC target (bits per second).</summary>
    public event Action<double>? BandwidthEstimated;

    /// <summary>Raised on the receive thread once a path is verified (first time) or promoted to a better one.</summary>
    public event Action<MediaPath>? PathVerified;

    /// <summary>Raised once when the peer has been silent for <see cref="ProtocolConstants.MediaDeadAfter"/>.</summary>
    public event Action<string>? Dead;

    /// <summary>Controller side: a complete frame; the handler owns the buffer.</summary>
    public event Action<AssembledFrame>? FrameReceived;

    /// <summary>Host side: the receiver's report plus the round trip derived from its echo.</summary>
    public event Action<MediaFeedback, TimeSpan?>? FeedbackReceived;

    /// <summary>Controller side: a stream lost a frame and a keyframe of that display should be requested over TCP (rate limited per stream).</summary>
    public event Action<byte>? RefreshRequested;

    public void Start()
    {
        _receiveLoop = Task.Factory.StartNew(() => ReceiveLoopAsync(_cts.Token), _cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
        _timerLoop = Task.Run(() => TimerLoopAsync(_cts.Token));
    }

    /// <summary>Begins sending Bind probes to the remote candidates (and pairing with the relay, when given).</summary>
    /// <summary>Starts probing the peer's candidates and, with a relay and token, the relay; the ticket is what a relay that checks tickets needs to see with the bind.</summary>
    public void StartProbing(IEnumerable<MediaCandidate> remotes, IPEndPoint? relay, byte[]? relayToken, RelayTicket? relayTicket = null)
    {
        lock (_lock)
        {
            foreach (MediaCandidate c in remotes)
            {
                if (CandidateGatherer.ToEndPoint(c) is { } ep)
                {
                    _remotes[ep] = (c.Kind, c.Priority);
                }
            }

            if (relay is not null && relayToken is { Length: RelayDatagram.TokenBytes })
            {
                _relay = relay;
                _relayToken = relayToken;
                _relayTicket = relayTicket is null ? null : Google.Protobuf.MessageExtensions.ToByteArray(relayTicket);
                _remotes[relay] = (MediaCandidate.Types.Kind.Relay, CandidateGatherer.RelayPriority);
            }

            _probeStart = _time.GetTimestamp();
        }

        Probe();
    }

    /// <summary>The peer told us (over TCP) that its own probe succeeded.</summary>
    public void MarkPeerReady()
    {
        _peerReady = true;
    }

    /// <summary>Host: sends one encoded frame; false when an older frame had to be dropped to keep up.</summary>
    public bool TrySendVideo(int display, byte codec, uint frameSeq, long ptsMs, bool keyFrame, int width, int height, ReadOnlySpan<byte> data, TimeSpan frameInterval)
    {
        if (!IsReady)
        {
            return false;
        }

        List<PooledDatagram> datagrams;
        lock (_cipher)
        {
            // The cipher's transmit counter is shared with control packets sent from the timer and receive threads.
            datagrams = _packetizer.Packetize((byte)display, codec, frameSeq, ptsMs, keyFrame, width, height, data);
        }

        _lastSend = _time.GetTimestamp();
        return _sender.Enqueue(datagrams, frameInterval);
    }

    public void SendClose(string reason)
    {
        if (!_disposed && Path is { } path)
        {
            SendControl(MediaPacketType.Close, System.Text.Encoding.UTF8.GetBytes(reason), path.Remote);
        }
    }

    // ---------------------------------------------------------------- receive

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        byte[] buffer = new byte[2048];
        var from = new System.Net.SocketAddress(AddressFamily.InterNetworkV6);
        while (!ct.IsCancellationRequested)
        {
            int n;
            try
            {
                n = await _socket.ReceiveFromAsync(buffer, from, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException e)
            {
                _log.LogDebug(e, "Media socket receive failed");
                continue;
            }

            IPEndPoint sender = UdpDatagramSocket.ToEndPoint(from);
            try
            {
                HandleDatagram(buffer.AsSpan(0, n), sender);
            }
            catch (Exception e)
            {
                _log.LogWarning(e, "Media datagram handling failed");
            }
        }
    }

    private void HandleDatagram(ReadOnlySpan<byte> datagram, IPEndPoint sender)
    {
        if (RelayDatagram.TryRead(datagram, out RelayDatagramType relayType, out ReadOnlySpan<byte> token))
        {
            if (_relay is not null && sender.Equals(_relay) && _relayToken is not null && token.SequenceEqual(_relayToken) && relayType == RelayDatagramType.BindAck)
            {
                _relayPaired = true;
                _log.LogDebug("Relay {Relay} paired the media channel", _relay);
            }

            return;
        }

        if (!_cipher.TryOpen(datagram, out MediaCommonHeader header, _plain, out int length))
        {
            DatagramsRejected++;
            return;
        }

        DatagramsReceived++;
        long now = _time.GetTimestamp();
        if (!_isHost)
        {
            lock (_arrivals)
            {
                _arrivals.Add(new TransportFeedback.Arrival(header.PacketSeq, ToMicros(now)));
            }
        }

        _lastReceive = now;
        _lastReceivedPacketSeq = header.PacketSeq;
        _lastReceivedAt = now;
        _link.NotePacket(header.PacketSeq, datagram.Length);
        ReadOnlySpan<byte> plain = _plain.AsSpan(0, length);
        switch (header.Type)
        {
            case MediaPacketType.Bind:
                OnBind(plain, sender);
                break;
            case MediaPacketType.BindAck:
                OnBindAck(plain, sender);
                break;
            case MediaPacketType.Ping:
                SendControl(MediaPacketType.Pong, plain, sender);
                break;
            case MediaPacketType.Pong:
            case MediaPacketType.Padding:
                break;
            case MediaPacketType.Video:
                MaybeMigrate(sender, header.PacketSeq);
                _streams.Accept(in header, plain);
                DrainFrames();
                break;
            case MediaPacketType.Feedback:
                if (MediaFeedback.TryRead(plain, out MediaFeedback feedback))
                {
                    _lastFeedbackReceive = now;
                    FeedbackReceived?.Invoke(feedback, RoundTripFromEcho(feedback, now));
                }

                break;
            case MediaPacketType.TransportFeedback:
                if (_isHost)
                {
                    OnTransportFeedback(plain, now);
                }

                break;
            case MediaPacketType.Close:
                MarkDead("peer closed the media channel");
                break;
        }
    }

    private void OnTransportFeedback(ReadOnlySpan<byte> payload, long now)
    {
        if (!TransportFeedback.TryRead(payload, _reportRead, out _) || _reportRead.Count == 0)
        {
            return;
        }

        // The report's bitmap counts every missing sequence number as lost, but many never went towards this path:
        // Bind probes to the other candidates (and pings to them) share the sequence space. Count only packets we
        // actually sent on the path, or path setup alone reads as >10 % loss and the loss-based estimate collapses.
        int lost = CountLostOnPath(_reportRead);

        _reportResults.Clear();
        ProbeCluster? probe;
        lock (_probeLock)
        {
            probe = _probe;
        }

        ulong highestSeq = 0;
        foreach (TransportFeedback.Arrival a in _reportRead)
        {
            highestSeq = Math.Max(highestSeq, a.PacketSeq);
            int slot = (int)(a.PacketSeq % SentRing);
            if (_sentSeq[slot] == a.PacketSeq && _sentAt[slot] != 0)
            {
                var result = new PacketResult(ToMicros(_sentAt[slot]), a.ArrivalUs, _sentSize[slot]);
                _reportResults.Add(result);
                if (probe is not null && a.PacketSeq >= probe.FirstSeq && a.PacketSeq <= probe.LastSeq)
                {
                    _probeResults.Add(result);
                }
            }
        }

        _reportResults.Sort(static (x, y) => x.SendUs.CompareTo(y.SendUs));
        double target = _gcc.OnFeedback(_reportResults, lost, ToMicros(now));
        if (probe is not null && highestSeq >= probe.LastSeq)
        {
            target = FinishProbe(probe, now);
        }

        _sender.PacingBytesPerSecond = target * PacingFactor / 8;
        BandwidthEstimated?.Invoke(target);
    }

    // ---------------------------------------------------------------- bandwidth probing (host)

    private sealed record ProbeCluster(int Id, ulong FirstSeq, ulong LastSeq, int Count, double RateBps, long SentAt, bool Initial);

    /// <summary>
    /// Host timer: a desktop is idle most of the time, so the delivered rate alone never proves the link can carry a
    /// window drag at full quality. Like WebRTC's ProbeController, send short padding bursts at a higher rate: at
    /// start (growing exponentially while they succeed) and every few seconds while the stream is application-limited.
    /// </summary>
    private void MaybeProbe(long now)
    {
        if (!_isHost || !IsReady)
        {
            return;
        }

        lock (_probeLock)
        {
            if (_probe is { } inFlight)
            {
                if (_time.GetElapsedTime(inFlight.SentAt, now) > ProbeTimeout)
                {
                    LastProbe = $"{inFlight.RateBps / 1e6:F1} Mb/s timed out";
                    _probe = null;
                    _probeResults.Clear();
                    ProbeFailed(inFlight, now);
                }

                return;
            }

            double target = _gcc.TargetBps;
            if (!_initialProbeDone)
            {
                // Wait for the host to say what the picture can use (first feedback), but not forever.
                _readyAt = _readyAt == 0 ? now : _readyAt;
                if ((!_ceilingKnown && _time.GetElapsedTime(_readyAt, now) < TimeSpan.FromSeconds(1)) || now < _nextProbeAt)
                {
                    return;
                }

                _initialProbeDone = true;
                if (target < 0.95 * _ceilingBps)
                {
                    SendProbe(Math.Min(_ceilingBps, 2 * target), initial: true, now);
                }

                return;
            }

            if (target >= 0.95 * _ceilingBps)
            {
                return;
            }

            // Idle (application-limited) and healthy: check whether the link could take more than we have proven.
            // Loss up to 4% (twice the usual gate) still probes -- FEC repairs it, so a little loss should not stop
            // the estimate from climbing on a direct path.
            bool appLimited = (_gcc.AckedBps ?? 0) < 0.5 * target;
            if (now >= _nextProbeAt && appLimited && _gcc.State != BandwidthUsage.Overusing && _gcc.LossFraction < 0.04)
            {
                SendProbe(Math.Min(_ceilingBps, 2 * target), initial: false, now);
            }
        }
    }

    private void SendProbe(double rateBps, bool initial, long now)
    {
        int payload = ProtocolConstants.MaxUdpDatagramBytes - ProtocolConstants.MediaCommonHeaderBytes - ProtocolConstants.MediaTagBytes;
        int count = Math.Max(10, (int)Math.Ceiling(rateBps * ProbeDuration.TotalSeconds / 8 / ProtocolConstants.MaxUdpDatagramBytes));
        var datagrams = new List<PooledDatagram>(count);
        byte[] zeros = new byte[payload];
        ulong first = 0, last = 0;
        lock (_cipher)
        {
            for (int i = 0; i < count; i++)
            {
                byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(ProtocolConstants.MaxUdpDatagramBytes);
                int n = _cipher.Seal(MediaPacketType.Padding, MediaPacketFlags.None, zeros, buffer, out ulong seq);
                datagrams.Add(new PooledDatagram(buffer, n));
                if (i == 0)
                {
                    first = seq;
                }

                last = seq;
            }
        }

        // The cluster's rate comes from its duration: count packets spread so they span exactly this long.
        var spread = TimeSpan.FromSeconds(count * ProtocolConstants.MaxUdpDatagramBytes * 8 / rateBps);
        _probeSeqLow = first;
        _probeSeqHigh = last;
        for (ulong seq = first; seq <= last; seq++)
        {
            _sentProbe[(int)(seq % SentRing)] = true;
        }

        _probe = new ProbeCluster(++_probeClusterId, first, last, count, rateBps, now, initial);
        _probeResults.Clear();
        _lastSend = now;
        _sender.EnqueueProbe(datagrams, spread);
    }

    /// <summary>Receive thread: the report covering the cluster's last packet arrived; evaluate and schedule the next probe.</summary>
    private double FinishProbe(ProbeCluster probe, long now)
    {
        double target;
        lock (_probeLock)
        {
            if (!ReferenceEquals(_probe, probe))
            {
                return _gcc.TargetBps;
            }

            double? estimate = ProbeBitrateEstimator.Estimate(_probeResults, probe.Count);
            int received = _probeResults.Count;
            // The link could not carry the burst (it dropped part of it, or delivered it well below the probe rate):
            // the measurement is what it really carries, so it may bring the estimate down as well as up.
            bool saturated = received < probe.Count * 0.8 || estimate < 0.7 * probe.RateBps;
            _probe = null;
            _probeResults.Clear();
            target = estimate is { } bps ? _gcc.OnProbeResult(bps, saturated) : _gcc.TargetBps;
            LastProbe = estimate is { } e
                ? $"{probe.RateBps / 1e6:F1} Mb/s measured {e / 1e6:F1} ({received}/{probe.Count}{(saturated ? ", saturated" : string.Empty)})"
                : $"{probe.RateBps / 1e6:F1} Mb/s unusable ({received}/{probe.Count})";
            _log.LogInformation("Media probe {Id}: {Result}, gcc now {Target:F1} Mb/s", probe.Id, LastProbe, target / 1e6);

            // A probe that got most of its rate through may be followed by a bigger one right away (start-up only
            // grows exponentially); otherwise wait before probing again.
            bool reachedRate = estimate is not null && !saturated;
            if (probe.Initial && reachedRate && probe.RateBps < _ceilingBps * 0.95)
            {
                SendProbe(Math.Min(_ceilingBps, 2 * estimate!.Value), initial: true, now);
            }
            else if (estimate is null)
            {
                ProbeFailed(probe, now);
            }
            else
            {
                _nextProbeAt = now + (long)(IdleProbeInterval.TotalSeconds * _time.TimestampFrequency);
            }
        }

        return target;
    }

    /// <summary>
    /// A probe that timed out or measured nothing waits <see cref="FailedProbeBackoff"/> before the next -- except at
    /// start-up. After it, probes go out only while the stream is idle, so a stream busy from its first second (a window
    /// being dragged, a video playing) would ramp slowly for as long as it stays busy, on the strength of one burst the
    /// machine happened to be too busy to send at its rate. A failed start-up probe is tried again a second later, a
    /// few times. Under <see cref="_probeLock"/>.
    /// </summary>
    private void ProbeFailed(ProbeCluster probe, long now)
    {
        if (probe.Initial && ++_startUpProbeFailures < StartUpProbeAttempts)
        {
            _initialProbeDone = false;
            _nextProbeAt = now + (long)(StartUpProbeRetry.TotalSeconds * _time.TimestampFrequency);
        }
        else
        {
            _nextProbeAt = now + (long)(FailedProbeBackoff.TotalSeconds * _time.TimestampFrequency);
        }
    }

    private int CountLostOnPath(List<TransportFeedback.Arrival> received)
    {
        int lost = 0;
        int next = 0;
        ulong first = received[0].PacketSeq, last = received[^1].PacketSeq;
        for (ulong seq = first; seq <= last; seq++)
        {
            if (next < received.Count && received[next].PacketSeq == seq)
            {
                next++;
                continue;
            }

            // A probe burst is meant to find the point where the link starts dropping; its own losses are the probe's
            // result, not evidence that the video stream is too fast, so they never reach the loss-based estimate.
            int slot = (int)(seq % SentRing);
            if (_sentSeq[slot] == seq && _sentOnPath[slot] && !_sentProbe[slot])
            {
                lost++;
            }
        }

        return lost;
    }

    private long ToMicros(long timestamp) => (long)(timestamp * (1_000_000.0 / _time.TimestampFrequency));

    private void OnBind(ReadOnlySpan<byte> payload, IPEndPoint sender)
    {
        if (payload.Length < 8 || BinaryPrimitives.ReadUInt32LittleEndian(payload) != ChannelId)
        {
            return;
        }

        // Answer to the source address (peer-reflexive), and learn it as a candidate so our own probes reach it.
        uint priority = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
        lock (_lock)
        {
            if (!_remotes.ContainsKey(sender))
            {
                _remotes[sender] = (KindFromPriority(priority), priority);
            }
        }

        Span<byte> ack = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(ack, ChannelId);
        BinaryPrimitives.WriteUInt32LittleEndian(ack[4..], priority);
        SendControl(MediaPacketType.BindAck, ack, sender);
    }

    private void OnBindAck(ReadOnlySpan<byte> payload, IPEndPoint sender)
    {
        if (payload.Length < 8 || BinaryPrimitives.ReadUInt32LittleEndian(payload) != ChannelId)
        {
            return;
        }

        (MediaCandidate.Types.Kind kind, uint priority) info;
        lock (_lock)
        {
            if (!_remotes.TryGetValue(sender, out info))
            {
                info = (KindFromPriority(BinaryPrimitives.ReadUInt32LittleEndian(payload[4..])), BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]));
            }
        }

        MediaPath candidate = new(info.kind, sender);
        bool promote;
        lock (_lock)
        {
            promote = Path is null || (info.priority > PriorityOf(Path.Kind) && _time.GetElapsedTime(_probeStart) <= PromotionGrace);
            if (promote)
            {
                Path = candidate;
            }
        }

        if (promote)
        {
            _log.LogInformation("Media path verified: {Kind} via {Remote}", candidate.Kind, candidate.Remote);
            PathVerified?.Invoke(candidate);
        }
    }

    private void MaybeMigrate(IPEndPoint sender, ulong packetSeq)
    {
        // Direct paths follow the peer if its address changes (authenticated packets cannot be spoofed); relayed ones are pinned.
        if (Path is { } path && path.Kind != MediaCandidate.Types.Kind.Relay && !path.Remote.Equals(sender) && packetSeq > _lastReceivedPacketSeq)
        {
            Path = new MediaPath(path.Kind, sender);
            _log.LogInformation("Media path migrated to {Remote}", sender);
        }
    }

    private void DrainFrames()
    {
        while (_streams.TryDequeue(out AssembledFrame frame))
        {
            FrameReceived?.Invoke(frame);
        }

        if (!_streams.ReferenceBroken)
        {
            return;
        }

        // Per stream: a keyframe of the display that broke, not of whichever one happened to arrive last.
        // Called from both the receive and the timer thread, hence the lock around the shared scratch list.
        lock (_refreshStreams)
        {
            _streams.CopyStreams(_refreshStreams);
            foreach (FrameAssembler stream in _refreshStreams)
            {
                if (stream.ReferenceBroken && _time.GetElapsedTime(_lastRefreshRequest.GetValueOrDefault(stream.Stream)) > RefreshRateLimit)
                {
                    _lastRefreshRequest[stream.Stream] = _time.GetTimestamp();
                    RefreshRequested?.Invoke(stream.Stream);
                }
            }
        }
    }

    // ---------------------------------------------------------------- send helpers

    private void SendControl(MediaPacketType type, ReadOnlySpan<byte> payload, IPEndPoint target)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            lock (_cipher)
            {
                int n = _cipher.Seal(type, MediaPacketFlags.None, payload, _datagram, out ulong packetSeq);
                _sender.SendNow(_datagram.AsSpan(0, n), target);
                RecordSent(packetSeq, _time.GetTimestamp(), n, onPath: Path is { } path && target.Equals(path.Remote));
            }
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        _lastSend = _time.GetTimestamp();
    }

    private void RecordSent(ulong packetSeq, long timestamp, int size, bool onPath)
    {
        // The pacer thread and control senders both land here; the sequence is written last so a reader that
        // sees it matching also sees this packet's time and size (a torn slot only costs one sample).
        int slot = (int)(packetSeq % SentRing);
        _sentSeq[slot] = 0;
        _sentAt[slot] = timestamp;
        _sentSize[slot] = size;
        _sentOnPath[slot] = onPath;
        _sentProbe[slot] = packetSeq >= _probeSeqLow && packetSeq <= _probeSeqHigh;
        _sentSeq[slot] = packetSeq;
    }

    private TimeSpan? RoundTripFromEcho(in MediaFeedback feedback, long now)
    {
        if (feedback.EchoPacketSeq == 0)
        {
            return null;
        }

        int slot = (int)(feedback.EchoPacketSeq % SentRing);
        if (_sentSeq[slot] != feedback.EchoPacketSeq || _sentAt[slot] == 0)
        {
            return null;
        }

        TimeSpan rtt = _time.GetElapsedTime(_sentAt[slot], now) - TimeSpan.FromMicroseconds(feedback.EchoDelayMicros);
        if (rtt < TimeSpan.Zero)
        {
            rtt = TimeSpan.Zero;
        }

        LastRttMs = rtt.TotalMilliseconds;
        return rtt;
    }

    private void Probe()
    {
        List<IPEndPoint> targets;
        lock (_lock)
        {
            targets = _remotes.Keys.ToList();
        }

        Span<byte> bind = stackalloc byte[8];
        Span<byte> pairing = stackalloc byte[RelayDatagram.MaxSize];
        foreach (IPEndPoint target in targets)
        {
            bool isRelay = _relay is not null && target.Equals(_relay);
            if (isRelay)
            {
                if (!_relayPaired)
                {
                    if (_relayTicket is not null)
                    {
                        int length = RelayDatagram.WriteBindTicket(pairing, _relayToken, _relayTicket);
                        _sender.SendNow(pairing[..length], target);
                    }
                    else
                    {
                        RelayDatagram.Write(pairing, RelayDatagramType.Bind, _relayToken);
                        _sender.SendNow(pairing[..RelayDatagram.Size], target);
                    }

                    continue;
                }
            }

            uint priority;
            lock (_lock)
            {
                priority = _remotes.TryGetValue(target, out (MediaCandidate.Types.Kind Kind, uint Priority) info) ? info.Priority : 0;
            }

            BinaryPrimitives.WriteUInt32LittleEndian(bind, ChannelId);
            BinaryPrimitives.WriteUInt32LittleEndian(bind[4..], priority);
            SendControl(MediaPacketType.Bind, bind, target);
        }
    }

    private async Task TimerLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(FeedbackInterval, _time);
        long lastProbe = 0;
        byte[] ping = new byte[8];
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                long now = _time.GetTimestamp();
                bool probing;
                lock (_lock)
                {
                    probing = _remotes.Count > 0 && (Path is null || _time.GetElapsedTime(_probeStart) <= PromotionGrace);
                }

                if (probing && _time.GetElapsedTime(lastProbe) >= ProbeInterval)
                {
                    lastProbe = now;
                    Probe();
                }

                if (Path is { } path)
                {
                    MaybeProbe(now);
                    if (!_isHost)
                    {
                        _streams.Tick();
                        DrainFrames();
                        if (_time.GetElapsedTime(_lastReceivedAt) < TimeSpan.FromSeconds(1))
                        {
                            SendFeedback(path.Remote);
                        }

                        if (_time.GetElapsedTime(_lastTransportFeedback) >= TransportFeedbackInterval)
                        {
                            _lastTransportFeedback = now;
                            SendTransportFeedback(path.Remote);
                        }
                    }

                    if (_time.GetElapsedTime(_lastSend) >= ProtocolConstants.MediaKeepalive)
                    {
                        BinaryPrimitives.WriteInt64LittleEndian(ping, now);
                        SendControl(MediaPacketType.Ping, ping, path.Remote);
                    }

                    if (_time.GetElapsedTime(_lastReceive) >= ProtocolConstants.MediaDeadAfter)
                    {
                        MarkDead("no datagrams from the peer");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Media channel timer loop stopped");
        }
    }

    /// <summary>
    /// One report per stream. Datagram frames are acknowledged by these reports alone, so each must name its own
    /// stream: a report that said stream 0 while display 1 was on screen left every frame of display 1
    /// unacknowledged, and the host stopped sending it as soon as its in-flight limit filled. The link's fields go
    /// in every report but count once: all but the first say so.
    /// </summary>
    private void SendFeedback(IPEndPoint target)
    {
        long now = _time.GetTimestamp();
        uint echoDelay = _lastReceivedPacketSeq == 0 ? 0 : (uint)Math.Min(uint.MaxValue, _time.GetElapsedTime(_lastReceivedAt, now).TotalMicroseconds);
        ulong clock = (ulong)(now * 1_000_000.0 / _time.TimestampFrequency);
        _streams.CopyStreams(_reportStreams);
        Span<byte> payload = stackalloc byte[MediaFeedback.Size];
        if (_reportStreams.Count == 0)
        {
            // Nothing assembled yet: the link still has something to say.
            new MediaFeedback(0, (ushort)_link.LossPermille, 0, 0, _lastReceivedPacketSeq, echoDelay, _link.ReceivedBytes, clock, 0, 0).Write(payload);
            SendControl(MediaPacketType.Feedback, payload, target);
            return;
        }

        for (int i = 0; i < _reportStreams.Count; i++)
        {
            FrameAssembler stream = _reportStreams[i];
            bool first = i == 0;
            new MediaFeedback(
                stream.Stream,
                (ushort)_link.LossPermille,
                stream.HighestDecodable,
                stream.LastFrameSeqReceived,
                first ? _lastReceivedPacketSeq : 0,
                first ? echoDelay : 0,
                _link.ReceivedBytes,
                clock,
                stream.FramesGivenUp,
                stream.ShardsRecovered,
                first ? MediaFeedbackFlags.None : MediaFeedbackFlags.LinkFieldsIgnored).Write(payload);
            SendControl(MediaPacketType.Feedback, payload, target);
        }
    }

    private void SendTransportFeedback(IPEndPoint target)
    {
        _arrivalsOut.Clear();
        lock (_arrivals)
        {
            _arrivalsOut.AddRange(_arrivals);
            _arrivals.Clear();
        }

        _arrivalsOut.Sort(static (x, y) => x.PacketSeq.CompareTo(y.PacketSeq));
        int maxPayload = ProtocolConstants.MaxUdpDatagramBytes - ProtocolConstants.MediaCommonHeaderBytes - ProtocolConstants.MediaTagBytes;
        int start = 0;
        while (start < _arrivalsOut.Count)
        {
            // Packets at or below the last reported sequence arrived out of order and were already reported as lost.
            if (_arrivalsOut[start].PacketSeq <= _highestReportedSeq)
            {
                start++;
                continue;
            }

            int end = start + 1;
            ulong baseSeq = _arrivalsOut[start].PacketSeq;
            while (end < _arrivalsOut.Count && _arrivalsOut[end].PacketSeq - baseSeq < TransportFeedback.MaxPacketsPerReport && end - start < MaxArrivalsPerReport)
            {
                end++;
            }

            ReadOnlySpan<TransportFeedback.Arrival> slice = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_arrivalsOut).Slice(start, end - start);
            int n = TransportFeedback.Write(slice, _reportBuffer);
            if (n <= maxPayload)
            {
                SendControl(MediaPacketType.TransportFeedback, _reportBuffer.AsSpan(0, n), target);
            }

            _highestReportedSeq = slice[^1].PacketSeq;
            start = end;
        }
    }

    private void MarkDead(string reason)
    {
        bool first;
        lock (_lock)
        {
            first = !_dead;
            _dead = true;
        }

        if (first)
        {
            _log.LogInformation("Media channel dead: {Reason}", reason);
            Dead?.Invoke(reason);
        }
    }

    private static uint PriorityOf(MediaCandidate.Types.Kind kind) => kind switch
    {
        MediaCandidate.Types.Kind.Local => CandidateGatherer.LocalPriority,
        MediaCandidate.Types.Kind.Reflexive => CandidateGatherer.ReflexivePriority,
        _ => CandidateGatherer.RelayPriority,
    };

    private static MediaCandidate.Types.Kind KindFromPriority(uint priority) => priority switch
    {
        >= CandidateGatherer.LocalPriority => MediaCandidate.Types.Kind.Local,
        >= CandidateGatherer.ReflexivePriority => MediaCandidate.Types.Kind.Reflexive,
        _ => MediaCandidate.Types.Kind.Relay,
    };

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _cts.CancelAsync().ConfigureAwait(false);
        _sender.Dispose();
        _socket.Dispose();
        foreach (Task? t in new[] { _receiveLoop, _timerLoop })
        {
            if (t is not null)
            {
                try
                {
                    await t.ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }
        }

        _cipher.Dispose();
        _cts.Dispose();
    }
}
