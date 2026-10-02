using DeskPair.Protocol.Messages;

namespace DeskPair.Core.Qos;

/// <summary>How good the link to the slowest viewer looks; drives how much lossless content is sent directly.</summary>
public enum LinkTier
{
    Poor,
    Fair,
    Good,
    Excellent,
}

/// <summary>
/// Adapts frame rate and bitrate to the slowest viewer. Delay samples come from TestDelay round trips;
/// congestion lowers bitrate first (frames are cheaper than bytes under CBR) and then fps; sustained
/// good samples restore both gradually. All timing goes through <see cref="TimeProvider"/>.
/// </summary>
public sealed class VideoQosController
{
    public const int MinFps = 5;
    public const int DefaultFps = 60;
    public const int MaxFps = 120;
    public const double MinRatio = 0.2;
    public const double MaxRatio = 1.0;
    public static readonly TimeSpan AdjustInterval = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan TierInterval = TimeSpan.FromMilliseconds(500);
    /// <summary>Round-trip samples averaged for the current delay estimate.</summary>
    public static readonly TimeSpan AverageWindow = TimeSpan.FromMilliseconds(500);
    /// <summary>How far back the minimum round trip (the propagation baseline) is remembered.</summary>
    public static readonly TimeSpan BaselineWindow = TimeSpan.FromSeconds(10);
    private const int SentRing = 256;

    /// <summary>Bits per pixel a moving picture needs to stay sharp at 4:2:0 (measured on real drags).</summary>
    public const double MotionBitsPerPixel = 0.35;

    /// <summary>
    /// How far above the measured need the bitrate ceiling is allowed to reach, so a fat direct path (a LAN, fibre)
    /// is not held to the "just sharp enough" rate and can go near-lossless. It only lifts the cap; GCC and the
    /// balanced table still decide the actual rate on an ordinary link, so a thin path is unaffected.
    /// </summary>
    public const double CeilingHeadroom = 10.0;

    /// <summary>Nothing above this is asked of a link or an encoder, whatever the picture size.</summary>
    public const int AbsoluteMaxKbps = 2_000_000;

    // Congestion thresholds are set at twice the measured "starts to hurt" points: the controller is deliberately
    // half as sensitive, so a transient hiccup on a direct path does not cut quality -- it takes sustained, real
    // congestion (twice the delay, twice the loss) to make it back off. GoodDelay (when to raise again) is left
    // where it is, so recovery stays as quick as before.
    /// <summary>Queueing delay (round trip above the recent minimum) that counts as congestion.</summary>
    public static readonly TimeSpan CongestedDelay = TimeSpan.FromMilliseconds(200);
    /// <summary>Queueing delay under which the link is considered to have headroom.</summary>
    public static readonly TimeSpan GoodDelay = TimeSpan.FromMilliseconds(40);
    /// <summary>Absolute round trip beyond which the link is treated as congested whatever its baseline.</summary>
    public static readonly TimeSpan PathologicalDelay = TimeSpan.FromMilliseconds(600);

    /// <summary>A round trip this long is treated as severe -- once it has lasted <see cref="SevereFor"/>.</summary>
    public static readonly TimeSpan SevereDelay = TimeSpan.FromSeconds(2);

    /// <summary>Packet loss (fraction) that counts as congestion -- half as sensitive as the measured 5% starting point.</summary>
    public const double CongestionLoss = 0.10;

    /// <summary>Packet loss (fraction) that is treated as a bad link -- half as sensitive as the measured 10% point.</summary>
    public const double SevereLoss = 0.20;

    /// <summary>
    /// How long severe samples must keep coming before the link is judged, rather than the viewer. A phone
    /// that comes back from the background answers every heartbeat it missed at once, each a round trip of
    /// seconds; those measure its absence, not the network, and used to halve the bitrate and frame rate
    /// on the spot -- after which a static screen stayed at that blurry keyframe. Real congestion at this
    /// level keeps producing such samples; a burst that stops within a second was a viewer away.
    /// </summary>
    public static readonly TimeSpan SevereFor = TimeSpan.FromSeconds(1);

