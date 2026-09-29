using DeskPair.Core.Video;
using DeskPair.Protocol.Messages;

namespace DeskPair.Core.Session.Controller;

/// <summary>Where a <see cref="ResolutionFollower"/> stands, for the toolbar to say.</summary>
public enum FollowState
{
    /// <summary>Not following.</summary>
    Off,

    /// <summary>Following; the display is the size of the window, or as near as it can be.</summary>
    Matched,

    /// <summary>A size has been asked for and the host has not answered yet.</summary>
    Waiting,

    /// <summary>The host refused the last size, or did not answer; <see cref="ResolutionFollower.Failure"/> says why.</summary>
    Failed,

    /// <summary>Somebody else changed the display since; it is left as they set it until this window changes size.</summary>
    Overridden,

    /// <summary>The display cannot be changed from here at all.</summary>
    Unsupported,
}

/// <summary>
/// Keeps one remote display the size of the window showing it, the way a Windows remote desktop session takes the
/// size of its window: when the window settles at a new size, the host is asked for that size (or the nearest mode
/// it has, <see cref="ResolutionFit"/>), and the picture is then shown 1:1.
///
/// Resizing is a stream of sizes and a change of mode takes a second, so it waits for the window to rest
/// (<see cref="Settle"/>), keeps one request in flight and asks for the latest size once that one is answered. A
/// refusal is not asked again until the window changes size. Somebody else's change is not fought: whoever spoke last
/// keeps the display until this window changes. Turned off, or moved to another display, it gives the display back
/// its original size -- if it is still the size this follower set.
///
/// Thread-safe: the window reports from the UI thread, answers arrive from the session, and its timers run on their
/// own; <see cref="StateChanged"/> may be raised on any of them.
/// </summary>
public sealed class ResolutionFollower : IDisposable
{
    /// <summary>How long the window must keep its size before the host is asked for it.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);

    /// <summary>How long an answer may take before the request counts as failed.</summary>
    public static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(6);

    private readonly object _lock = new();
    private readonly Func<int, Resolution?, Task> _ask;
    private readonly ITimer _settle;
    private readonly ITimer _timeout;

    private bool _on;
    private int _display = -1;
    private string? _name;
    private DisplayInfo? _info;
    private (int W, int H) _window;
    private double _uiScale = 1;

    /// <summary>The size asked for and not yet answered, and the window it was asked for; null when nothing is in flight.</summary>
    private (int W, int H, (int W, int H) Window)? _asked;

    /// <summary>The window changed size while a request was in flight: ask again once it is answered.</summary>
    private bool _stale;

    /// <summary>The window size at which asking failed, or somebody else took the display: not asked again at that size.</summary>
    private (int W, int H)? _leftAt;

    /// <summary>The size this follower set and the display still has, which is what turning off gives back.</summary>
    private (int W, int H)? _mine;

