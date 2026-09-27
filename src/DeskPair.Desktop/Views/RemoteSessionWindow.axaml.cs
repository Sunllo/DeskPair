using Avalonia.Controls;
using DeskPair.Desktop.ViewModels;

namespace DeskPair.Desktop.Views;

/// <summary>
/// Holds every remote session the user has open, one per tab. The sessions themselves live in
/// <see cref="RemoteSessionsViewModel"/>; this window starts the ones added to it and closes when the last
/// one goes.
/// </summary>
public partial class RemoteSessionWindow : Window
{
    private bool _closing;

    public RemoteSessionWindow(RemoteSessionsViewModel sessions)
    {
        InitializeComponent();
        Sessions = sessions;
        DataContext = sessions;
        sessions.Emptied += () => Close();
        Closing += OnClosing;

        // The tab's display has the viewer's attention again; with displays open in other windows, the host
        // hears so and gives it the larger share of the link.
        Activated += (_, _) => sessions.Selected?.TabActivated();
    }

    public RemoteSessionsViewModel Sessions { get; }

    /// <summary>Adds a session to this window and starts it, selecting its tab.</summary>
    public void Open(RemoteSessionViewModel session)
    {
        RemoteSessionViewModel opened = Sessions.Add(session);
        if (!ReferenceEquals(opened, session))
        {
            // Already connected to this machine: its tab is now selected and the duplicate is not needed.
            _ = session.DisposeAsync();
            return;
        }

        _ = session.StartAsync();
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        e.Cancel = true;
        await Sessions.CloseAllAsync();
        Close();
    }
}