    /// <summary>
    /// A viewer heard nothing from for this long was away, not congested: what it reported just before it
    /// went quiet (a burst of loss as a phone was being suspended) is stale, and its first samples back
    /// start the averages afresh.
    /// </summary>
    public static readonly TimeSpan SilenceResets = TimeSpan.FromSeconds(2);

    private readonly object _lock = new();
    private readonly Dictionary<int, User> _users = new();
    private readonly TimeProvider _time;
    private DateTimeOffset _lastAdjust;
    private int _fps = DefaultFps;
    private double _ratio = MaxRatio;
    private double _probe = 1.0;
    private int _ceilingKbps;
    private readonly Dictionary<int, double> _ceilingByDisplay = new();
    private readonly Dictionary<int, long> _pixelsByDisplay = new();
    private LinkTier _tier = LinkTier.Fair;
    private DateTimeOffset _lastTierEval;

    public VideoQosController(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _lastAdjust = _time.GetUtcNow();
    }

    private sealed class User
    {
        public int FpsCap = DefaultFps;
        public ImageQuality Quality = ImageQuality.IqBalanced;
        public bool Refinement = true;
        public int CustomBitrateKbps;
        public readonly Queue<(DateTimeOffset At, TimeSpan Rtt)> Delays = new();
        public readonly Queue<(DateTimeOffset At, TimeSpan Rtt)> RttHistory = new();
        public int GoodStreak;
        public readonly Dictionary<int, StreamState> Streams = new();
        public TimeSpan? LastRoundTrip;
        /// <summary>When the current run of severe samples began; default when the last sample was ordinary.</summary>
        public DateTimeOffset SevereSince;
        public DateTimeOffset LastSampleAt;
        public double Loss;
        /// <summary>GCC bandwidth estimate from the UDP media channel (bits per second); null on the TCP path.</summary>
        public double? BandwidthBps;
        /// <summary>The displays streamed to this viewer, and the one with its attention (-1 for none).</summary>
        public int[] Displays = [];
        public int Focus = -1;
    }

    /// <summary>Per-display send/ack counters plus a ring of send timestamps so every ack yields a round-trip sample.</summary>
    private sealed class StreamState
    {
        public uint Sent;
        public uint Acked;
        public uint GivenUp;
        public uint BrokenAtSeq;
        public readonly long[] SentAt = new long[SentRing];
        public readonly uint[] SeqAt = new uint[SentRing];
    }

    /// <summary>Current target frame rate across all viewers.</summary>
    public int Fps
    {
        get
        {
            lock (_lock)
            {
                return _fps;
            }
        }
    }

    public TimeSpan FrameInterval => TimeSpan.FromSeconds(1.0 / Math.Max(1, Fps));

    /// <summary>Bitrate multiplier in [0.2, 1.0] applied to the quality-derived base bitrate.</summary>
    public double Ratio
    {
        get
        {
            lock (_lock)
            {
                return _ratio;
            }
        }
    }

    /// <summary>Floor for the unacknowledged-frame limit; the actual limit grows with fps × round trip (see IsCongested).</summary>
    public int MaxInFlight { get; init; } = 6;

    /// <summary>Link quality derived from round trips and acknowledgement backlog; re-evaluated on each delay sample.</summary>
    public LinkTier Tier
    {
        get
        {
            lock (_lock)
            {
                return _tier;
            }
        }
    }

    /// <summary>Multiplier the bitrate probe has climbed to (1.0 = table value); capped by the tier.</summary>
    public double Probe
    {
        get
        {
            lock (_lock)
            {
                return _probe;
            }
        }
    }

    /// <summary>Test/diagnostic override of the measured tier.</summary>
    public LinkTier? TierOverride { get; set; }

    /// <summary>Highest bitrate multiplier (over the balanced table) a tier may probe up to.</summary>
    public static double MaxProbe(LinkTier tier) => tier switch
    {
        LinkTier.Excellent => 1.5,
        LinkTier.Good => 1.5,
        LinkTier.Fair => 1.25,
        _ => 1.0,
    };

