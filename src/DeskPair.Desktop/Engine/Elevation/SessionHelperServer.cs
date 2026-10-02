using System.Collections.Concurrent;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Abstractions.Cursor;
using DeskPair.Platform.Abstractions.Input;
using DeskPair.Protocol.Helper;

namespace DeskPair.Desktop.Engine.Elevation;

/// <summary>
/// The SYSTEM helper's side of the link, after the handshake: it captures the secure desktop and injects into it
/// on the engine's behalf. Platform-neutral -- it is handed the platform's own capture, input and cursor -- so
/// the same loop runs whatever the OS; the Windows role supplies the Windows implementations. It never touches
/// the network: it speaks only to the one engine that raised it, over the one pipe.
/// </summary>
internal sealed class SessionHelperServer
{
    private readonly HelperChannel _channel;
    private readonly IDisplayEnumerator _displays;
    private readonly IScreenCapturerFactory _capturers;
    private readonly IInputInjector _injector;
    private readonly ICursorProvider _cursor;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<int, CapturePump> _pumps = new();

    public SessionHelperServer(HelperChannel channel, IDisplayEnumerator displays, IScreenCapturerFactory capturers, IInputInjector injector, ICursorProvider cursor, ILogger log)
    {
        _channel = channel;
        _displays = displays;
        _capturers = capturers;
        _injector = injector;
        _cursor = cursor;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await SendDisplaysAsync(cts.Token).ConfigureAwait(false);
        Task cursorLoop = Task.Run(() => CursorLoopAsync(cts.Token), cts.Token);
        try
        {
            while (!cts.IsCancellationRequested)
            {
                HelperMessage? m = await _channel.ReadAsync(cts.Token).ConfigureAwait(false);
                if (m is null)
                {
                    break; // the engine closed the pipe
                }

                switch (m.UnionCase)
                {
                    case HelperMessage.UnionOneofCase.Create:
                        StartCapture(m.Create, cts.Token);
                        break;
                    case HelperMessage.UnionOneofCase.Dispose:
                        StopCapture(m.Dispose.Id);
                        break;
                    case HelperMessage.UnionOneofCase.Mouse:
                        Inject(() => _injector.InjectMouse(ToMouse(m.Mouse), new VirtualScreenRect(m.Mouse.VsX, m.Mouse.VsY, m.Mouse.VsW, m.Mouse.VsH)));
                        break;
                    case HelperMessage.UnionOneofCase.Key:
                        Inject(() => _injector.InjectKey(ToKey(m.Key)));
                        break;
                    case HelperMessage.UnionOneofCase.LockKeys:
                        Inject(() => _injector.SetLockKeyStates(new LockKeyStates(m.LockKeys.CapsLock, m.LockKeys.NumLock, m.LockKeys.ScrollLock)));
                        break;
                    case HelperMessage.UnionOneofCase.ReleaseAll:
                        Inject(_injector.ReleaseAll);
                        break;
                    case HelperMessage.UnionOneofCase.Stop:
                        return;
                }
            }
        }
        finally
        {
            cts.Cancel();
            foreach (CapturePump pump in _pumps.Values)
            {
                await pump.DisposeAsync().ConfigureAwait(false);
            }

            try
            {
                await cursorLoop.ConfigureAwait(false);
            }
            catch
            {
                // shutting down
            }
        }
    }

    private void Inject(Action action)
    {
        try
        {
            _injector.EnsureInputDesktop();
            action();
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Could not inject on the secure desktop");
        }
    }

    private static MouseInput ToMouse(HelperMouse m) => new((MouseAction)m.Action, (MouseButtons)m.Buttons, m.X, m.Y, m.Wheel);

    private static KeyInput ToKey(HelperKey k) => new((KeyInputMode)k.Mode, k.Down, k.Code, (ControlKey)k.Control, k.Text.Length > 0 ? k.Text : null);

