using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using DeskPair.Desktop.ViewModels;

namespace DeskPair.Desktop.Views;

/// <summary>
/// Borderless card anchored above the tray at the bottom-right of the primary display, like a
/// notification: who is connected, what they may do, and a way to cut them off. It stays out of sight until
/// there is something to decide or someone is actually connected, and collapses to a small arrow handle in
/// the same corner rather than disappearing into the taskbar.
///
/// It is one window of the app rather than a program of its own, so closing it leaves DeskPair running. The
/// view model outlives it and the app builds a new card for the next connection, which is why every
/// subscription here is undone in <see cref="OnClosed"/>: otherwise each card that came and went would stay
/// attached to the view model for the life of the app.
/// </summary>
public partial class ConnectionManagerWindow : Window
{
    /// <summary>The card's width, and the width of the arrow handle it collapses to.</summary>
    private const double CardWidth = 390;
    private const double HandleWidth = 56;

    private readonly ConnectionManagerViewModel _vm;
    private bool _userMoved;

    public ConnectionManagerWindow(ConnectionManagerViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Width = CardWidth;
        Opened += (_, _) => DockBottomRight();
        SizeChanged += (_, _) => DockBottomRight();
        Header.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                BeginMoveDrag(e);
            }
        };

        vm.PropertyChanged += OnViewModelChanged;
        vm.Connections.CollectionChanged += OnConnectionsChanged;
        vm.EngineLost += OnEngineLost;
    }

    /// <summary>Brings the card in front of whatever the user is doing; pending approvals also stay on top.</summary>
    public void Surface(bool topmost)
    {
        if (topmost)
        {
            Topmost = true;
        }

        WindowState = WindowState.Normal;
        Show();
        Activate();
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.PropertyChanged -= OnViewModelChanged;
        _vm.Connections.CollectionChanged -= OnConnectionsChanged;
        _vm.EngineLost -= OnEngineLost;
        base.OnClosed(e);
    }

    private void OnConnectionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_vm.Connections.Any(c => c.IsPending))
        {
            Topmost = false;
        }

        if (_vm.Connections.Count == 0)
        {
            _ = CloseWhenIdleAsync();
        }
    }

    /// <summary>Nothing left to manage: the engine is gone, so the card has nothing to say.</summary>
    private void OnEngineLost() => Avalonia.Threading.Dispatcher.UIThread.Post(Close);

    /// <summary>The card exists only while someone is connected; the app opens a new one when that changes.</summary>
    private async Task CloseWhenIdleAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        if (_vm.Connections.Count == 0)
        {
            // Close, not hide: a hidden window stays in the lifetime's window list and would keep the app
            // alive under ShutdownMode.OnLastWindowClose long after the user closed the main window.
            Close();
        }
    }

    /// <summary>Collapsing shrinks the window itself, so only the handle sits over whatever is underneath.</summary>
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ConnectionManagerViewModel.IsCollapsed))
        {
            return;
        }

        Width = _vm.IsCollapsed ? HandleWidth : CardWidth;

        // A collapsed card was moved out of the way deliberately; expanding it should land where it started.
        Avalonia.Threading.Dispatcher.UIThread.Post(DockBottomRight);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _userMoved = true; // after a manual drag, stop re-docking on size changes
    }

    private void DockBottomRight()
    {
        if (_userMoved)
        {
            return;
        }

        Screen? screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null)
        {
            return;
        }

        PixelRect area = screen.WorkingArea;
        double scale = screen.Scaling;
        int width = (int)Math.Round(Bounds.Width * scale);
        int height = (int)Math.Round(Bounds.Height * scale);
        if (width == 0 || height == 0)
        {
            return;
        }

        Position = new PixelPoint(area.Right - width - (int)(8 * scale), area.Bottom - height - (int)(8 * scale));
    }
}
