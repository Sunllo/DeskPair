using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Protocol.Helper;

namespace DeskPair.Desktop.Engine.Elevation;

/// <summary>
/// The engine's end of the link to a running SYSTEM helper: it owns the connected channel, reads what the helper
/// pushes (frames, the desktop switching, the cursor, the display list) and routes it to the proxy objects, and
/// sends input the other way. When the pipe closes -- the helper died, or stood down -- it raises <see cref="Faulted"/>
/// so the engine reverts to reading the ordinary desktop itself.
/// </summary>
internal sealed class HelperClientLink : IAsyncDisposable
{
    private readonly HelperChannel _channel;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<int, HelperScreenCapturer> _capturers = new();
    private readonly Channel<HelperMessage> _outbound = Channel.CreateUnbounded<HelperMessage>(new UnboundedChannelOptions { SingleReader = true });
    private Task? _readLoop;
    private Task? _writeLoop;

    private readonly object _cursorLock = new();
    private HelperCursorShape? _cursorShape;
    private ulong _cursorId;
    private (int X, int Y)? _cursorPos;

    public HelperClientLink(HelperChannel channel, ILogger log)
    {
        _channel = channel;
        _log = log;
    }

    /// <summary>The pipe closed: the helper is gone, and the engine must fall back to the local desktop.</summary>
    public event Action? Faulted;

    /// <summary>The displays as the helper (SYSTEM) sees them, sent once after the handshake.</summary>
    public IReadOnlyList<DisplayDescriptor> Displays { get; private set; } = [];

    public void Start()
    {
        _readLoop = Task.Run(ReadLoopAsync);
        _writeLoop = Task.Run(WriteLoopAsync);
    }

    public void Register(HelperScreenCapturer capturer) => _capturers[capturer.Id] = capturer;

    public void Unregister(int id) => _capturers.TryRemove(id, out _);

    /// <summary>Queues a message to the helper. Sent in call order by the single writer, so input keeps its order.</summary>
    public void Post(HelperMessage message) => _outbound.Writer.TryWrite(message);

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (HelperMessage message in _outbound.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                await _channel.WriteAsync(message, _cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogWarning(e, "The elevation helper write loop failed");
        }
    }

    public ulong CursorId
    {
        get
        {
            lock (_cursorLock)
            {
                return _cursorId;
            }
        }
    }

    public HelperCursorShape? CursorShape(ulong id)
    {
        lock (_cursorLock)
        {
            return _cursorShape is { } shape && shape.Id == id ? shape : null;
        }
    }

