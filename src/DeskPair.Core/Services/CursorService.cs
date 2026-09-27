using Google.Protobuf;
using Microsoft.Extensions.Logging;
using DeskPair.Core.Session;
using DeskPair.Platform.Abstractions.Cursor;
using DeskPair.Protocol.Messages;

namespace DeskPair.Core.Services;

/// <summary>
/// Polls the cursor at 30 Hz. Each shape is sent as CursorData once per subscriber, then referenced by id;
/// positions are sent whenever they change.
///
/// A viewer that is not drawing the remote pointer still needs the shape — its own pointer takes that shape,
/// which is the whole point of not compositing the cursor into the picture — but it has no use for the
/// position, so that is the half switched off when "show the remote pointer as well as yours" is off.
/// </summary>
public sealed class CursorService : PublisherService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(33);

    private readonly ICursorProvider _cursor;
    private readonly TimeProvider _time;
    private readonly Dictionary<int, HashSet<ulong>> _sentShapes = new();

    /// <summary>Connections that asked not to be sent positions. Absent means yes, so a viewer that never says loses nothing.</summary>
    private readonly HashSet<int> _positionsOff = new();

    /// <summary>Connections that have just asked for positions again and need the current one without waiting for a move.</summary>
    private readonly HashSet<int> _positionsDue = new();
    private readonly object _lock = new();
    private ulong _currentId;
    private (int X, int Y)? _lastPos;

    public CursorService(ICursorProvider cursor, TimeProvider time, ILogger log)
        : base("cursor", log)
    {
        _cursor = cursor;
        _time = time;
    }

    /// <summary>Connections whose own input moved the cursor recently are not told about that move (echo suppression).</summary>
    public Func<int, bool>? IsEchoFor { get; set; }

    /// <summary>Whether a connection wants cursor positions; shapes are sent either way.</summary>
    public void SetWantsPosition(int connectionId, bool wants)
    {
        lock (_lock)
        {
            if (wants)
            {
                // Turning it back on mid-session must not wait for the next mouse move to show a pointer.
                if (_positionsOff.Remove(connectionId))
                {
                    _positionsDue.Add(connectionId);
                }
            }
            else
            {
                _positionsOff.Add(connectionId);
                _positionsDue.Remove(connectionId);
            }
        }
    }

    private bool WantsPosition(int connectionId)
    {
        lock (_lock)
        {
            return !_positionsOff.Contains(connectionId);
        }
    }

    protected override async ValueTask OnSubscribedAsync(IServiceSubscriber subscriber, CancellationToken ct)
    {
        lock (_lock)
        {
            _sentShapes[subscriber.ConnectionId] = new HashSet<ulong>();
        }

        ulong id = _cursor.GetCurrentCursorId();
        Message? shape = ShapeMessageFor(subscriber.ConnectionId, id);
        if (shape is not null)
        {
            await subscriber.PublishAsync(shape, MessagePriority.Control, ct).ConfigureAwait(false);
        }

        if (WantsPosition(subscriber.ConnectionId) && _cursor.GetCursorPosition() is { } pos)
        {
            await subscriber.PublishAsync(PositionMessage(pos), MessagePriority.Input, ct).ConfigureAwait(false);
        }
    }

    protected override void OnUnsubscribed(int connectionId)
    {
        lock (_lock)
        {
            _sentShapes.Remove(connectionId);
            _positionsOff.Remove(connectionId);
            _positionsDue.Remove(connectionId);
        }
    }

    protected override async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(PollInterval, _time);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            ulong id = _cursor.GetCurrentCursorId();
            if (id != _currentId)
            {
                _currentId = id;
                foreach (IServiceSubscriber s in Subscribers)
                {
                    Message? shape = ShapeMessageFor(s.ConnectionId, id);
                    if (shape is not null)
                    {
                        await s.PublishAsync(shape, MessagePriority.Control, ct).ConfigureAwait(false);
                    }
                }
            }

            (int X, int Y)? pos = _cursor.GetCursorPosition();
            if (pos is null)
            {
                continue;
            }

            bool moved = pos != _lastPos;
            _lastPos = pos;
            Message msg = PositionMessage(pos.Value);
            foreach (IServiceSubscriber s in Subscribers)
            {
                bool due;
                lock (_lock)
                {
                    due = _positionsDue.Remove(s.ConnectionId);
                }

                if (!WantsPosition(s.ConnectionId) || (!moved && !due))
                {
                    continue;
                }

                // Echo suppression is about a move this viewer caused; one it just asked for is not an echo.
                if (!due && IsEchoFor?.Invoke(s.ConnectionId) == true)
                {
                    continue;
                }

                await s.PublishAsync(msg, MessagePriority.Input, ct).ConfigureAwait(false);
            }
        }
    }

    private Message? ShapeMessageFor(int connectionId, ulong id)
    {
        bool sendFull;
        lock (_lock)
        {
            sendFull = _sentShapes.TryGetValue(connectionId, out HashSet<ulong>? sent) && sent.Add(id);
        }

        if (!sendFull)
        {
            return new Message { CursorId = new CursorId { Id = id } };
        }

        CursorImage? image = _cursor.GetCursorImage(id);
        if (image is null)
        {
            return new Message { CursorId = new CursorId { Id = id } };
        }

        return new Message
        {
            CursorData = new CursorData
            {
                Id = id,
                Hotx = image.HotX,
                Hoty = image.HotY,
                Width = image.Width,
                Height = image.Height,
                Bgra = ByteString.CopyFrom(image.Bgra.Span),
            },
        };
    }

    private static Message PositionMessage((int X, int Y) pos) =>
        new() { CursorPosition = new CursorPosition { X = pos.X, Y = pos.Y } };
}
