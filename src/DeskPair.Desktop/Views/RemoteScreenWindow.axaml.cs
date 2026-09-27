using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using DeskPair.Desktop.ViewModels;

namespace DeskPair.Desktop.Views;

/// <summary>
/// One of the host's displays in a window of its own, typically dragged onto the viewer's second monitor.
/// Closing it stops that display's stream and nothing else; the session and its tab carry on.
/// </summary>
public partial class RemoteScreenWindow : Window
{
    private readonly RemoteScreenViewModel _vm;
    private bool _closingFromSession;

    public RemoteScreenWindow(RemoteScreenViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        vm.Attach(Display);
        ApplyScrollMode();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RemoteScreenViewModel.FitToWindow))
            {
                ApplyScrollMode();
            }
        };
        vm.CloseRequested += () =>
        {
            _closingFromSession = true;
            Close();
        };
        Activated += (_, _) => vm.Activated();
        Closed += (_, _) =>
        {
            // Closed by the session (its display went, or it ended) is already accounted for there.
            if (!_closingFromSession)
            {
                vm.Closed();
            }
        };
    }

    /// <summary>Fit needs a finite viewport; 1:1 needs scrollbars.</summary>
    private void ApplyScrollMode()
    {
        ScrollBarVisibility mode = _vm.FitToWindow ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        DisplayScroller.HorizontalScrollBarVisibility = mode;
        DisplayScroller.VerticalScrollBarVisibility = mode;
    }
}