    /// <summary>Fraction of the screen's tiles that may change in one frame and still go out losslessly instead of as video.</summary>
    public static double TileThresholdFraction(LinkTier tier) => tier switch
    {
        LinkTier.Excellent => 0.6,
        LinkTier.Good => 0.2,
        LinkTier.Fair => 0.08,
        _ => 0.0,
    };

    /// <summary>How long the screen must be still before lossless refinement of lossy tiles starts.</summary>
    public static TimeSpan RefinementDelay(LinkTier tier) => tier switch
    {
        LinkTier.Excellent => TimeSpan.Zero,
        LinkTier.Good => TimeSpan.FromMilliseconds(300),
        LinkTier.Fair => TimeSpan.FromSeconds(1),
        _ => TimeSpan.FromSeconds(2),
    };

    /// <summary>Bytes per second the video service may spend (video plus tiles) for the given picture size.</summary>
    public long BytesPerSecondBudget(int width, int height) => TargetBitrateKbps(width, height) * 1000L / 8;

    /// <summary>Bytes per second the video service of <paramref name="display"/> may spend (video plus tiles).</summary>
    public long BytesPerSecondBudget(int display, int width, int height) => TargetBitrateKbps(display, width, height) * 1000L / 8;

    /// <summary>
    /// How much more of a viewer's link its focused display gets than each of the others. It is the one being
    /// looked at and typed into; the others are mostly glanced at.
    /// </summary>
    public const double FocusWeight = 1.5;

    /// <summary>
    /// Highest bitrate worth probing for on one viewer's link: the sum of what each of its displays can use, since
    /// one link carries them all. With one display this is <see cref="BitrateCeilingKbps"/>.
    /// </summary>
    public int BitrateCeilingKbpsFor(int connectionId)
    {
        lock (_lock)
        {
            if (!_users.TryGetValue(connectionId, out User? u) || u.Displays.Length == 0)
            {
                return _ceilingKbps;
            }

            double sum = 0;
            foreach (int d in u.Displays)
            {
                sum += _ceilingByDisplay.GetValueOrDefault(d, _ceilingKbps);
            }

            return (int)sum;
        }
    }

    /// <summary>False when any viewer opted out of lossless refinement or asked for the lowest quality.</summary>
    public bool LosslessRefinementEnabled
    {
        get
        {
            lock (_lock)
            {
                return _users.Values.All(u => u.Refinement && u.Quality != ImageQuality.IqLow);
            }
        }
    }

    public void AddUser(int connectionId, SessionOptions? options = null)
    {
        lock (_lock)
        {
            var u = new User();
            _users[connectionId] = u;
            if (options is not null)
            {
                Apply(u, options);
            }

            Recompute();
        }
    }

    public void RemoveUser(int connectionId)
    {
        lock (_lock)
        {
            _users.Remove(connectionId);
            Recompute();
        }
    }

    public void UpdateOptions(int connectionId, SessionOptions options)
    {
        lock (_lock)
        {
            if (_users.TryGetValue(connectionId, out User? u))
            {
                Apply(u, options);
                Recompute();
            }
        }
    }

    public void ReportDelay(int connectionId, TimeSpan roundTrip)
    {
        lock (_lock)
        {
            if (_users.TryGetValue(connectionId, out User? u))
            {
                Record(u, roundTrip);
            }
        }
    }

    /// <summary>Packet loss the viewer measured before FEC on the UDP channel (fraction); loss above <see cref="CongestionLoss"/> counts as congestion.</summary>
    public void ReportLoss(int connectionId, double fraction)
    {
        lock (_lock)
        {
            if (_users.TryGetValue(connectionId, out User? u))
            {
                fraction = Math.Clamp(fraction, 0, 1);
                u.Loss = u.Loss == 0 ? fraction : 0.7 * u.Loss + 0.3 * fraction;
            }
        }
    }

    /// <summary>
    /// GCC estimate for a viewer on the UDP channel (bits per second), or null when the viewer is back on TCP.
    /// While set, it replaces the round-trip probe for that viewer's share of the bitrate target.
    /// </summary>
    public void ReportBandwidth(int connectionId, double? bitsPerSecond)
    {
        lock (_lock)
        {
            if (_users.TryGetValue(connectionId, out User? u))
            {
                u.BandwidthBps = bitsPerSecond;
            }
        }
    }