    private void StartCapture(CreateCapturer create, CancellationToken ct)
    {
        DisplayDescriptor? display = _displays.GetDisplays().FirstOrDefault(d => d.Index == create.Display);
        if (display is not { } descriptor)
        {
            _log.LogWarning("The engine asked to capture display {Display}, which this helper does not see", create.Display);
            return;
        }

        StopCapture(create.Id);
        var pump = new CapturePump(create.Id, _capturers.Create(descriptor, create.PreferGpu), _channel, _log);
        _pumps[create.Id] = pump;
        pump.Start(ct);
    }

    private void StopCapture(int id)
    {
        if (_pumps.TryRemove(id, out CapturePump? pump))
        {
            _ = pump.DisposeAsync();
        }
    }

    private async Task SendDisplaysAsync(CancellationToken ct)
    {
        var message = new HelperDisplays();
        foreach (DisplayDescriptor d in _displays.GetDisplays())
        {
            message.Displays.Add(new HelperDisplay { Index = d.Index, Name = d.Name, X = d.X, Y = d.Y, Width = d.Width, Height = d.Height, Scale = d.Scale, Primary = d.IsPrimary });
        }

        await _channel.WriteAsync(new HelperMessage { Displays = message }, ct).ConfigureAwait(false);
    }

    private async Task CursorLoopAsync(CancellationToken ct)
    {
        ulong lastId = 0;
        (int X, int Y)? lastPos = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                ulong id = _cursor.GetCurrentCursorId();
                if (id != lastId)
                {
                    lastId = id;
                    if (_cursor.GetCursorImage(id) is { } image)
                    {
                        await _channel.WriteAsync(new HelperMessage
                        {
                            CursorShape = new HelperCursorShape { Id = image.Id, HotX = image.HotX, HotY = image.HotY, Width = image.Width, Height = image.Height, Bgra = ByteString.CopyFrom(image.Bgra.Span) },
                        }, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        await _channel.WriteAsync(new HelperMessage { CursorId = new HelperCursorId { Id = id } }, ct).ConfigureAwait(false);
                    }
                }

                (int X, int Y)? pos = _cursor.GetCursorPosition();
                if (pos is { } p && pos != lastPos)
                {
                    lastPos = pos;
                    await _channel.WriteAsync(new HelperMessage { CursorPos = new HelperCursorPos { X = p.X, Y = p.Y } }, ct).ConfigureAwait(false);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(50), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "The helper cursor loop failed");
        }
    }

    /// <summary>One display being captured and pushed to the engine, until the desktop switches or it is disposed.</summary>
    private sealed class CapturePump(int id, IScreenCapturer capturer, HelperChannel channel, ILogger log) : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private Task? _loop;
        private uint _seq;

        public void Start(CancellationToken ct)
        {
            CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            _loop = Task.Run(() => LoopAsync(linked.Token), linked.Token);
        }

        private async Task LoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    CaptureResult result = await capturer.AcquireFrameAsync(TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);
                    switch (result.Status)
                    {
                        case CaptureStatus.Frame:
                            CaptureFrame frame = result.Frame;
                            if (!frame.Cpu.IsEmpty)
                            {
                                await channel.WriteAsync(new HelperMessage
                                {
                                    Frame = new HelperFrame { Id = id, Seq = ++_seq, Width = frame.Width, Height = frame.Height, Stride = frame.Stride, Bgra = ByteString.CopyFrom(frame.Cpu.Span) },
                                }, ct).ConfigureAwait(false);
                            }

                            break;
                        case CaptureStatus.DesktopSwitched:
                            await channel.WriteAsync(new HelperMessage { Switched = new HelperSwitched { Id = id } }, ct).ConfigureAwait(false);
                            return; // the engine rebuilds the stream; this capturer is done
                        case CaptureStatus.Error:
                            log.LogWarning(result.Error, "Capturing the secure desktop failed");
                            return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                log.LogWarning(e, "The helper capture loop failed");
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            if (_loop is not null)
            {
                try
                {
                    await _loop.ConfigureAwait(false);
                }
                catch
                {
                    // going anyway
                }
            }

            await capturer.DisposeAsync().ConfigureAwait(false);
            _cts.Dispose();
        }
    }
}