    /// <param name="time">The clock the waits run on.</param>
    /// <param name="ask">Sends a resolution request for a display index: a size, or null for the display's original.</param>
    public ResolutionFollower(TimeProvider time, Func<int, Resolution?, Task> ask)
    {
        _ask = ask;
        _settle = time.CreateTimer(_ => Evaluate(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _timeout = time.CreateTimer(_ => TimedOut(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public FollowState State { get; private set; }

    /// <summary>The host's reason when <see cref="State"/> is <see cref="FollowState.Failed"/>; empty for no answer.</summary>
    public string Failure { get; private set; } = string.Empty;

    public event Action? StateChanged;

    /// <summary>True while a request of this follower's is waiting for its answer.</summary>
    public bool IsWaiting
    {
        get
        {
            lock (_lock)
            {
                return _asked is not null;
            }
        }
    }

    /// <summary>Starts following display <paramref name="display"/>, as the host describes it in <paramref name="info"/>.</summary>
    public void Start(int display, DisplayInfo? info)
    {
        lock (_lock)
        {
            _on = true;
            _display = display;
            _name = info?.Name;
            _info = info;
            _leftAt = null;
            _stale = false;
            _asked = null;

            // Started again after a dropped connection, the host has put its screen back: nothing is this follower's.
            _mine = null;
            _timeout.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            SetState(Supported(info) ? FollowState.Matched : FollowState.Unsupported);
            _settle.Change(Settle, Timeout.InfiniteTimeSpan);
        }

        Raise();
    }

    /// <summary>
    /// Stops following. The display gets its original size back when it still has the one this follower set; whoever
    /// changed it since keeps it.
    /// </summary>
    public Task StopAsync()
    {
        (int Display, bool Restore) give;
        lock (_lock)
        {
            give = (_display, _on && IsMine());
            _on = false;
            _asked = null;
            _mine = null;
            _settle.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _timeout.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            SetState(FollowState.Off);
        }

        Raise();
        return give.Restore ? Send(give.Display, null) : Task.CompletedTask;
    }

    /// <summary>
    /// The window now holds <paramref name="width"/> x <paramref name="height"/> of the display's pixels at 1:1;
    /// <paramref name="uiScale"/> is the scale at which the host's own interface would look the size of the viewer's.
    /// </summary>
    public void Window(int width, int height, double uiScale)
    {
        lock (_lock)
        {
            if ((width, height) == _window && Math.Abs(uiScale - _uiScale) < 0.001)
            {
                return;
            }

            _window = (width, height);
            _uiScale = uiScale;
            if (_leftAt is { } left && left != _window)
            {
                _leftAt = null;
            }

            if (_on)
            {
                _settle.Change(Settle, Timeout.InfiniteTimeSpan);
            }
        }
    }

    /// <summary>
    /// What the host says about the followed display now -- its index and description after a change of list --
    /// and, when the news is an answer, which display it named and why it refused. Every list the session hears
    /// comes through here: an answer to this follower, another viewer's change, or one made at the desk.
    /// </summary>
    public void Heard(int display, DisplayInfo? info, int changed = -1, string failure = "")
    {
        int giveBack = -1;
        bool evaluate = false;
        lock (_lock)
        {
            if (!_on)
            {
                return;
            }

            if (info is not null && _name is not null && info.Name != _name)
            {
                // Another display in this window now: the one left gets its size back, and this one is followed.
                if (IsMine())
                {
                    giveBack = _display;
                }

                _asked = null;
                _mine = null;
                _leftAt = null;
                _timeout.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                _name = info.Name;
                _display = display;
                _info = info;
                SetState(Supported(info) ? FollowState.Matched : FollowState.Unsupported);
                _settle.Change(Settle, Timeout.InfiniteTimeSpan);
            }
            else
            {
                if (_info is null && info is not null)
                {
                    // The first description of the display: the window may have been waiting for it.
                    _settle.Change(Settle, Timeout.InfiniteTimeSpan);
                }

                _display = display;
                _info = info ?? _info;
                _name ??= info?.Name;
                if (Supported(_info) && State == FollowState.Unsupported)
                {
                    SetState(FollowState.Matched);
                }

                evaluate = Answer(changed, failure);
            }
        }

        Raise();
        if (evaluate)
        {
            Evaluate();
        }

        if (giveBack >= 0)
        {
            _ = Send(giveBack, null);
        }
    }

    /// <summary>Settles what the latest news means for a request in flight, or for the size this follower set; under the lock.</summary>
    private bool Answer(int changed, string failure)
    {
        bool aboutMine = changed == _display || changed < 0;
        if (_asked is { } asked)
        {
            if (failure.Length > 0 && aboutMine)
            {
                _asked = null;
                _timeout.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                _leftAt = asked.Window;
                Failure = failure;
                SetState(FollowState.Failed);
                return Ask();
            }

            if (_info is { } now && now.Width == asked.W && now.Height == asked.H)
            {
                _asked = null;
                _timeout.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                _mine = now.Original is null ? null : (asked.W, asked.H);
                SetState(FollowState.Matched);
                return Ask();
            }

            if (changed == _display && failure.Length == 0)
            {
                // The display changed, and not to what was asked: somebody else's request came after this one.
                _asked = null;
                _timeout.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                _mine = null;
                _leftAt = asked.Window;
                SetState(FollowState.Overridden);
                return Ask();
            }

            return false;
        }

        if (_mine is { } mine && _info is { } info && (info.Width, info.Height) != mine)
        {
            // Changed under this follower by somebody else: theirs to keep until this window changes.
            _mine = null;
            _leftAt = _window;
            SetState(FollowState.Overridden);
        }

        return false;
    }

    /// <summary>Whether to look again now that nothing is in flight: the window moved on meanwhile. Under the lock.</summary>
    private bool Ask()
    {
        bool again = _stale;
        _stale = false;
        return again;
    }

    private void TimedOut()
    {
        bool evaluate;
        lock (_lock)
        {
            if (_asked is not { } asked)
            {
                return;
            }

            _asked = null;
            _leftAt = asked.Window;
            Failure = string.Empty;
            SetState(FollowState.Failed);
            evaluate = Ask();
        }

        Raise();
        if (evaluate)
        {
            Evaluate();
        }
    }

    /// <summary>Decides whether the window's size is worth asking for now, and asks.</summary>
    private void Evaluate()
    {
        (int Display, Resolution? Mode)? request = null;
        lock (_lock)
        {
            if (!_on || _info is not { } info || _display < 0)
            {
                return;
            }

            if (_asked is not null)
            {
                _stale = true;
                return;
            }

            if (!Supported(info))
            {
                SetState(FollowState.Unsupported);
            }
            else if (_leftAt != _window)
            {
                FitChoice choice = ResolutionFit.Choose(_window.W, _window.H, info, _uiScale);
                switch (choice.Action)
                {
                    case FitAction.Set:
                        _asked = (choice.Mode!.Width, choice.Mode.Height, _window);
                        request = (_display, choice.Mode);
                        break;
                    case FitAction.Restore when info.Original is { } original:
                        _asked = (original.Width, original.Height, _window);
                        request = (_display, null);
                        break;
                    default:
                        if (State is FollowState.Waiting)
                        {
                            SetState(FollowState.Matched);
                        }

                        break;
                }

                if (request is not null)
                {
                    _timeout.Change(AnswerTimeout, Timeout.InfiniteTimeSpan);
                    SetState(FollowState.Waiting);
                }
            }
        }

        Raise();
        if (request is { } r)
        {
            _ = Send(r.Display, r.Mode);
        }
    }

    /// <summary>
    /// Sends a request and never throws: a session on its way out may refuse to send, and this runs on timers where an
    /// exception would take the program down. A request that never left is answered by the timeout like any other.
    /// </summary>
    private async Task Send(int display, Resolution? mode)
    {
        try
        {
            await _ask(display, mode).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Counted as unanswered; the timeout says so.
        }
    }

    /// <summary>The display is still the size this follower set. Under the lock.</summary>
    private bool IsMine() => _mine is { } mine && _info is { } info && (info.Width, info.Height) == mine && info.Original is not null;

    private static bool Supported(DisplayInfo? info) => info is not null && (info.AnySize is not null || info.Modes.Count > 0);

    private FollowState _raised = FollowState.Off;

    private void SetState(FollowState state) => State = state;

    private void Raise()
    {
        FollowState now;
        lock (_lock)
        {
            now = State;
            if (now == _raised && now != FollowState.Failed)
            {
                return;
            }

            _raised = now;
        }

        StateChanged?.Invoke();
    }

    public void Dispose()
    {
        _settle.Dispose();
        _timeout.Dispose();
    }
}
