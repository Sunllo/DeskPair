using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using DeskPair.Desktop.Services;
using DeskPair.Core.Session.Controller;
using DeskPair.Core.Terminal;
using DeskPair.Desktop.Localization;
using DeskPair.Protocol.Messages;
using DeskPair.Protocol.Rendezvous;

namespace DeskPair.Desktop.ViewModels;

/// <summary>
/// A terminal on another computer: one terminal connection, one shell at a time, drawn from a
/// <see cref="TerminalScreen"/> the host's output is fed into.
///
/// A shell ends when the connection does -- there is no detaching and coming back, because a root shell
/// left running with nobody attached is the one thing worse than losing it -- and the window says so
/// rather than leaving a frozen screen. While the connection is still up, a new shell is one click.
/// </summary>
public sealed partial class TerminalViewModel : SessionViewModelBase
{
    /// <summary>How long the window size has to settle before the host is told: dragging an edge would otherwise send a resize per frame.</summary>
    private static readonly TimeSpan ResizeSettle = TimeSpan.FromMilliseconds(150);

    private readonly DispatcherTimer _resizeTimer;
    private int _terminalId;
    private int _columns = 80;
    private int _rows = 24;
    private string _host = string.Empty;
    private string? _pendingPaste;

    public TerminalViewModel(string target, string myId, DesktopConfig config, ILoggerFactory logs)
        : base(target, myId, config, logs)
    {
        Screen = new TerminalScreen(_columns, _rows);
        Screen.TitleChanged += _ => UpdateTitle();
        _resizeTimer = new DispatcherTimer { Interval = ResizeSettle };
        _resizeTimer.Tick += (_, _) =>
        {
            _resizeTimer.Stop();
            if (IsShellOpen && Session is { } session)
            {
                _ = session.ResizeTerminalAsync(_terminalId, _columns, _rows, Cts.Token).AsTask();
            }
        };
    }

    protected override ConnType ConnType => ConnType.ConnTerminal;

    public TerminalScreen Screen { get; }

    /// <summary>Raised on the UI thread when the screen has something new to draw.</summary>
    public event Action? ScreenChanged;

    [ObservableProperty]
    public partial string Identity { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ShellName { get; set; } = string.Empty;

    /// <summary>A sentence over the terminal: the shell ended, it could not be opened, the host does not allow one.</summary>
    [ObservableProperty]
    public partial string Notice { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsShellOpen { get; set; }

    [ObservableProperty]
    public partial bool CanOpenNewShell { get; set; }

    [ObservableProperty]
    public partial bool PastePending { get; set; }

    [ObservableProperty]
    public partial string PasteQuestion { get; set; } = string.Empty;

    public bool HasNotice => Notice.Length > 0;

    partial void OnNoticeChanged(string value) => OnPropertyChanged(nameof(HasNotice));

    protected override async Task OnAuthorizedAsync()
    {
        // An old host cannot grant the permission and a host that does not allow a terminal will not, so
        // the one check answers both before anything is sent -- sending anyway would be a scope violation.
        if (Session?.PeerInfo is not { } info || !info.Granted.Contains(Permission.PermTerminal))
        {
            Notice = Strings.Get("terminal.notAllowed");
            return;
        }

        _host = info.Hostname;
        await OpenShellAsync();
    }

    [RelayCommand]
    private async Task OpenShellAsync()
    {
        if (Session is not { State: ControllerSessionState.Authorized } session)
        {
            return;
        }

        _terminalId++;
        CanOpenNewShell = false;
        Notice = Strings.Get("terminal.opening");
        Screen.Feed("\u001bc"u8); // a fresh screen for a fresh shell
        ScreenChanged?.Invoke();
        await session.OpenTerminalAsync(_terminalId, _columns, _rows, Cts.Token);
    }

    public override void OnTerminal(TerminalResponse response) => Dispatcher.UIThread.Post(() =>
    {
        switch (response.UnionCase)
        {
            case TerminalResponse.UnionOneofCase.Opened when response.Opened.Id == _terminalId:
                Identity = response.Opened.Identity;
                ShellName = response.Opened.Shell;
                IsShellOpen = true;
                Notice = string.Empty;
                UpdateTitle();
                break;
            case TerminalResponse.UnionOneofCase.Output when response.Output.Id == _terminalId:
                Screen.Feed(response.Output.Data.Span);
                foreach (byte[] reply in Screen.TakeReplies())
                {
                    SendInput(reply);
                }

                ScreenChanged?.Invoke();
                break;
            case TerminalResponse.UnionOneofCase.Exit when response.Exit.Id == _terminalId:
                IsShellOpen = false;
                Notice = response.Exit.Reason is "the shell exited" or ""
                    ? Strings.Format("terminal.ended", response.Exit.Code)
                    : Strings.Format("terminal.endedReason", response.Exit.Reason);
                CanOpenNewShell = IsConnected;
                break;
            case TerminalResponse.UnionOneofCase.Error when response.Error.Id == _terminalId:
                IsShellOpen = false;
                Notice = Strings.Format("terminal.failed", response.Error.Message);
                CanOpenNewShell = IsConnected;
                break;
        }
    });

    public override void OnClosed(string reason)
    {
        base.OnClosed(reason);
        Dispatcher.UIThread.Post(() =>
        {
            if (IsShellOpen)
            {
                Notice = Strings.Get("terminal.connectionLost");
            }

            IsShellOpen = false;
            CanOpenNewShell = false;
        });
    }

    /// <summary>What the user typed; nothing is sent once the shell has ended.</summary>
    public void SendInput(byte[] bytes)
    {
        if (IsShellOpen && Session is { State: ControllerSessionState.Authorized } session)
        {
            _ = session.SendTerminalInputAsync(_terminalId, bytes, Cts.Token).AsTask();
        }
    }

    /// <summary>The window's grid changed. The screen follows at once; the host after the size has settled.</summary>
    public void Resize(int columns, int rows)
    {
        if (columns == _columns && rows == _rows)
        {
            return;
        }

        _columns = columns;
        _rows = rows;
        Screen.Resize(columns, rows);
        ScreenChanged?.Invoke();
        _resizeTimer.Stop();
        _resizeTimer.Start();
    }

    /// <summary>
    /// A paste of more than one line is asked about first: each line runs as a command, and text copied
    /// from a web page can carry lines nobody saw.
    /// </summary>
    public void RequestPaste(string text)
    {
        if (!TerminalKeys.IsMultiLine(text))
        {
            SendInput(TerminalKeys.Paste(text, Screen.BracketedPaste));
            return;
        }

        _pendingPaste = text;
        int lines = text.TrimEnd('\r', '\n').Split('\n').Length;
        PasteQuestion = Strings.Format("terminal.pasteConfirm", lines);
        PastePending = true;
    }

    [RelayCommand]
    private void ConfirmPaste()
    {
        if (_pendingPaste is { } text)
        {
            SendInput(TerminalKeys.Paste(text, Screen.BracketedPaste));
        }

        _pendingPaste = null;
        PastePending = false;
    }

    [RelayCommand]
    private void CancelPaste()
    {
        _pendingPaste = null;
        PastePending = false;
    }

    private void UpdateTitle()
    {
        string where = Identity.Length > 0 && _host.Length > 0 ? $"{Identity}@{_host}" : Target;
        Title = Screen.Title.Length > 0
            ? $"{Screen.Title} — {where} - {Strings.Get("app.title")}"
            : $"{where} - {Strings.Get("app.title")}";
    }

    public override async ValueTask DisposeAsync()
    {
        _resizeTimer.Stop();
        await base.DisposeAsync();
    }
}
