using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DeskPair.Desktop.ViewModels;

/// <summary>
/// The sessions open at once, as tabs in one window. Reaching four machines used to mean four windows to
/// arrange, hunt through the taskbar for, and close one by one; this is the same sessions with one window
/// around them.
///
/// Every session keeps running whether or not its tab is the one on screen — a background tab is still
/// connected, still receiving, and comes back instantly rather than reconnecting. What it does not do is
/// draw: the view of an unselected tab is hidden, so its frames are decoded and dropped rather than painted.
/// </summary>
public sealed partial class RemoteSessionsViewModel : ObservableObject
{
    public ObservableCollection<RemoteSessionViewModel> Sessions { get; } = [];

    [ObservableProperty]
    public partial RemoteSessionViewModel? Selected { get; set; }

    /// <summary>Raised when the last tab closes, so the window can go with it.</summary>
    public event Action? Emptied;

    public bool HasSessions => Sessions.Count > 0;

    /// <summary>Only worth a strip once there is more than one thing to switch between.</summary>
    public bool ShowTabs => Sessions.Count > 1;

    partial void OnSelectedChanged(RemoteSessionViewModel? value)
    {
        // Each view's visibility follows its own view model rather than a comparison in the binding, so the
        // views stay simple and a session always knows whether it is the one on screen.
        foreach (RemoteSessionViewModel session in Sessions)
        {
            session.IsActive = ReferenceEquals(session, value);
        }
    }

    /// <summary>
    /// One tab per target. Asking again for a machine that is already open selects the tab it is on rather
    /// than opening a second session to the same desk, which is almost never what was meant.
    /// </summary>
    public RemoteSessionViewModel Add(RemoteSessionViewModel session)
    {
        RemoteSessionViewModel? existing = Sessions.FirstOrDefault(
            s => string.Equals(s.Target, session.Target, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            Selected = existing;
            return existing;
        }

        session.CloseRequested += () => _ = CloseAsync(session);
        Sessions.Add(session);
        Selected = session;
        OnPropertyChanged(nameof(HasSessions));
        OnPropertyChanged(nameof(ShowTabs));
        return session;
    }

    [RelayCommand]
    public async Task CloseAsync(RemoteSessionViewModel? session)
    {
        if (session is null || !Sessions.Contains(session))
        {
            return;
        }

        // Pick the neighbour before removing, so closing the middle of a row lands somewhere sensible rather
        // than on nothing.
        int index = Sessions.IndexOf(session);
        Sessions.Remove(session);
        Selected = Sessions.Count == 0 ? null : Sessions[Math.Min(index, Sessions.Count - 1)];
        OnPropertyChanged(nameof(HasSessions));
        OnPropertyChanged(nameof(ShowTabs));

        await session.DisposeAsync().ConfigureAwait(false);
        if (Sessions.Count == 0)
        {
            Emptied?.Invoke();
        }
    }

    /// <summary>Ends every session, for a window that is closing.</summary>
    public async Task CloseAllAsync()
    {
        RemoteSessionViewModel[] all = [.. Sessions];
        Sessions.Clear();
        Selected = null;
        OnPropertyChanged(nameof(HasSessions));
        OnPropertyChanged(nameof(ShowTabs));
        foreach (RemoteSessionViewModel session in all)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }
}
