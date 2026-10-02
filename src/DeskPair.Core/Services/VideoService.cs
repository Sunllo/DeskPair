using System.Buffers;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using DeskPair.Core.Qos;
using DeskPair.Core.Session;
using DeskPair.Core.Video;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Abstractions.Codec;
using DeskPair.Protocol.Messages;
using AbstractCodec = DeskPair.Platform.Abstractions.Codec.VideoCodec;
using WireCodec = DeskPair.Protocol.Messages.VideoCodec;

namespace DeskPair.Core.Services;

/// <summary>What the video service sent last; diagnostics for logs and the viewer's status line.</summary>
public enum VideoMode
{
    Idle,
    Video,
    Tiles,
    Refining,
}

/// <summary>Per-stage cost of the capture loop over the last reporting window (milliseconds are averages per captured frame).</summary>
public sealed record VideoStats(
    double Fps,
    double CaptureMs,
    double DetectMs,
    double ConvertMs,
    double EncodeMs,
    double TileMs,
    int Frames,
    int Skipped,
    int Latched,
    int KeyFrames,
    int BitrateKbps,
    LinkTier Tier);

/// <summary>
/// Captures one display and publishes it two ways, RDP-GFX style: small changes go out as lossless tiles,
/// large changes (scrolling, dragging, video) as H.264 frames, and once the screen settles the tiles that
/// were last delivered lossily are refined losslessly within the QoS byte budget. Viewers that do not
/// understand tiles get the plain video stream.
/// </summary>
/// <remarks>
/// The loop runs synchronously on its own thread: capture, change detection, colour conversion and the
/// encoder call are all CPU work with a per-frame budget of one frame interval, and a pooled thread with
/// async continuations adds scheduling jitter on top. Pacing uses the timer thread's high-resolution wait
/// plus a short spin so the cadence does not quantise to the coarse Windows timer.
/// </remarks>
public sealed class VideoService : PublisherService
{
    private const int RepeatEncodeMax = 10;
    private const int VideoLatchFrames = 3;
    private const int VideoRecheckFrames = 30;
    private static readonly TimeSpan ForceFrameInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StatsInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SpinTail = TimeSpan.FromMilliseconds(1.5);
    private static readonly TimeSpan CongestionStall = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan BitrateIncreaseInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MotionGap = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// How long a static screen keeps a keyframe encoded while the link was judged poor. Viewers without
    /// lossless tiles (the phones) see the last keyframe until something moves; when the bitrate target has
    /// since doubled -- a phone back from the background, a link that recovered -- a fresh keyframe at the
    /// better rate is worth its cost. Tile viewers get their refinement pass instead.
    /// </summary>
    public static readonly TimeSpan QualityRefreshAfter = TimeSpan.FromSeconds(3);

    /// <summary>Most the encoder bitrate is raised above the target to make up for frames arriving slower than configured.</summary>
    public const double MaxFrameRateCompensation = 2.5;

    /// <summary>Hard ceiling for what is asked of the encoder (compensation included); above this H.264 levels and hardware rate control stop behaving.</summary>
    public const int MaxEncoderBitrateKbps = 2_000_000;
    private static readonly TimeSpan BitrateDecreaseInterval = TimeSpan.FromMilliseconds(250);
    /// <summary>Lossless refinement (and small lossless patches) start only after the video stream has been quiet this long.</summary>
    public static readonly TimeSpan RefinementQuietTime = TimeSpan.FromMilliseconds(500);
    private const long SmallChangeByteLimit = 64 * 1024;

    /// <summary>Largest change sent as lossless tiles instead of video: 1% of the screen, at least 12 tiles.</summary>
    private static int SmallChangeTiles(int tileCount) => Math.Max(12, tileCount / 100);

    private readonly IScreenCapturerFactory _capturers;
    private readonly IVideoEncoderFactory _encoders;
    private readonly VideoQosController _qos;
    private readonly TimeProvider _time;
    private readonly HashSet<int> _needKeyframe = new();
    private readonly object _kfLock = new();
    private readonly ChangeDetector _detector = new();
    private int _keyframeRequested;
    private int _keyframeUrgent;
    private string? _keyframeReason;
    private long _lastKeyFrameTicks;
    private uint _seq;
    private uint _lastVideoSeq;
    private uint _lastKeyFrameSeq;
    private uint _refinePass;
    private bool _passOpen;

    public VideoService(DisplayDescriptor display, IScreenCapturerFactory capturers, IVideoEncoderFactory encoders, VideoQosController qos, TimeProvider time, ILogger log)
        : base($"video[{display.Index}]", log)
    {
        Display = display;
        _capturers = capturers;
        _encoders = encoders;
        _qos = qos;
        _time = time;
    }

    public DisplayDescriptor Display { get; private set; }

    /// <summary>
    /// Re-reads this display from the platform, for the restart after the desktop changed shape. Without it
    /// the stream comes back with the geometry it started with and keeps sending a crop of the new screen.
    /// </summary>
    public Func<int, DisplayDescriptor?>? RefreshDisplay { get; set; }

    /// <summary>
    /// The codec to encode in. Settable rather than init-only because it is negotiated: a viewer that cannot
    /// decode what is running makes the host restart the loop on something it can. The loop reads this once,
    /// at start, so changing it takes effect on the next restart, not mid-stream.
    /// </summary>
    public AbstractCodec Codec { get; set; } = AbstractCodec.H264;