    /// <summary>
    /// Highest bitrate the last computed target could use (resolution and quality cap, or the custom bitrate);
    /// bandwidth probing stops there. 0 until a video service asked for a target.
    /// </summary>
    public int BitrateCeilingKbps
    {
        get
        {
            lock (_lock)
            {
                return _ceilingKbps;
            }
        }
    }

    /// <summary>The lowest GCC estimate among viewers on UDP (bits per second); null when none is.</summary>
    public double? BandwidthEstimateBps
    {
        get
        {
            lock (_lock)
            {
                double? min = null;
                foreach (User u in _users.Values)
                {
                    if (u.BandwidthBps is { } b)
                    {
                        min = min is { } m ? Math.Min(m, b) : b;
                    }
                }

                return min;
            }
        }
    }

    /// <summary>Most recent round-trip sample for a viewer (from a ping or a frame ack); diagnostics and tests.</summary>
    public TimeSpan? LastRoundTrip(int connectionId)
    {
        lock (_lock)
        {
            return _users.TryGetValue(connectionId, out User? u) ? u.LastRoundTrip : null;
        }
    }

    private void Record(User u, TimeSpan roundTrip)
    {
        DateTimeOffset now = _time.GetUtcNow();
        u.LastRoundTrip = roundTrip;
        if (u.LastSampleAt != default && now - u.LastSampleAt >= SilenceResets)
        {
            u.Delays.Clear();
            u.Loss = 0;
            u.SevereSince = default;
        }

        u.LastSampleAt = now;
        if (roundTrip >= SevereDelay)
        {
            if (u.SevereSince == default)
            {
                u.SevereSince = now;
            }

            if (now - u.SevereSince < SevereFor)
            {
                return; // a burst of late answers, most likely a viewer that was away; not a measurement of the link
            }
        }
        else
        {
            u.SevereSince = default;
        }

        u.Delays.Enqueue((now, roundTrip));
        while (u.Delays.Count > 64)
        {
            u.Delays.Dequeue();
        }

        u.RttHistory.Enqueue((now, roundTrip));
        while (u.RttHistory.Count > 0 && (u.RttHistory.Count > 1200 || now - u.RttHistory.Peek().At > BaselineWindow))
        {
            u.RttHistory.Dequeue();
        }

        if (roundTrip >= SevereDelay)
        {
            // Severe, and it has lasted: react now instead of waiting for the interval.
            _fps = Math.Max(MinFps, _fps / 2);
            _ratio = Math.Max(MinRatio, _ratio * 0.5);
            _probe = 1.0;
            _tier = LinkTier.Poor;
            _lastAdjust = now;
            return;
        }

        if (now - _lastTierEval >= TierInterval)
        {
            _lastTierEval = now;
            EvaluateTier();
        }

        if (now - _lastAdjust < AdjustInterval)
        {
            return;
        }

        _lastAdjust = now;
        Adjust();
    }

    /// <summary>Sequence numbers are per video service (display), so in-flight accounting is keyed by both.</summary>
    public void FrameSent(int connectionId, int display, uint seq)
    {
        lock (_lock)
        {
            if (_users.TryGetValue(connectionId, out User? u))
            {
                StreamState s = Stream(u, display);
                s.Sent = seq;
                s.SeqAt[seq % SentRing] = seq;
                s.SentAt[seq % SentRing] = _time.GetTimestamp();
            }
        }
    }

    /// <summary>An acknowledged frame moves the in-flight window and, when its send time is still known, yields a round-trip sample.</summary>
    public void FrameAcked(int connectionId, int display, uint seq)
    {
        lock (_lock)
        {
            if (!_users.TryGetValue(connectionId, out User? u))
            {
                return;
            }

            StreamState s = Stream(u, display);
            s.Acked = Math.Max(s.Acked, seq);
            if (s.SeqAt[seq % SentRing] == seq && s.SentAt[seq % SentRing] != 0)
            {
                TimeSpan rtt = _time.GetElapsedTime(s.SentAt[seq % SentRing]);
                s.SentAt[seq % SentRing] = 0;
                Record(u, rtt);
            }
        }
    }

