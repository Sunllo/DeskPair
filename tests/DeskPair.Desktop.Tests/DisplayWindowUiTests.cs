using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using DeskPair.Desktop.Services;
using DeskPair.Desktop.ViewModels;
using DeskPair.Desktop.Views;
using DeskPair.Protocol.Messages;

namespace DeskPair.Desktop.Tests;

/// <summary>
/// A display window and the session tab have to survive being built: XAML that fails to load fails at the
/// click that opens it, and nothing before that would know.
/// </summary>
public class DisplayWindowUiTests
{
    private static RemoteSessionViewModel Session() => new("123456789", "me", new DesktopConfig(), NullLoggerFactory.Instance);

    [AvaloniaFact]
    public void A_display_window_builds_attaches_its_picture_and_tells_the_session_when_closed()
    {
        RemoteSessionViewModel session = Session();
        var screen = new RemoteScreenViewModel(session, "FAKE1", 1) { Title = "Office · Display 2" };

        var window = new RemoteScreenWindow(screen);
        window.Show();

        screen.View.ShouldNotBeNull("the window hands its picture to the view model");
        screen.View.DisplayIndex.ShouldBe(1, "mouse events are stamped with this display");
        window.Title.ShouldBe("Office · Display 2");

        screen.DisplayIndex = 0;
        screen.View.DisplayIndex.ShouldBe(0, "a display that moved moves the picture's stamp with it");

        // Closed by the user: the session hears it (and, with no connection, has nothing to send).
        window.Close();
    }

    [AvaloniaFact]
    public void A_display_window_closed_by_the_session_closes()
    {
        var screen = new RemoteScreenViewModel(Session(), "FAKE1", 1);
        var window = new RemoteScreenWindow(screen);
        window.Show();
        bool closed = false;
        window.Closed += (_, _) => closed = true;

        screen.RequestClose();

        closed.ShouldBeTrue();
    }

    /// <summary>
    /// Two displays, the usual case: one other to open, and the button opens it. It used to offer a menu with that
    /// single entry, and on a real two-display host the button was reported as doing nothing.
    /// </summary>
    [AvaloniaFact]
    public void With_one_other_display_the_button_opens_it_without_a_menu()
    {
        (RemoteSessionView view, List<string> log) = Tab(displays: 2);

        Click(view.DisplayWindowButton);

        view.OfferedMenu.ShouldBeNull();
        log.ShouldContain(l => l.StartsWith("Display window 1 not opened", StringComparison.Ordinal),
            "it went straight to opening the second display (and, with no connection here, said why it could not)");
    }

    [AvaloniaFact]
    public void With_several_the_button_offers_them_in_a_menu()
    {
        (RemoteSessionView view, _) = Tab(displays: 3);

        Click(view.DisplayWindowButton);

        MenuFlyout menu = view.OfferedMenu.ShouldNotBeNull();
        menu.IsOpen.ShouldBeTrue();
        menu.Items.Cast<MenuItem>().Select(i => (int)i.CommandParameter!).ShouldBe([1, 2], "every display but the tab's");
    }

    /// <summary>A session tab in a window, told by a host that streams several displays that it has <paramref name="displays"/>.</summary>
    private static (RemoteSessionView View, List<string> Log) Tab(int displays)
    {
        var log = new List<string>();
        var vm = new RemoteSessionViewModel("123456789", "me", new DesktopConfig(), new RecordingLogs(log));
        var view = new RemoteSessionView { DataContext = vm };
        new Window { Content = view, Width = 1200, Height = 800 }.Show();

        var info = new PeerInfo { Hostname = "Office", MultiDisplay = true };
        for (int i = 0; i < displays; i++)
        {
            info.Displays.Add(new DisplayInfo { Width = 2560, Height = 1440, X = 2560 * i, Name = $"DISPLAY{i + 1}", Primary = i == 0 });
        }

        vm.OnPeerInfo(info);
        Dispatcher.UIThread.RunJobs();
        return (view, log);
    }

    /// <summary>A real click, so a button that is hidden, disabled or covered does not pass for one that works.</summary>
    private static void Click(Button button)
    {
        var window = (Window)TopLevel.GetTopLevel(button)!;
        window.UpdateLayout();
        Point centre = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(centre, MouseButton.Left);
        window.MouseUp(centre, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class RecordingLogs(List<string> lines) : ILoggerFactory, ILogger
    {
        public ILogger CreateLogger(string categoryName) => this;

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Add(formatter(state, exception));
    }
}