    /// <summary>Stops the capture loop and starts it again, which is how a codec change takes effect.</summary>
    public Task RestartForCodecChangeAsync() => RestartAsync();

    /// <summary>
    /// Asked when no encoder of <see cref="Codec"/> would start at all, though the machine lists one -- Media
    /// Foundation's software H.264 encoder on a Windows without a graphics driver refuses its output format, say. The
    /// answer is the codec to go on in, one every viewer of the stream reads, or null when there is none; the host
    /// strikes the failed codec off so that no stream tries it again.
    /// </summary>
    public Func<AbstractCodec, AbstractCodec?>? CodecFailed { get; set; }

    /// <summary>
    /// A per-viewer gate: returns false for a connection that must not receive frames right now. The host sets it
    /// for the "secure desktop for listed devices only" policy -- while the input desktop is the secure one, a
    /// device not allowed to see it is sent nothing and keeps the last ordinary picture, with the banner over it.
    /// </summary>
    public Func<int, bool>? FrameGate { get; init; }

    /// <summary>The same restart after the display changed size: the loop re-reads the descriptor and builds an encoder to match.</summary>
    public Task RestartForDisplayChangeAsync() => RestartAsync();

    /// <summary>What each attached viewer can decode, for <see cref="CodecNegotiation"/>.</summary>
    public IEnumerable<CodecNegotiation.Viewer> SubscriberCapabilities() =>
        SubscriberSnapshot().Select(s => new CodecNegotiation.Viewer(s.DecodableCodecs, s.PreferredCodec));

    public long FramesSent { get; private set; }

    public long TileUpdatesSent { get; private set; }

    public long TilesSent { get; private set; }

    public long TileBytesSent { get; private set; }

    /// <summary>Ticks where every viewer was congested and no frame was encoded (nothing is dropped, so no keyframe is needed).</summary>
    public long TicksSkipped { get; private set; }

    public VideoMode LastMode { get; private set; }

    /// <summary>Latest per-stage timing snapshot (updated every five seconds while running).</summary>
    public VideoStats? LastStats { get; private set; }

    /// <summary>Refinement rounds started (diagnostics/tests).</summary>
    public long RefinementPasses { get; private set; }

    /// <summary>Master switch for the lossless tile path (tests and diagnostics).</summary>
    public bool TilesEnabled { get; set; } = true;

    /// <summary>
    /// Keyframes requested for recovery (lost frames, decode errors) are merged and produced at most once per
    /// <see cref="KeyFrameMinInterval"/>: each one is many times the size of a delta frame, and on a congested
    /// link a keyframe storm is what turns a hiccup into seconds of stutter. New viewers are served at once.
    /// </summary>
    public static readonly TimeSpan KeyFrameMinInterval = TimeSpan.FromSeconds(1);

    public void RequestKeyFrame(string reason = "requested")
    {
        // First reason wins until the keyframe is produced; later requests are merged into it.
        Interlocked.CompareExchange(ref _keyframeReason, reason, null);
        Interlocked.Exchange(ref _keyframeRequested, 1);
    }

    /// <summary>Keyframe that must not wait for the rate limit (a viewer subscribed and has no picture yet).</summary>
    public void RequestUrgentKeyFrame(int connectionId, string reason = "new viewer")
    {
        Interlocked.Exchange(ref _keyframeUrgent, 1);
        RequestKeyFrame(connectionId, reason);
    }

    public void RequestKeyFrame(int connectionId, string reason = "requested")
    {
        lock (_kfLock)
        {
            _needKeyframe.Add(connectionId);
        }

        RequestKeyFrame(reason);
    }

    protected override ValueTask OnSubscribedAsync(IServiceSubscriber subscriber, CancellationToken ct)
    {
        RequestUrgentKeyFrame(subscriber.ConnectionId, "viewer subscribed");
        return ValueTask.CompletedTask;
    }

    /// <summary>Closes every viewer with a reason, for a failure the viewer would otherwise never learn of.</summary>
    private void TellSubscribers(string reason, CancellationToken ct)
    {
        Log.LogError("{Service}: {Reason}", Name, reason);
        var message = new Message { Misc = new Misc { CloseReason = new CloseReason { Reason = reason } } };
        foreach (IServiceSubscriber subscriber in SubscriberSnapshot())
        {
            try
            {
                subscriber.PublishAsync(message, MessagePriority.Control, ct).AsTask().GetAwaiter().GetResult();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Log.LogDebug(e, "{Service}: could not tell {Conn} why", Name, subscriber.ConnectionId);
            }
        }
    }