    private static StreamState Stream(User u, int display)
    {
        if (!u.Streams.TryGetValue(display, out StreamState? s))
        {
            s = new StreamState();
            u.Streams[display] = s;
        }

        return s;
    }

    /// <summary>Forgets a stream's counters, e.g. when the viewer switches away from a display.</summary>
    /// <summary>
    /// UDP viewers report how many frames they had to give up (cumulative). A new give-up means their picture
    /// references a frame they never decoded, so it stays broken until a keyframe sent after that point arrives.
    /// </summary>
    public void ReportGivenUp(int connectionId, int display, uint givenUpCumulative, uint highestDecodable)
    {
        lock (_lock)
        {
            if (_users.TryGetValue(connectionId, out User? u))
            {
                StreamState s = Stream(u, display);
                if (givenUpCumulative > s.GivenUp)
                {
                    s.BrokenAtSeq = Math.Max(s.BrokenAtSeq, highestDecodable);
                }

                s.GivenUp = Math.Max(s.GivenUp, givenUpCumulative);
            }
        }
    }

    /// <summary>
    /// True when the viewer has everything up to video frame <paramref name="seq"/> and its reference chain is intact
    /// (no give-up since the keyframe <paramref name="lastKeyFrameSeq"/>). Lossless tiles are only valid on such a picture.
    /// </summary>
    public bool ViewerCaughtUp(int connectionId, int display, uint seq, uint lastKeyFrameSeq)
    {
        lock (_lock)
        {
            if (!_users.TryGetValue(connectionId, out User? u) || !u.Streams.TryGetValue(display, out StreamState? s))
            {
                return false;
            }

            if (s.Acked < seq)
            {
                return false;
            }

            return s.BrokenAtSeq == 0 || (lastKeyFrameSeq > s.BrokenAtSeq && s.Acked >= lastKeyFrameSeq);
        }
    }

    /// <summary>The displays streamed to a viewer and its focus: the link's budget is shared between them.</summary>
    public void SetSubscription(int connectionId, IReadOnlyCollection<int> displays, int focus)
    {
        lock (_lock)
        {
            if (_users.TryGetValue(connectionId, out User? u))
            {
                u.Displays = [.. displays];
                u.Focus = focus;
            }
        }
    }

    public void ResetStream(int connectionId, int display)
    {
        lock (_lock)
        {
            if (_users.TryGetValue(connectionId, out User? u))
            {
                u.Streams.Remove(display);
            }
        }
    }

    public bool IsCongested(int connectionId, int display)
    {
        lock (_lock)
        {
            if (!_users.TryGetValue(connectionId, out User? u) || !u.Streams.TryGetValue(display, out StreamState? s))
            {
                return false;
            }

            return s.Sent > s.Acked && s.Sent - s.Acked > (uint)InFlightLimit(u);
        }
    }

    /// <summary>Target bitrate for the given frame size, honouring the most demanding viewer's quality.</summary>
    public int TargetBitrateKbps(int width, int height) => TargetBitrateKbps(-1, width, height);