    public (int X, int Y)? CursorPosition
    {
        get
        {
            lock (_cursorLock)
            {
                return _cursorPos;
            }
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                HelperMessage? m = await _channel.ReadAsync(_cts.Token).ConfigureAwait(false);
                if (m is null)
                {
                    break; // pipe closed
                }

                switch (m.UnionCase)
                {
                    case HelperMessage.UnionOneofCase.Frame:
                        if (_capturers.TryGetValue(m.Frame.Id, out HelperScreenCapturer? fc))
                        {
                            fc.Push(m.Frame);
                        }

                        break;
                    case HelperMessage.UnionOneofCase.Switched:
                        if (_capturers.TryGetValue(m.Switched.Id, out HelperScreenCapturer? sc))
                        {
                            sc.MarkSwitched();
                        }

                        break;
                    case HelperMessage.UnionOneofCase.Displays:
                        Displays = [.. m.Displays.Displays.Select(d => new DisplayDescriptor(d.Index, d.Name, d.X, d.Y, d.Width, d.Height, d.Scale == 0 ? 1.0 : d.Scale, FrameRotation.None, d.Primary, 0))];
                        break;
                    case HelperMessage.UnionOneofCase.CursorShape:
                        lock (_cursorLock)
                        {
                            _cursorShape = m.CursorShape;
                            _cursorId = m.CursorShape.Id;
                        }

                        break;
                    case HelperMessage.UnionOneofCase.CursorId:
                        lock (_cursorLock)
                        {
                            _cursorId = m.CursorId.Id;
                        }

                        break;
                    case HelperMessage.UnionOneofCase.CursorPos:
                        lock (_cursorLock)
                        {
                            _cursorPos = (m.CursorPos.X, m.CursorPos.Y);
                        }

                        break;
                }
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogWarning(e, "The elevation helper link failed");
        }
        finally
        {
            // Fault first (the elevator detaches the factory) so that when the capturers below report the desktop
            // switched, the engine rebuilds the streams onto the local desktop rather than another dead proxy.
            if (!_cts.IsCancellationRequested)
            {
                Faulted?.Invoke();
            }

            // Whether it faulted or stood down, the proxy capturers must stop waiting on a dead pipe.
            foreach (HelperScreenCapturer capturer in _capturers.Values)
            {
                capturer.MarkSwitched();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Post(new HelperMessage { Stop = true });
        _outbound.Writer.TryComplete();

        // Let the stop reach the helper before the pipe is cut, but do not hang on a dead one.
        if (_writeLoop is not null)
        {
            try
            {
                await _writeLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            {
                // best effort
            }
        }

        _cts.Cancel();
        foreach (Task? loop in new[] { _readLoop, _writeLoop })
        {
            if (loop is not null)
            {
                try
                {
                    await loop.ConfigureAwait(false);
                }
                catch
                {
                    // the loops' own logging covered it
                }
            }
        }

        await _channel.DisposeAsync().ConfigureAwait(false); // closes the pipe
        _cts.Dispose();
    }
}

/// <summary>
/// The engine's capturer for one display while a helper is attached: it hands out the latest frame the helper
/// pushed, waits for the next within the timeout, and reports the desktop switching so the engine rebuilds the
/// stream (a UAC closing, say). CPU-backed; each frame is its own buffer, so the last picture stays valid for
/// repeats.
/// </summary>
internal sealed class HelperScreenCapturer : IScreenCapturer
{
    private readonly object _lock = new();
    private byte[]? _frame;
    private int _width;
    private int _height;
    private int _stride;
    private long _ticks;
    private bool _hasNew;
    private bool _switched;
    private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public HelperScreenCapturer(int id, DisplayDescriptor display)
    {
        Id = id;
        Display = display;
    }

    public int Id { get; }

    public DisplayDescriptor Display { get; }

    public bool SupportsGpuTexture => false;

    public GpuApi GpuApi => GpuApi.None;

    /// <summary>Run when the engine disposes this capturer: the factory unregisters it and tells the helper to drop it.</summary>
    public Action? OnDispose { get; set; }

    public void Push(HelperFrame f)
    {
        TaskCompletionSource signal;
        lock (_lock)
        {
            _frame = f.Bgra.ToByteArray();
            _width = f.Width;
            _height = f.Height;
            _stride = f.Stride;
            _ticks = Stopwatch.GetTimestamp();
            _hasNew = true;
            signal = _signal;
        }

        signal.TrySetResult();
    }

    public void MarkSwitched()
    {
        TaskCompletionSource signal;
        lock (_lock)
        {
            _switched = true;
            signal = _signal;
        }

        signal.TrySetResult();
    }

    public async ValueTask<CaptureResult> AcquireFrameAsync(TimeSpan timeout, CancellationToken ct)
    {
        TimeSpan wait = timeout <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(33) : timeout;
        while (true)
        {
            Task signal;
            lock (_lock)
            {
                if (_switched)
                {
                    _switched = false;
                    return CaptureResult.Switched;
                }

                if (_hasNew && _frame is not null)
                {
                    _hasNew = false;
                    var frame = new CaptureFrame
                    {
                        Width = _width,
                        Height = _height,
                        Format = PixelFormat.Bgra32,
                        Stride = _stride,
                        TimestampTicks = _ticks,
                        Cpu = _frame,
                    };
                    return new CaptureResult(CaptureStatus.Frame, frame);
                }

                if (_signal.Task.IsCompleted)
                {
                    _signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                signal = _signal.Task;
            }

            Task done;
            try
            {
                done = await Task.WhenAny(signal, Task.Delay(wait, ct)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return CaptureResult.TimedOut;
            }

            if (done != signal)
            {
                return CaptureResult.TimedOut;
            }
        }
    }

    public void ForceFallbackPath()
    {
    }

    public ValueTask DisposeAsync()
    {
        OnDispose?.Invoke();
        return ValueTask.CompletedTask;
    }
}
