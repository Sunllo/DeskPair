using Avalonia.Controls;
using DeskPair.Desktop.ViewModels;

namespace DeskPair.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // The window has no title bar of its own; this strip is what it is dragged and double-clicked by.
        TitleStrip.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                BeginMoveDrag(e);
            }
        };
        TitleStrip.DoubleTapped += (_, _) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    public MainWindow(MainWindowViewModel vm)
        : this()
    {
        DataContext = vm;
        Activated += (_, _) => vm.Home.ReloadRecent();

        // Clicking the account at the foot of the rail. Modal over this window, because signing in is one
        // thing to finish rather than a page to wander away from.
        vm.SignInRequested += () => new SignInWindow(vm.Settings.Account).ShowDialog(this);

        // With the tray icon showing, closing the window leaves the desk reachable instead of quitting.
        Closing += (_, e) =>
        {
            if (App.Config.CloseToTray && !App.IsShuttingDown)
            {
                e.Cancel = true;
                Hide();
            }
        };
    }
}