    /// <summary>
    /// Target bitrate for <paramref name="display"/>'s stream, honouring the most demanding of the viewers watching
    /// it. What a link can carry -- its GCC estimate, or the viewer's own custom bitrate -- is one budget for all the
    /// displays that viewer watches, so each gets a share by pixels, the focused one weighted by
    /// <see cref="FocusWeight"/>. What a picture needs (the balanced table, the motion cap) is the picture's own and
    /// is not divided. A display of -1 means any display, as though every viewer watched just that one.
    /// </summary>
    public int TargetBitrateKbps(int display, int width, int height)
    {
        lock (_lock)
        {
            if (display >= 0)
            {
                _pixelsByDisplay[display] = (long)width * height;
            }

            double quality = 0.0;
            double custom = 0;
            double? bandwidth = null;
            bool anyTcp = false;
            bool anyone = false;
            foreach (User u in _users.Values)
            {
                if (display >= 0 && u.Displays.Length > 0 && Array.IndexOf(u.Displays, display) < 0)
                {
                    continue;
                }

                anyone = true;
                double share = display >= 0 ? Share(u, display) : 1.0;
                quality = Math.Max(quality, QualityMultiplier(u.Quality));
                if (u.CustomBitrateKbps > 0)
                {
                    custom = Math.Max(custom, u.CustomBitrateKbps * share);
                }

                if (u.BandwidthBps is { } b)
                {
                    bandwidth = bandwidth is { } m ? Math.Min(m, b * share) : b * share;
                }
                else
                {
                    anyTcp = true;
                }
            }

            if (!anyone)
            {
                quality = 1.0;
            }

            double ceiling = custom > 0 ? custom : MaxBitrateKbps(width, height, _fps) * Math.Max(quality, 0.5);
            _ceilingKbps = (int)ceiling;
            if (display >= 0)
            {
                _ceilingByDisplay[display] = ceiling;
            }

            if (custom > 0)
            {
                double customKbps = custom * _ratio;
                return (int)Math.Max(100, bandwidth is { } cb ? Math.Min(customKbps, cb / 1000) : customKbps);
            }

            // Viewers on TCP: the balanced table, probed by round trips. Viewers on UDP: the GCC estimate,
            // capped by what the picture can use at the chosen quality.
            double target = double.MaxValue;
            if (anyTcp || bandwidth is null)
            {
                target = BaseBitrateKbps(width, height) * Math.Max(quality, 0.5) * _probe * _ratio;
            }

            if (bandwidth is { } estimate)
            {
                double cap = MaxBitrateKbps(width, height, _fps) * Math.Max(quality, 0.5);
                target = Math.Min(target, Math.Min(cap, estimate / 1000));
            }

            return (int)Math.Max(100, target);
        }
    }

    /// <summary>
    /// Resolution-to-bitrate table (kbps at balanced quality), interpolated on pixel count. About twice
    /// RustDesk's defaults: desktop text at 4:2:0 needs the headroom to stay crisp, and QoS lowers it on congestion.
    /// </summary>
    public static int BaseBitrateKbps(int width, int height)
    {
        long pixels = (long)width * height;
        (long Pixels, int Kbps)[] table =
        [
            (640L * 480, 800), (800L * 600, 1000), (1024L * 768, 1600), (1280L * 720, 2000), (1280L * 1024, 2400),
            (1600L * 900, 2800), (1920L * 1080, 4000), (2560L * 1440, 6500), (3840L * 2160, 11000), (7680L * 4320, 24000),
        ];
        if (pixels <= table[0].Pixels)
        {
            return table[0].Kbps;
        }

        for (int i = 1; i < table.Length; i++)
        {
            if (pixels <= table[i].Pixels)
            {
                double t = (double)(pixels - table[i - 1].Pixels) / (table[i].Pixels - table[i - 1].Pixels);
                return (int)(table[i - 1].Kbps + t * (table[i].Kbps - table[i - 1].Kbps));
            }
        }

        return table[^1].Kbps;
    }

    /// <summary>Quality preference as a multiplier of the balanced bitrate (and of the GCC cap).</summary>
    public static double QualityMultiplier(ImageQuality quality) => quality switch
    {
        ImageQuality.IqBest => 1.5,
        ImageQuality.IqLow => 0.5,
        _ => 1.0,
    };

    /// <summary>
    /// Highest bitrate worth spending on a picture (probing and GCC stop here): pixels x frame rate x
    /// <see cref="MotionBitsPerPixel"/> x <see cref="CeilingHeadroom"/>. The 0.35 bits/pixel comes from a measured WAN
    /// session where 2560x1440 window drags looked right at about 172 KB per frame; the x10 headroom lets a fat direct
    /// path go far sharper than "just enough" without changing what a thin link (bound by GCC or the table) gets.
    /// With the headroom: 1080p30 about 218 Mb/s, 1440p30 about 387, 1440p60 about 774, 4K60 about 1742, all under the
    /// 2 Gb/s clamp; only very large pictures at very high frame rates hit it.
    /// </summary>
        public static int MaxBitrateKbps(int width, int height, int fps) =>
        (int)Math.Clamp((double)width * height * Math.Clamp(fps, MinFps, MaxFps) * MotionBitsPerPixel * CeilingHeadroom / 1000, 1_000, AbsoluteMaxKbps);

