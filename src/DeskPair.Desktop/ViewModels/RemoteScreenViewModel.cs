using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskPair.Desktop.Controls;

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

    public RemoteScreenViewModel(RemoteSessionViewModel owner, string name, int index)
    {
        Owner = owner;
        DisplayName = name;
        DisplayIndex = index;
        FitToWindow = owner.FitToWindow;
    }

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
