using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskPair.Core.Session.Controller;
using DeskPair.Desktop.Controls;
using DeskPair.Protocol.Messages;

namespace DeskPair.Desktop.ViewModels;

/// <summary>
/// One of the host's displays in a window of its own.
///
/// Not a session: a view onto the session in its tab, which owns the connection, the toolbar and everything
/// that is really session state -- quality, sound, recording. Copying those into every window would give the
/// user several switches for one thing. This one only frames its picture, asks for a fresh one, and brings the
/// session's tab forward. Input needs nothing of its own: the picture stamps its display on every mouse event,
/// and the host's keyboard focus follows the last click.
/// </summary>
public sealed partial class RemoteScreenViewModel : ObservableObject
{
    private RemoteDisplayView? _view;

    /// <summary>Keeps this window's display the size of this window, while the session follows windows.</summary>
    private readonly ResolutionFollower _follower;

    public RemoteScreenViewModel(RemoteSessionViewModel owner, string name, int index)
    {
        Owner = owner;
        DisplayName = name;
        DisplayIndex = index;
        FitToWindow = owner.FitToWindow;
        _follower = owner.NewFollower();
        _follower.StateChanged += () => Dispatcher.UIThread.Post(UpdateStatus);
    }

    /// <summary>This window's display is being kept the size of this window; Fit waits meanwhile.</summary>
    [ObservableProperty]
    public partial bool IsFollowing { get; set; }

    /// <summary>What following the window is doing, when there is something to say.</summary>
    [ObservableProperty]
    public partial string MatchWindowStatus { get; set; } = string.Empty;

    /// <summary>A request of this window's follower is waiting for its answer.</summary>
    internal bool IsWaiting => IsFollowing && _follower.IsWaiting;

    /// <summary>Starts or stops following, as the session decides; <paramref name="again"/> starts a running follower over.</summary>
    internal void Follow(bool on, DisplayInfo? info, bool again = false)
    {
        if (on && (!IsFollowing || again))
        {
            IsFollowing = true;
            FitToWindow = true;
            _follower.Start(DisplayIndex, info);
            ReportWindow();
        }
        else if (!on && IsFollowing)
        {
            IsFollowing = false;
            _ = _follower.StopAsync();
        }

        UpdateStatus();
    }

    /// <summary>What the host said about the displays, for this window's follower.</summary>
    internal void Heard(DisplayInfo? info, int changed, string failure)
    {
        if (IsFollowing)
        {
            _follower.Heard(DisplayIndex, info, changed, failure);
        }
    }

    /// <summary>The window is going: its display goes back to its own size if it is still the one set here.</summary>
    internal void StopFollowing()
    {
        Follow(false, null);
        _follower.Dispose();
    }

    private void ReportWindow()
    {
        if (_view is { } view)
        {
            (int width, int height, double uiScale) = view.FillingSize;
            _follower.Window(width, height, uiScale);
        }
    }

    private void UpdateStatus() => MatchWindowStatus = RemoteSessionViewModel.FollowStatus(IsFollowing, true, _follower);

    public RemoteSessionViewModel Owner { get; }

    /// <summary>The host's name for the display: what this window is matched by when indices move.</summary>
    public string DisplayName { get; }

    [ObservableProperty]
    public partial int DisplayIndex { get; set; }

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool FitToWindow { get; set; }

    /// <summary>Raised when the session wants this window gone: its display went, or the session ended.</summary>
    public event Action? CloseRequested;

    /// <summary>The window's picture. The session routes frames of this display here.</summary>
    public RemoteDisplayView? View => _view;

    public void Attach(RemoteDisplayView view)
    {
        _view = view;
        view.DisplayIndex = DisplayIndex;
        view.FitToWindow = FitToWindow;
        view.ShowRemoteCursor = Owner.ShowRemoteCursor;
        view.SmoothPlayback = Owner.SmoothPlayback;
        view.Origin = Owner.OriginOf(DisplayIndex);
        view.MouseInput += e => Owner.SendMouse(e);
        view.KeyInput += (e, down) => Owner.SendKey(e, down);
        view.FillingSizeChanged += ReportWindow;
        ReportWindow();
    }

    partial void OnDisplayIndexChanged(int value)
    {
        if (_view is not null)
        {
            _view.DisplayIndex = value;
            _view.Origin = Owner.OriginOf(value);
        }
    }

    partial void OnFitToWindowChanged(bool value)
    {
        if (_view is not null)
        {
            _view.FitToWindow = value;
            _view.InvalidateMeasure();
            _view.InvalidateVisual();
        }
    }

    /// <summary>The window came to the front: its display now has the viewer's attention.</summary>
    public void Activated() => Owner.FocusDisplay(DisplayIndex);

    /// <summary>The user closed the window: the session stops streaming this display.</summary>
    public void Closed() => Owner.CloseScreen(this);

    public void RequestClose() => CloseRequested?.Invoke();

    [RelayCommand]
    private Task RefreshAsync() => Owner.RefreshDisplayAsync(DisplayIndex);

    [RelayCommand]
    private void ShowSession() => App.ShowSession(Owner);
}