    private static void Apply(User u, SessionOptions options)
    {
        u.FpsCap = options.CustomFps > 0 ? Math.Clamp(options.CustomFps, MinFps, MaxFps) : DefaultFps;
        u.Quality = options.ImageQuality;
        u.CustomBitrateKbps = options.ImageQuality == ImageQuality.IqCustom ? options.CustomBitrateKbps : 0;
        u.Refinement = options.LosslessRefinement != BoolOption.BoNo;
    }

    private void Recompute()
    {
        int cap = _users.Count == 0 ? DefaultFps : _users.Values.Min(u => u.FpsCap);
        _fps = Math.Min(_fps, cap);
        if (_fps < MinFps)
        {
            _fps = MinFps;
        }

        if (_users.Count == 0)
        {
            _fps = DefaultFps;
            _ratio = MaxRatio;
        }
    }

    private void Adjust()
    {
        bool anyCongested = false;
        bool allGood = _users.Count > 0;
        foreach (User u in _users.Values)
        {
            if (u.Delays.Count == 0)
            {
                allGood = false;
                continue;
            }

            TimeSpan avg = AverageRtt(u);
            TimeSpan queueing = Queueing(u);
            if (u.BandwidthBps is { } gcc)
            {
                // GCC owns the bitrate on this path; only give up frame rate once it has nothing left to give.
                bool atFloor = gcc <= 1_200_000;
                if (atFloor && (queueing > CongestedDelay || avg > PathologicalDelay || u.Loss > SevereLoss))
                {
                    anyCongested = true;
                    u.GoodStreak = 0;
                }
                else
                {
                    u.GoodStreak++;
                }

                continue;
            }

            if (queueing > CongestedDelay || avg > PathologicalDelay || u.Loss > CongestionLoss)
            {
                anyCongested = true;
                u.GoodStreak = 0;
            }
            else if (queueing <= GoodDelay)
            {
                u.GoodStreak++;
            }
            else
            {
                allGood = false;
            }

            if (u.GoodStreak < 2)
            {
                allGood = false;
            }
        }

        int cap = _users.Count == 0 ? DefaultFps : _users.Values.Min(u => u.FpsCap);
        if (anyCongested)
        {
            if (_probe > 1.0)
            {
                _probe = Math.Max(1.0, _probe * 0.6); // give back probed headroom before touching quality
            }
            else if (_ratio > MinRatio)
            {
                _ratio = Math.Max(MinRatio, _ratio * 0.8);
            }
            else
            {
                _fps = Math.Max(MinFps, (int)(_fps * 0.8));
            }
        }
        else if (allGood)
        {
            if (_fps < cap)
            {
                _fps = Math.Min(cap, Math.Max(_fps + 1, (int)(_fps * 1.2)));
            }
            else if (_ratio < MaxRatio)
            {
                _ratio = Math.Min(MaxRatio, _ratio * 1.1 + 0.01);
            }
            else if (_probe < MaxProbe(_tier))
            {
                _probe = Math.Min(MaxProbe(_tier), _probe * 1.15); // link has headroom: raise quality a notch every interval
            }
        }
    }

    /// <summary>Mean of the samples inside <see cref="AverageWindow"/>, or of the last five when sampling is slow (pings only).</summary>
    private TimeSpan AverageRtt(User u)
    {
        if (u.Delays.Count == 0)
        {
            return TimeSpan.Zero;
        }

        DateTimeOffset cutoff = _time.GetUtcNow() - AverageWindow;
        long ticks = 0;
        int count = 0;
        int index = 0;
        foreach ((DateTimeOffset at, TimeSpan rtt) in u.Delays)
        {
            index++;
            if (at >= cutoff || index > u.Delays.Count - 5)
            {
                ticks += rtt.Ticks;
                count++;
            }
        }

        return TimeSpan.FromTicks(ticks / Math.Max(1, count));
    }

