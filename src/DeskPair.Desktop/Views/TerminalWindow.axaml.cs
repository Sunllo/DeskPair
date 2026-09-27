using Avalonia.Controls;
using Avalonia.Threading;
using DeskPair.Desktop.ViewModels;

namespace DeskPair.Desktop.Views;

public partial class TerminalWindow : Window
{
    private readonly TerminalViewModel _vm;
    private bool _closing;
    private bool _redrawQueued;

    public TerminalWindow(TerminalViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Terminal.Screen = vm.Screen;
        Terminal.Input += vm.SendInput;
        Terminal.GridSizeChanged += vm.Resize;
        Terminal.PasteRequested += vm.RequestPaste;

        // Output can arrive in many small messages; one redraw per frame is enough.
        vm.ScreenChanged += () =>
        {
            if (!_redrawQueued)
            {
                _redrawQueued = true;
                Dispatcher.UIThread.Post(() =>
                {
                    _redrawQueued = false;
                    Terminal.Refresh();
                }, DispatcherPriority.Render);
            }
        };
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TerminalViewModel.IsShellOpen) && vm.IsShellOpen)
            {
                Terminal.Focus();
            }
        };
        vm.CloseRequested += () => Close();
        PasswordBox.AttachedToVisualTree += (_, _) => PasswordBox.Focus();
        Opened += async (_, _) => await vm.StartAsync();
        Closing += OnClosing;
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        e.Cancel = true;
        await _vm.DisposeAsync();
        Close();
    }
}
