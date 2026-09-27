using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeskPair.Core.Session.Controller;
using DeskPair.Desktop;
using DeskPair.Desktop.Services;
using DeskPair.Desktop.ViewModels;
using DeskPair.Desktop.Views;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Abstractions.Codec;
using DeskPair.Protocol.Ipc;
using DeskPair.Protocol.Messages;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeskPair.Tools.Screenshots;

/// <summary>One method per picture. Each builds the product's own window around sample data and saves it.</summary>
internal sealed class Shots(string output, string scratch)
{
    public void All()
    {
        Home();
        Devices();
        History();
        Security();
        RemoteSession();
        FileTransfer();
        Terminal();
        IncomingRequest();
    }

    private void Home() => Save(MainWindow(MainWindowViewModel.HomeSection), 1000, 660, "home.png");

    private void Devices() => Save(MainWindow(MainWindowViewModel.DevicesSection), 1000, 660, "devices.png");

    private void History()
    {
        MainWindow window = MainWindow(MainWindowViewModel.HistorySection);
        Save(window, 1000, 660, "history.png", before: () =>
        {
            // The page asks the engine for its record as it opens; there is none here, so it is handed the sample.
            var vm = (MainWindowViewModel)window.DataContext!;
            vm.History.Rows.Clear();
            foreach (ConnectionHistoryEntry entry in Samples.History())
            {
                vm.History.Rows.Add(ConnectionHistoryRow.From(entry, t => t));
            }

            vm.History.Problem = string.Empty;
            vm.History.IsEmpty = false;
        });
    }

    private void Security()
    {
        MainWindow window = MainWindow(MainWindowViewModel.SettingsSection);
        Save(window, 1000, 760, "settings-security.png", before: () =>
            window.GetVisualDescendants().OfType<TabControl>().First().SelectedIndex = 4);
    }

    private void RemoteSession()
    {
        var sessions = new RemoteSessionsViewModel();
        sessions.Add(Connected(new RemoteSessionViewModel(Samples.BuildServer, Samples.ThisDesk, App.Config, NullLoggerFactory.Instance), "build-server", "Ubuntu 24.04"));
        RemoteSessionViewModel reception = sessions.Add(
            Connected(new RemoteSessionViewModel(Samples.Reception, Samples.ThisDesk, App.Config, NullLoggerFactory.Instance), "Reception", "Windows"));

        byte[] desktop = MockDesktop.Render(1920, 1080);
        var window = new RemoteSessionWindow(sessions);
        Save(window, 1440, 900, "remote-session.png", before: () => reception.OnVideoFrame(0, new DecodedFrame
        {
            Width = 1920,
            Height = 1080,
            Format = PixelFormat.Bgra32,
            Cpu = desktop,
            Stride = 1920 * 4,
        }));
    }

    private void FileTransfer()
    {
        var vm = Connected(new FileTransferViewModel(Samples.BuildServer, Samples.ThisDesk, App.Config, NullLoggerFactory.Instance), "build-server", "Ubuntu 24.04");

        // The local side has listed the sample folder; its path would name the account this runs under.
        vm.LocalPath = @"C:\Users\alice\Documents";
        vm.RemotePath = "/home/alice";
        vm.RemoteEntries.Clear();
        foreach ((string name, bool dir, long size, int days) in new (string, bool, long, int)[]
        {
            ("backups", true, 0, 1), ("Documents", true, 0, 3), ("projects", true, 0, 0),
            ("deploy.sh", false, 2_104, 6), ("notes.md", false, 11_372, 1), ("site-2026-09.tar.gz", false, 184_320_000, 2),
        })
        {
            vm.RemoteEntries.Add(new FileRow(name, dir, size, Samples.LocalDay(-days)));
        }

        Save(new FileTransferWindow(vm), 1100, 700, "file-transfer.png");
    }

    private void Terminal()
    {
        var vm = Connected(new TerminalViewModel(Samples.BuildServer, Samples.ThisDesk, App.Config, NullLoggerFactory.Instance), "build-server", "Ubuntu 24.04");
        var window = new TerminalWindow(vm);
        Save(window, 960, 420, "terminal.png", before: () =>
        {
            vm.OnTerminal(new TerminalResponse { Opened = new TerminalOpened { Id = 0, Identity = "alice", Shell = "bash" } });
            vm.OnTerminal(new TerminalResponse { Output = new TerminalOutput { Id = 0, Data = Google.Protobuf.ByteString.CopyFrom(Samples.TerminalOutput()) } });
        });
    }