    /// <summary>Round-trip inflation over the recent minimum: the part of the delay caused by queueing, not by distance.</summary>
    private TimeSpan Queueing(User u)
    {
        if (u.RttHistory.Count == 0)
        {
            return TimeSpan.Zero;
        }

        TimeSpan min = TimeSpan.MaxValue;
        foreach ((_, TimeSpan rtt) in u.RttHistory)
        {
            if (rtt < min)
            {
                min = rtt;
            }
        }

        return AverageRtt(u) - min;
    }

    /// <summary>
    /// The part of a viewer's link <paramref name="display"/> gets: its pixels over the pixels of every display the
    /// viewer watches, the focus counted <see cref="FocusWeight"/> times. A display whose size is not known yet is
    /// taken to be the size of this one.
    /// </summary>
    private double Share(User u, int display)
    {
        if (u.Displays.Length <= 1)
        {
            return 1.0;
        }

        long fallback = _pixelsByDisplay.GetValueOrDefault(display, 1);
        double mine = 0, total = 0;
        foreach (int d in u.Displays)
        {
            double weight = _pixelsByDisplay.GetValueOrDefault(d, fallback) * (d == u.Focus ? FocusWeight : 1.0);
            total += weight;
            if (d == display)
            {
                mine = weight;
            }
        }

        return total > 0 ? mine / total : 1.0 / u.Displays.Length;
    }

    /// <summary>Frames a viewer may have unacknowledged before it counts as congested: a round trip's worth at the current fps, plus slack.</summary>
    private int InFlightLimit(User u)
    {
        double rttSeconds = AverageRtt(u).TotalSeconds;
        return Math.Max(MaxInFlight, (int)Math.Ceiling(_fps * rttSeconds * 1.5) + 2);
    }

    private void EvaluateTier()
    {
        if (TierOverride is { } forced)
        {
            _tier = forced;
            _probe = Math.Min(_probe, MaxProbe(_tier));
            return;
        }

        TimeSpan worstQueueing = TimeSpan.Zero;
        TimeSpan worstRtt = TimeSpan.Zero;
        double worstExcess = 0;
        bool overLimit = false;
        bool anySamples = false;
        foreach (User u in _users.Values)
        {
            if (u.Delays.Count == 0)
            {
                continue;
            }

            anySamples = true;
            TimeSpan rtt = AverageRtt(u);
            worstRtt = rtt > worstRtt ? rtt : worstRtt;
            TimeSpan q = Queueing(u);
            worstQueueing = q > worstQueueing ? q : worstQueueing;

            // Frames legitimately in flight on this link at this fps; only the excess is a queue building up.
            double expected = _fps * rtt.TotalSeconds;
            int limit = InFlightLimit(u);
            foreach (StreamState st in u.Streams.Values)
            {
                long backlog = Math.Max(0, (long)st.Sent - st.Acked);
                worstExcess = Math.Max(worstExcess, backlog - expected);
                overLimit |= backlog > limit;
            }
        }

        double worstLoss = _users.Values.Count == 0 ? 0 : _users.Values.Max(u => u.Loss);
        LinkTier measured = !anySamples ? LinkTier.Fair
            : worstRtt > PathologicalDelay || overLimit || worstLoss > SevereLoss ? LinkTier.Poor
            : worstLoss > CongestionLoss ? LinkTier.Fair
            : worstQueueing < TimeSpan.FromMilliseconds(15) && worstExcess <= 1 && _ratio >= MaxRatio ? LinkTier.Excellent
            : worstQueueing < GoodDelay && worstExcess <= 2 ? LinkTier.Good
            : worstQueueing < TimeSpan.FromMilliseconds(120) ? LinkTier.Fair
            : LinkTier.Poor;

        // Climb one tier per evaluation, drop immediately.
        _tier = measured > _tier ? _tier + 1 : measured;
        if (_probe > MaxProbe(_tier))
        {
            _probe = MaxProbe(_tier);
        }
    }
}