    protected override Task RunAsync(CancellationToken ct) =>
        Task.Factory.StartNew(() => RunLoop(ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    /// <summary>Encoders that failed in this session; never offered again while the host is running.</summary>
    private readonly HashSet<string> _failedEncoders = [];

    private void RunLoop(CancellationToken ct)
    {
        SupportedCodecs supported = _encoders.Probe();
        if (!supported.Supports(Codec))
        {
            // Nothing on this machine can encode. Thrown, this would be swallowed by the guard in
            // PublisherService and the viewer would sit on a connected session whose picture never arrives,
            // with the reason only in the host's log. Tell the viewer instead, then stop quietly.
            TellSubscribers($"this computer has no {Codec} encoder", ct);
            return;
        }

        Thread thread = Thread.CurrentThread;
        ThreadPriority priority = thread.Priority;
        try
        {
            thread.Priority = ThreadPriority.AboveNormal;
            thread.Name ??= Name;
        }
        catch (Exception)
        {
        }

        // The screen may have changed shape since this service last started -- that is one of the reasons it
        // restarts -- so take the current descriptor rather than the one this stream was created with.
        if (RefreshDisplay?.Invoke(Display.Index) is { } current && current != Display)
        {
            Log.LogInformation("{Service}: display {Index} is now {W}x{H}", Name, current.Index, current.Width, current.Height);
            Display = current;
        }

        IScreenCapturer capturer = _capturers.Create(Display, preferGpu: false);
        try
        {
            // An encoder that gives up mid-session is not the end of the stream: drop it, remember not to ask
            // for it again, and come back with the next one down. Bounded, because each attempt excludes one
            // more encoder and the list is finite.
            while (!ct.IsCancellationRequested)
            {
                int bitrate = _qos.TargetBitrateKbps(Display.Index, Display.Width, Display.Height);
                var config = new VideoEncoderConfig(Codec, Display.Width, Display.Height, _qos.Fps, bitrate, PreferHardware: true, PixelFormat.Bgra32, GpuApi.None, Display.AdapterLuid)
                {
                    Exclude = [.. _failedEncoders],
                };
                IVideoEncoder? encoder = null;
                try
                {
                    try
                    {
                        encoder = _encoders.Create(config);
                    }
                    catch (NotSupportedException e)
                    {
                        // Nothing this machine has for the codec would start, or everything it had has since failed:
                        // the codec is out, not one encoder. Another every viewer reads may still do; without one the
                        // viewers are told rather than left waiting for a picture that never comes.
                        if (CodecFailed?.Invoke(Codec) is { } next && next != Codec)
                        {
                            Log.LogWarning(e, "{Service}: no {Codec} encoder would start; going on in {Next}", Name, Codec, next);
                            Codec = next;
                            continue;
                        }

                        Log.LogError(e, "{Service}: no {Codec} encoder would start ({Count} failed before), and no other codec suits every viewer", Name, Codec, _failedEncoders.Count);
                        TellSubscribers(_failedEncoders.Count > 0 ? $"every {Codec} encoder on this computer failed" : $"no {Codec} encoder on this computer would start", ct);
                        return;
                    }

                    Loop(capturer, encoder, bitrate, ct);
                    return;
                }
                catch (EncoderUnusableException e)
                {
                    Log.LogWarning(e, "{Service}: dropping {Encoder}; {Reason}", Name, e.Encoder.Name, e.Reason);
                    _failedEncoders.Add(e.Encoder.Name);
                }
                finally
                {
                    encoder?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            }
        }
        finally
        {
            capturer.DisposeAsync().AsTask().GetAwaiter().GetResult();
            try
            {
                thread.Priority = priority;
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>
    /// One encode attempt, with the throw counted rather than allowed to end the session. A single failure is
    /// treated as a dropped frame, which is what it usually is; a run of them raises
    /// <see cref="EncoderUnusableException"/> so <see cref="RunLoop"/> can come back on a different encoder.
    /// </summary>
    private static bool Encode(IVideoEncoder encoder, EncoderHealth health, ReadOnlySpan<byte> input, int stride, long ptsMs, out EncodedPacket packet)
    {
        try
        {
            bool produced = encoder.TryEncode(input, stride, ptsMs, out packet);
            health.RecordSuccess();
            return produced;
        }
        catch (Exception e) when (e is not (OperationCanceledException or ObjectDisposedException or OutOfMemoryException))
        {
            packet = default;
            if (health.RecordFailure(e))
            {
                throw new EncoderUnusableException(encoder.Descriptor, health.Verdict!, e);
            }

            return false;
        }
    }

    /// <summary>A packet the encoder finished after the call that submitted its frame had returned, if there is one.</summary>
    private static bool Collect(IVideoEncoder encoder, EncoderHealth health, out EncodedPacket packet)
    {
        try
        {
            return encoder.TryCollect(out packet);
        }
        catch (Exception e) when (e is not (OperationCanceledException or ObjectDisposedException or OutOfMemoryException))
        {
            packet = default;
            if (health.RecordFailure(e))
            {
                throw new EncoderUnusableException(encoder.Descriptor, health.Verdict!, e);
            }

            return false;
        }
    }

    private void Loop(IScreenCapturer capturer, IVideoEncoder encoder, int bitrate, CancellationToken ct)
    {
        PixelFormat inputFormat = encoder.RequiredInputFormat;
        if (inputFormat is not (PixelFormat.Bgra32 or PixelFormat.Nv12 or PixelFormat.I420))
        {
            throw new NotSupportedException($"Encoder wants {inputFormat}; only BGRA, NV12 and I420 inputs are supported.");
        }

        byte[]? planar = null;
        var health = new EncoderHealth();
        Log.LogInformation("{Service}: {Width}x{Height} {Codec} ({Hw}) at {Fps} fps, {Bitrate} kbps, tiles {Tiles}", Name, Display.Width, Display.Height, Codec, encoder.IsHardware ? "hw" : "sw", _qos.Fps, bitrate, TilesEnabled);

        // The capturer's buffer stays valid until it produces the next frame (timeouts never overwrite it),
        // so the last picture is referenced, not copied.
        ReadOnlyMemory<byte> last = default;
        int lastStride = 0;
        int repeats = 0;
        long lastSentTicks = 0;
        long lastChangeTicks = _time.GetTimestamp();
        int currentBitrate = bitrate;
        int lastKeyLinkKbps = 0;
        int currentFps = _qos.Fps;
        long lastBitrateChange = _time.GetTimestamp();
        long lastCaptureTicks = 0;
        double motionIntervalEwma = 0; // seconds between captured frames while the screen moves
        string? keyframeReason = null;
        (int X, int Y) focus = (Display.Width / 2, Display.Height / 2);
        bool planarStale = true;
        bool detectorStale = true;
        int videoStreak = 0; // consecutive frames that went out as video because most of the screen moved
        long congestedSince = 0;
        var stats = new StageTimer(_time);

        while (!ct.IsCancellationRequested)
        {
            long iterationStart = _time.GetTimestamp();
            TimeSpan spf = _qos.FrameInterval;

            // Rate-control changes are not free for hardware encoders, so follow the QoS target only in steps of at
            // least 10%: decreases quickly (a congested link cannot wait), increases at most once a second.
            // The encoder's rate control spreads the bitrate over the configured 60 fps. A window drag usually delivers
            // far fewer frames, so each would get a quarter of the bits the link allows: scale the encoder's bitrate
            // by configured / actual motion frame rate (capped) so the stream's real rate lands on the target.
            double fpsScale = motionIntervalEwma <= 0 ? 1.0 : Math.Clamp(motionIntervalEwma * currentFps, 1.0, MaxFrameRateCompensation);
            int linkKbps = _qos.TargetBitrateKbps(Display.Index, Display.Width, Display.Height);
            encoder.SetLinkBitrate(linkKbps);
            int wanted = (int)Math.Min(MaxEncoderBitrateKbps, linkKbps * fpsScale);
            TimeSpan sinceChange = _time.GetElapsedTime(lastBitrateChange);
            bool bigStep = Math.Abs(wanted - currentBitrate) >= currentBitrate / 10;
            // Halving or more goes through at once: frames suddenly arriving at full rate while the encoder still
            // compensates for a slow drag would otherwise overshoot the link several times over.
            bool steepDrop = wanted <= currentBitrate / 2;
            if (bigStep && (steepDrop || (wanted < currentBitrate ? sinceChange >= BitrateDecreaseInterval : sinceChange >= BitrateIncreaseInterval)))
            {
                encoder.SetBitrate(wanted);
                currentBitrate = wanted;
                lastBitrateChange = _time.GetTimestamp();
            }

            if (_qos.Fps != currentFps)
            {
                currentFps = _qos.Fps;
                encoder.SetFrameRate(currentFps);
            }

            IServiceSubscriber[] subscribers = SubscriberSnapshot();
            bool tilesOk = TilesEnabled && subscribers.Length > 0 && _qos.LosslessRefinementEnabled && AllSupportTiles(subscribers);
            bool keyframeWanted = Volatile.Read(ref _keyframeRequested) == 1
                && (Volatile.Read(ref _keyframeUrgent) == 1 || _lastKeyFrameTicks == 0 || _time.GetElapsedTime(_lastKeyFrameTicks) >= KeyFrameMinInterval);
            LinkTier tier = _qos.Tier;

            // A hardware encoder that took longer than it was waited for still holds that frame. Without asking,
            // it only comes out with the next submission -- and with tiles a still screen submits nothing, so a
            // keyframe a viewer is waiting for (a display just opened, a broken picture) would wait until something
            // on that screen moved. Taking it first also lets the next submission be waited for on its own.
            if (!encoder.IsLatencyFree && Collect(encoder, health, out EncodedPacket late))
            {
                Deliver(late, tilesOk, linkKbps);
            }

            long t0 = _time.GetTimestamp();
            CaptureResult result = Acquire(capturer, spf, ct);
            stats.Capture += _time.GetTimestamp() - t0;
            bool sendVideo = false;
            List<int>? dirty = null;
            switch (result.Status)
            {
                case CaptureStatus.Frame:
                    CaptureFrame f = result.Frame;
                    if (f.IsGpuTexture)
                    {
                        throw new NotSupportedException("GPU frames are not wired yet.");
                    }

                    last = f.Cpu[..(f.Stride * f.Height)];
                    lastStride = f.Stride;
                    repeats = 0;
                    planarStale = true;
                    stats.Frames++;
                    (double waitMs, double readbackMs) = capturer.LastFrameTiming;
                    stats.WaitMs += waitMs;
                    stats.ReadbackMs += readbackMs;
                    {
                        // Frame rate while something is actually moving: frames closer than 200 ms apart form a burst.
                        long nowTicks = _time.GetTimestamp();
                        if (lastCaptureTicks != 0 && _time.GetElapsedTime(lastCaptureTicks, nowTicks) < MotionGap)
                        {
                            stats.MotionTicks += nowTicks - lastCaptureTicks;
                            stats.MotionFrames++;
                            double interval = _time.GetElapsedTime(lastCaptureTicks, nowTicks).TotalSeconds;
                            motionIntervalEwma = motionIntervalEwma <= 0 ? interval : 0.5 * motionIntervalEwma + 0.5 * interval;
                        }

                        lastCaptureTicks = nowTicks;
                    }

                    if (tilesOk && videoStreak >= VideoLatchFrames && videoStreak % VideoRecheckFrames != 0)
                    {
                        // Sustained motion: hashing every tile only to conclude "changed" again is wasted work at 1440p.
                        videoStreak++;
                        detectorStale = true;
                        lastChangeTicks = _time.GetTimestamp();
                        sendVideo = true;
                        stats.Latched++;
                    }
                    else if (tilesOk)
                    {
                        // One stream, one timeline: every change goes out as video. Change detection only tells an
                        // unchanged frame apart (so a still screen costs nothing) and tracks what refinement must redo.
                        long t1 = _time.GetTimestamp();
                        dirty = _detector.Update(last.Span, lastStride, f.Width, f.Height, detectorStale ? default : f.DirtyRects.Span);
                        detectorStale = false;
                        stats.Detect += _time.GetTimestamp() - t1;
                        if (dirty.Count > 0)
                        {
                            lastChangeTicks = _time.GetTimestamp();
                            focus = Centroid(dirty);
                            if (!keyframeWanted && dirty.Count <= SmallChangeTiles(_detector.TileCount) && !_detector.AnyMotion(dirty)
                                && _time.GetElapsedTime(lastSentTicks) >= RefinementQuietTime && AllCaughtUp(subscribers))
                            {
                                // A caret blink or a typed character on an otherwise still, fully delivered picture: patch it
                                // losslessly on the same timeline instead of restarting the video (and the refinement) for it.
                                long t2 = _time.GetTimestamp();
                                sendVideo = !TrySendTiles(dirty, last.Span, lastStride, refinement: false, SmallChangeByteLimit, subscribers);
                                stats.Tiles += _time.GetTimestamp() - t2;
                                videoStreak = 0;
                            }
                            else
                            {
                                videoStreak++;
                                sendVideo = true;
                            }
                        }
                        else
                        {
                            videoStreak = 0;
                            sendVideo = keyframeWanted;
                        }
                    }
                    else
                    {
                        // Video-only viewers: every captured frame is a change (capturers time out on a still screen).
                        detectorStale = true;
                        lastChangeTicks = _time.GetTimestamp();
                        sendVideo = true;
                    }

                    break;
                case CaptureStatus.Timeout:
                    _detector.Idle();
                    videoStreak = 0;
                    // Static screen. Hardware encoders want a steady feed and a long gap needs a frame, but a repeated
                    // lossy frame would undo lossless tiles on the viewer, so tiles mode only repeats for keyframes.
                    if (!tilesOk && !keyframeWanted && lastKeyLinkKbps > 0 && linkKbps >= 2 * lastKeyLinkKbps
                        && _time.GetElapsedTime(_lastKeyFrameTicks) >= QualityRefreshAfter)
                    {
                        RequestKeyFrame("quality recovered");
                    }

                    bool forced = keyframeWanted || (!tilesOk && _time.GetElapsedTime(lastSentTicks) >= ForceFrameInterval);
                    if (!last.IsEmpty && (forced || (!tilesOk && !encoder.IsLatencyFree && repeats < RepeatEncodeMax)))
                    {
                        repeats++;
                        sendVideo = true;
                    }

                    break;
                case CaptureStatus.DesktopSwitched:
                    // "the module restarts the service" is what this used to say, and nothing did: the loop
                    // returned and the picture stopped for the rest of the session. Restart here instead --
                    // fire and forget, because RestartAsync waits for this very loop to finish, which the
                    // return below is about to do.
                    Log.LogInformation("{Service}: desktop switched, recreating the capturer", Name);
                    _ = RestartAsync();
                    return;
                case CaptureStatus.Error:
                    Log.LogWarning(result.Error, "{Service}: capture error", Name);
                    Sleep(TimeSpan.FromMilliseconds(200), ct);
                    continue;
            }

            if (sendVideo && !last.IsEmpty && subscribers.Length > 0)
            {
                bool ready = AnyReady(subscribers);
                if (!ready)
                {
                    if (congestedSince == 0)
                    {
                        congestedSince = _time.GetTimestamp();
                    }
                    else if (_time.GetElapsedTime(congestedSince) > CongestionStall)
                    {
                        // Nothing has been acknowledged for seconds: the unacked frames are gone (transport change, lost
                        // acks), not queued. Start the accounting over with a keyframe instead of waiting forever.
                        Log.LogWarning("{Service}: no acknowledgements for {Seconds:F0} s; resetting in-flight accounting", Name, CongestionStall.TotalSeconds);
                        foreach (IServiceSubscriber s in subscribers)
                        {
                            _qos.ResetStream(s.ConnectionId, Display.Index);
                            RequestKeyFrame(s.ConnectionId, "no acknowledgements");
                        }

                        congestedSince = 0;
                        ready = true;
                    }
                }
                else
                {
                    congestedSince = 0;
                }

                if (!ready)
                {
                    // Every viewer is still working off earlier frames: encoding now would only produce a frame we drop
                    // (and a keyframe to recover from the drop). Skipping the tick costs nothing.
                    TicksSkipped++;
                    stats.Skipped++;
                }
                else
                {
                    if (keyframeWanted && Interlocked.Exchange(ref _keyframeRequested, 0) == 1)
                    {
                        Interlocked.Exchange(ref _keyframeUrgent, 0);
                        keyframeReason = Interlocked.Exchange(ref _keyframeReason, null) ?? "requested";
                        encoder.RequestKeyFrame();
                    }

                    bool encoded;
                    EncodedPacket packet;
                    bool pictureWasNew = planarStale;
                    long t3 = _time.GetTimestamp();
                    if (inputFormat != PixelFormat.Bgra32)
                    {
                        int need = inputFormat == PixelFormat.Nv12 ? PixelConversion.Nv12Size(Display.Width, Display.Height) : PixelConversion.I420Size(Display.Width, Display.Height);
                        planar ??= new byte[need];
                        if (planarStale)
                        {
                            if (inputFormat == PixelFormat.Nv12)
                            {
                                PixelConversion.BgraToNv12(last.Span, lastStride, Display.Width, Display.Height, planar);
                            }
                            else
                            {
                                PixelConversion.BgraToI420(last.Span, lastStride, Display.Width, Display.Height, planar);
                            }

                            planarStale = false;
                        }

                        long t4 = _time.GetTimestamp();
                        stats.Convert += t4 - t3;
                        encoded = Encode(encoder, health, planar, Display.Width, _time.GetUtcNow().ToUnixTimeMilliseconds(), out packet);
                        stats.Encode += _time.GetTimestamp() - t4;
                    }
                    else
                    {
                        encoded = Encode(encoder, health, last.Span, lastStride, _time.GetUtcNow().ToUnixTimeMilliseconds(), out packet);
                        stats.Encode += _time.GetTimestamp() - t3;
                    }

                    if (encoded && health.RecordPacket(packet.Data.Span, pictureWasNew))
                    {
                        throw new EncoderUnusableException(encoder.Descriptor, health.Verdict!);
                    }

                    if (encoded)
                    {
                        Deliver(packet, tilesOk, linkKbps);
                    }
                }
            }
            else if (tilesOk && !last.IsEmpty && _detector.PendingRefinement > 0
                && _time.GetElapsedTime(lastSentTicks) >= RefinementQuietTime
                && AllCaughtUp(subscribers))
            {
                if (detectorStale)
                {
                    // Detection was skipped during motion; bring the hashes up to date with the picture being refined.
                    _detector.Update(last.Span, lastStride, Display.Width, Display.Height);
                    _detector.MarkAllLossy();
                    detectorStale = false;
                }

                if (!_passOpen)
                {
                    _passOpen = true;
                    _refinePass++;
                    RefinementPasses++;
                }

                long allowance = (long)(_qos.BytesPerSecondBudget(Display.Index, Display.Width, Display.Height) * Math.Max(spf.TotalSeconds, 0.01));
                int maxTiles = (int)Math.Clamp(allowance / 1500, 16, 400);
                List<int> candidates = _detector.RefinementCandidates(focus.X, focus.Y, maxTiles);
                long t5 = _time.GetTimestamp();
                if (candidates.Count > 0 && TrySendTiles(candidates, last.Span, lastStride, refinement: true, long.MaxValue, subscribers))
                {
                    LastMode = VideoMode.Refining;
                }

                stats.Tiles += _time.GetTimestamp() - t5;
            }
            else if (dirty is { Count: 0 } && _detector.PendingRefinement == 0)
            {
                LastMode = VideoMode.Idle;
            }

            if (stats.Elapsed >= StatsInterval)
            {
                LastStats = stats.Snapshot(tier, currentBitrate);
                Log.LogInformation("{Service}: {Fps:F1} fps ({MotionFps:F1} fps while moving, {MotionSeconds:F1} s), {Encoded} encoded, avg {AvgKb:F1} KB max {MaxKb:F1} KB per frame, capture {Capture:F2} ms (desktop wait {Wait:F1}, readback {Readback:F1}), detect {Detect:F2} ms, convert {Convert:F2} ms, encode {Encode:F2} ms, tiles {Tiles:F2} ms, {Skipped} skipped, {Latched} latched, {KeyFrames} keyframes, encoder {Kbps} kbps (x{Scale:F2} for the motion frame rate), tier {Tier}",
                    Name, LastStats.Fps, stats.MotionFps, stats.MotionSeconds, stats.Encoded, stats.Encoded == 0 ? 0 : stats.EncodedBytes / 1024.0 / stats.Encoded, stats.MaxFrameBytes / 1024.0,
                    LastStats.CaptureMs, stats.Frames == 0 ? 0 : stats.WaitMs / stats.Frames, stats.Frames == 0 ? 0 : stats.ReadbackMs / stats.Frames, LastStats.DetectMs, LastStats.ConvertMs, LastStats.EncodeMs, LastStats.TileMs, LastStats.Skipped, LastStats.Latched, LastStats.KeyFrames, currentBitrate, motionIntervalEwma <= 0 ? 1.0 : Math.Clamp(motionIntervalEwma * currentFps, 1.0, MaxFrameRateCompensation), tier);
                stats.Reset();
            }

            Pace(iterationStart, spf, ct);
        }

        // A packet out of the encoder, whether it answered at once or late.
        void Deliver(EncodedPacket packet, bool tilesOk, int linkKbps)
        {
            stats.Encoded++;
            stats.EncodedBytes += packet.Data.Length;
            stats.MaxFrameBytes = Math.Max(stats.MaxFrameBytes, packet.Data.Length);
            if (packet.IsKeyFrame)
            {
                stats.KeyFrames++;
                _lastKeyFrameTicks = _time.GetTimestamp();
                lastKeyLinkKbps = linkKbps;
                Log.LogInformation("{Service}: keyframe {Bytes} KB at {Kbps} kbps ({Reason})", Name, packet.Data.Length / 1024, currentBitrate, keyframeReason ?? "encoder decided");
                keyframeReason = null;
            }

            Publish(packet);
            lastSentTicks = _time.GetTimestamp();
            LastMode = VideoMode.Video;
            _passOpen = false; // any refinement round in progress is superseded by this picture
            if (tilesOk)
            {
                _detector.MarkAllLossy(); // the viewer's whole picture is lossy again
            }
        }
    }

    private static CaptureResult Acquire(IScreenCapturer capturer, TimeSpan timeout, CancellationToken ct)
    {
        ValueTask<CaptureResult> pending = capturer.AcquireFrameAsync(timeout, ct);
        return pending.IsCompletedSuccessfully ? pending.Result : pending.AsTask().GetAwaiter().GetResult();
    }

    /// <summary>Sleeps until <paramref name="start"/> + <paramref name="interval"/>: a high-resolution timer wait, then a short spin for the tail.</summary>
    private void Pace(long start, TimeSpan interval, CancellationToken ct)
    {
        TimeSpan remaining = interval - _time.GetElapsedTime(start);
        if (remaining > SpinTail)
        {
            Sleep(remaining - SpinTail, ct);
        }

        // Bounded spin (also guards fake clocks that never advance).
        for (int i = 0; i < 4000 && _time.GetElapsedTime(start) < interval; i++)
        {
            Thread.SpinWait(30);
        }
    }

    private void Sleep(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            Task.Delay(delay, _time, ct).Wait(ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (AggregateException e) when (e.InnerException is OperationCanceledException)
        {
        }
    }

    private int CountPending(List<int> tiles)
    {
        int n = 0;
        foreach (int t in tiles)
        {
            if (_detector.NeedsRefinement(t))
            {
                n++;
            }
        }

        return n;
    }

    /// <summary>Tiles patch the viewer's current picture, so they are only valid once it holds the latest video frame intact.</summary>
    private bool AllCaughtUp(IServiceSubscriber[] subscribers)
    {
        if (_lastVideoSeq == 0)
        {
            return false;
        }

        foreach (IServiceSubscriber s in subscribers)
        {
            if (!_qos.ViewerCaughtUp(s.ConnectionId, Display.Index, _lastVideoSeq, _lastKeyFrameSeq))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AllSupportTiles(IServiceSubscriber[] subscribers)
    {
        foreach (IServiceSubscriber s in subscribers)
        {
            if (!s.SupportsLosslessTiles)
            {
                return false;
            }
        }

        return true;
    }

    private bool AnyReady(IServiceSubscriber[] subscribers)
    {
        foreach (IServiceSubscriber s in subscribers)
        {
            if (!_qos.IsCongested(s.ConnectionId, Display.Index))
            {
                return true;
            }
        }

        return false;
    }

    private static double TileBudgetMultiplier(LinkTier tier) => tier switch
    {
        LinkTier.Excellent => 3.0,
        LinkTier.Good => 1.5,
        _ => 1.0,
    };

    private (int X, int Y) Centroid(List<int> tiles)
    {
        long sx = 0, sy = 0;
        foreach (int t in tiles)
        {
            (int x, int y, int w, int h) = _detector.TileRect(t);
            sx += x + w / 2;
            sy += y + h / 2;
        }

        return ((int)(sx / tiles.Count), (int)(sy / tiles.Count));
    }

    /// <summary>
    /// Encodes and publishes tiles to every viewer that can take them right now; false when the batch
    /// exceeded <paramref name="byteAllowance"/> (caller falls back to video) or nobody could receive it.
    /// </summary>
    private bool TrySendTiles(List<int> tiles, ReadOnlySpan<byte> frame, int stride, bool refinement, long byteAllowance, IServiceSubscriber[] subscribers)
    {
        if (tiles.Count == 0)
        {
            return false;
        }

        var update = new TileUpdate
        {
            Display = Display.Index,
            Width = (uint)Display.Width,
            Height = (uint)Display.Height,
            TileSize = (uint)_detector.TileSize,
            Codec = Protocol.Messages.TileCodec.TcBgrDeltaBrotli,
            Refinement = refinement,
            Pass = refinement ? _refinePass : 0,
        };
        int max = Video.TileCodec.MaxEncodedLength(_detector.TileSize, _detector.TileSize);
        byte[] scratch = ArrayPool<byte>.Shared.Rent(max);
        long total = 0;
        try
        {
            foreach (int t in tiles)
            {
                (int x, int y, int w, int h) = _detector.TileRect(t);
                int n = Video.TileCodec.Encode(frame[(y * stride + x * 4)..], stride, w, h, scratch);
                total += n;
                if (total > byteAllowance)
                {
                    return false;
                }

                update.Tiles.Add(new Tile { Col = (uint)(x / _detector.TileSize), Row = (uint)(y / _detector.TileSize), W = (uint)w, H = (uint)h, Data = ByteString.CopyFrom(scratch, 0, n) });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }

        uint seq = ++_seq;
        update.Seq = seq;
        update.PassComplete = refinement && _detector.PendingRefinement - CountPending(tiles) <= 0;
        int delivered = 0;
        List<int> dropped = BroadcastVideo(new Message { TileUpdate = update }, subscribers, s =>
        {
            if (FrameGate is { } gate && !gate(s.ConnectionId))
            {
                return false;
            }

            if (!s.SupportsLosslessTiles || _qos.IsCongested(s.ConnectionId, Display.Index))
            {
                return false;
            }

            _qos.FrameSent(s.ConnectionId, Display.Index, seq);
            delivered++;
            return true;
        });
        foreach (int id in dropped)
        {
            RequestKeyFrame(id, "tile update dropped by the send queue");
            delivered--;
        }

        if (delivered <= 0)
        {
            return false; // congested: keep the tiles pending and try again next tick
        }

        _detector.MarkClean(tiles);
        if (update.PassComplete)
        {
            _passOpen = false;
        }

        TileUpdatesSent++;
        TilesSent += tiles.Count;
        TileBytesSent += total;
        LastMode = refinement ? VideoMode.Refining : VideoMode.Tiles;
        return true;
    }

    private void Publish(EncodedPacket packet)
    {
        uint seq = ++_seq;
        _lastVideoSeq = seq;
        if (packet.IsKeyFrame)
        {
            _lastKeyFrameSeq = seq;
        }

        var frame = new Message
        {
            VideoFrame = new VideoFrame
            {
                Codec = ToWire(Codec),
                Display = Display.Index,
                Width = (uint)Display.Width,
                Height = (uint)Display.Height,
                Frame = new EncodedVideoFrame
                {
                    Data = ByteString.CopyFrom(packet.Data.Span),
                    Key = packet.IsKeyFrame,
                    PtsMs = packet.PtsTicks,
                    Seq = seq,
                },
            },
        };

        HashSet<int> waiting;
        lock (_kfLock)
        {
            waiting = new HashSet<int>(_needKeyframe);
            if (packet.IsKeyFrame)
            {
                _needKeyframe.Clear();
            }
        }

        List<int> dropped = BroadcastVideo(frame, SubscriberSnapshot(), s =>
        {
            // Not allowed to see what is on screen right now (the secure desktop, for a device the policy keeps out).
            if (FrameGate is { } gate && !gate(s.ConnectionId))
            {
                return false;
            }

            // A viewer waiting for a keyframe cannot use a delta; a congested viewer gets nothing until it acks.
            if (!packet.IsKeyFrame && waiting.Contains(s.ConnectionId))
            {
                return false;
            }

            if (_qos.IsCongested(s.ConnectionId, Display.Index))
            {
                RequestKeyFrame(s.ConnectionId, "frame skipped for a congested viewer");
                return false;
            }

            _qos.FrameSent(s.ConnectionId, Display.Index, seq);
            return true;
        });

        foreach (int id in dropped)
        {
            RequestKeyFrame(id, "frame dropped by the send queue");
        }

        FramesSent++;
    }

    /// <summary>
    /// Every codec the abstraction knows has a wire value, including the ones nothing encodes yet. A gap here
    /// is not a compile error, it is a throw from inside the capture loop the first time such a frame is sent.
    /// </summary>
    public static WireCodec ToWire(AbstractCodec codec) => codec switch
    {
        AbstractCodec.H264 => WireCodec.VcH264,
        AbstractCodec.H265 => WireCodec.VcH265,
        AbstractCodec.Vp8 => WireCodec.VcVp8,
        AbstractCodec.Vp9 => WireCodec.VcVp9,
        AbstractCodec.Av1 => WireCodec.VcAv1,
        _ => throw new ArgumentOutOfRangeException(nameof(codec)),
    };

    public static AbstractCodec? FromWire(WireCodec codec) => codec switch
    {
        WireCodec.VcH264 => AbstractCodec.H264,
        WireCodec.VcH265 => AbstractCodec.H265,
        WireCodec.VcVp8 => AbstractCodec.Vp8,
        WireCodec.VcVp9 => AbstractCodec.Vp9,
        WireCodec.VcAv1 => AbstractCodec.Av1,
        _ => null,
    };

    private sealed class StageTimer(TimeProvider time)
    {
        private long _since = time.GetTimestamp();

        public long Capture, Detect, Convert, Encode, Tiles;
        public int Frames, Skipped, Latched, KeyFrames, Encoded, MotionFrames;
        public long EncodedBytes, MaxFrameBytes, MotionTicks;
        public double WaitMs, ReadbackMs;

        public double MotionSeconds => MotionTicks / (double)time.TimestampFrequency;

        public double MotionFps => MotionTicks == 0 ? 0 : MotionFrames / MotionSeconds;

        public TimeSpan Elapsed => time.GetElapsedTime(_since);

        public VideoStats Snapshot(LinkTier tier, int bitrateKbps)
        {
            double seconds = Math.Max(Elapsed.TotalSeconds, 1e-6);
            double perFrame = Frames == 0 ? 0 : 1000.0 / time.TimestampFrequency / Frames;
            return new VideoStats(Frames / seconds, Capture * perFrame, Detect * perFrame, Convert * perFrame, Encode * perFrame, Tiles * perFrame, Frames, Skipped, Latched, KeyFrames, bitrateKbps, tier);
        }

        public void Reset()
        {
            _since = time.GetTimestamp();
            Capture = Detect = Convert = Encode = Tiles = 0;
            Frames = Skipped = Latched = KeyFrames = Encoded = MotionFrames = 0;
            EncodedBytes = MaxFrameBytes = MotionTicks = 0;
            WaitMs = ReadbackMs = 0;
        }
    }
}