    /// <summary>The card that asks the person at this computer whether to let somebody in.</summary>
    private void IncomingRequest()
    {
        HostLink host = Samples.Host();
        var vm = new ConnectionManagerViewModel(host);
        host.Deliver(new IpcMessage
        {
            ApprovalRequest = new ApprovalRequest
            {
                ConnId = 1,
                PeerId = Samples.Reception,
                PeerName = "Reception",
                PeerPlatform = "Windows",
                ConnType = Protocol.Rendezvous.ConnType.ConnRemote,
                TimeoutMs = 30_000,
            },
        });

        // Drawn at 192 dpi, this card alone comes out at four times its size rather than two (its shadow and
        // layers, most likely, scaled twice). So it is drawn at 96 dpi, magnified by a layout transform instead,
        // which gives the same sharp picture at twice the size, with the corners around the card transparent.
        var card = new ConnectionManagerWindow(vm);
        var content = (Control)card.Content!;
        card.Content = null;
        content.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        var magnified = new LayoutTransformControl
        {
            LayoutTransform = new Avalonia.Media.ScaleTransform(2, 2),
            Child = content,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
        };
        var plain = new Window
        {
            Width = 780,
            Height = 1200,
            Background = Avalonia.Media.Brushes.Transparent,
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
            DataContext = vm,
            Content = magnified,
        };
        plain.Show();
        Settle();

        var size = new PixelSize((int)Math.Ceiling(magnified.Bounds.Width), (int)Math.Ceiling(magnified.Bounds.Height));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
        bitmap.Render(magnified);
        string path = Path.Combine(output, "incoming-request.png");
        bitmap.Save(path);
        plain.Close();
        Console.WriteLine($"{path}  {size.Width / 2}x{size.Height / 2} (saved at 2x)");
    }

    private MainWindow MainWindow(int section)
    {
        HostLink host = Samples.Host();

        // Written where the page reads it again: signing in reloads the list from its file.
        string devices = Path.Combine(scratch, "devices.json");
        Samples.Devices().Save(devices);
        var vm = new MainWindowViewModel(
            new HomeViewModel(host, devices) { IsServiceConnected = true },
            new DeviceListViewModel(Samples.Devices(), Samples.Presence, action => action(), path: devices),
            new IncomingConnectionsViewModel(host),
            new ConnectionHistoryViewModel(host),
            new SettingsViewModel(host),
            new UpdateNoticeViewModel());

        // Signed in, as a made-up account: the device list is the account's, and is only shown to somebody who has one.
        vm.IsSignedIn = true;
        vm.AccountLine = "alice@example.com";
        vm.AccountDetail = "Studio PC";
        vm.Devices.IsSignedIn = true;
        vm.SelectedSection = section;
        return new MainWindow(vm);
    }

    /// <summary>A session that has been let in, to the machine and system named.</summary>
    private static T Connected<T>(T session, string name, string platform)
        where T : SessionViewModelBase
    {
        session.OnStateChanged(ControllerSessionState.Authorized);
        session.IsConnected = true;
        session.OnPeerInfo(new PeerInfo
        {
            Hostname = name,
            Platform = platform,
            Username = "alice",
            Version = "0.4.3",
            CurrentDisplay = 0,
            Displays = { new DisplayInfo { Width = 1920, Height = 1080, Name = "Display 1", Online = true, Primary = true, Scale = 1 } },
        });
        return session;
    }

    /// <summary>
    /// Shows <paramref name="window"/>, lets it settle, runs <paramref name="before"/> and saves it at twice its size.
    /// A null height is the window's own, for one that sizes itself to what it holds.
    /// </summary>
    private void Save(Window window, int width, int? height, string file, Action? before = null)
    {
        window.Width = width;
        if (height is { } h)
        {
            window.Height = h;
        }

        window.Show();
        Settle();
        before?.Invoke();
        Settle();

        int pixelsHigh = height ?? (int)Math.Ceiling(window.ClientSize.Height);
        using var bitmap = new RenderTargetBitmap(new PixelSize(width * 2, pixelsHigh * 2), new Vector(192, 192));
        bitmap.Render(window);
        string path = Path.Combine(output, file);
        bitmap.Save(path);
        window.Close();
        Console.WriteLine($"{path}  {width}x{pixelsHigh} (saved at 2x)");
    }

    private static void Settle()
    {
        for (int i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }
}
