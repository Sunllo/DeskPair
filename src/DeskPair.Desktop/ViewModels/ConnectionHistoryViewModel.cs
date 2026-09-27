using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskPair.Desktop.Localization;
using DeskPair.Desktop.Services;
using DeskPair.Protocol.Ipc;

namespace DeskPair.Desktop.ViewModels;

/// <summary>
/// One connection, as the page shows it. Built by a pure function so what it says can be tested without a
/// window, an engine or a clock.
/// </summary>
public sealed record ConnectionHistoryRow(string Who, string When, string Detail, string Allowed, string Status, bool IsOpen)
{
    /// <summary>
    /// Formats one entry. <paramref name="toLocal"/> is injected so a test can pin the time zone; the page
    /// passes the machine's own.
    /// </summary>
    public static ConnectionHistoryRow From(ConnectionHistoryEntry entry, Func<DateTimeOffset, DateTimeOffset>? toLocal = null)
    {
        toLocal ??= t => t.ToLocalTime();
        DateTimeOffset started = toLocal(DateTimeOffset.FromUnixTimeMilliseconds(entry.StartedUtcMs));

        if (entry.Kind == "cleared")
        {
            return new ConnectionHistoryRow(Strings.Get("history.cleared"), started.ToString("g"), string.Empty, string.Empty, string.Empty, false);
        }

        string who = entry.PeerName.Length > 0 ? entry.PeerName : entry.PeerId;
        if (who.Length == 0)
        {
            who = Strings.Get("history.unknownPeer");
        }

        var detail = new List<string>();
        if (entry.EndedUtcMs > 0)
        {
            detail.Add(Duration(DateTimeOffset.FromUnixTimeMilliseconds(entry.EndedUtcMs) - DateTimeOffset.FromUnixTimeMilliseconds(entry.StartedUtcMs)));
        }

        if (entry.Address.Length > 0)
        {
            detail.Add(entry.AddressReported ? Strings.Format("history.relayedFrom", entry.Address) : entry.Address);
        }

        detail.Add(Strings.Get(entry.Authenticated switch
        {
            "temporary" => "history.auth.temporary",
            "permanent" => "history.auth.permanent",
            "approval" => "history.auth.approval",
            _ => "history.auth.none",
        }));

        if (entry.Kind == "file-transfer")
        {
            detail.Add(Strings.Get("settings.files"));
        }
        else if (entry.Kind == "terminal")
        {
            // A shell is the one thing this page must never be vague about: how many, and as whom.
            detail.Add(entry.TerminalOpens > 0
                ? Strings.Format("history.terminal", entry.TerminalOpens, entry.TerminalIdentity.Length > 0 ? entry.TerminalIdentity : "?")
                : Strings.Get("history.terminalNone"));
        }

        string allowed = entry.Granted.Count == 0
            ? string.Empty
            : Strings.Format("history.allowed", string.Join(", ", entry.Granted.Select(Permission)));

        // "Still open" and "ended without a record" look the same on the wire -- no end time -- but mean
        // opposite things, and only the engine knows which. It says so by whether the session is still live,
        // which the page cannot see, so the honest word covers both: nothing recorded the end.
        string status = entry.EndedUtcMs > 0 ? string.Empty : Strings.Get("history.noEnd");
        return new ConnectionHistoryRow(who, started.ToString("g"), string.Join(" · ", detail), allowed, status, entry.EndedUtcMs == 0);
    }

    private static string Duration(TimeSpan span) => span switch
    {
        { TotalSeconds: < 60 } => Strings.Format("history.seconds", (int)Math.Max(1, span.TotalSeconds)),
        { TotalMinutes: < 60 } => Strings.Format("history.minutes", (int)span.TotalMinutes),
        _ => Strings.Format("history.hours", (int)span.TotalHours, span.Minutes),
    };

    private static string Permission(string wire) => wire switch
    {
        "PermKeyboard" => Strings.Get("settings.keyboard"),
        "PermClipboard" => Strings.Get("settings.clipboard"),
        "PermAudio" => Strings.Get("settings.audio"),
        "PermFile" => Strings.Get("settings.files"),
        "PermRestart" => Strings.Get("settings.allowRestart"),
        "PermTerminal" => Strings.Get("settings.allowTerminal"),
        _ => wire,
    };
}

/// <summary>
/// Who has connected to this computer. Read from the engine, which is the only process that sees a
/// connection at all -- under the service the engine is not even the same account as this window.
/// </summary>
public partial class ConnectionHistoryViewModel : ObservableObject
{
    private readonly HostLink _host;

    public ConnectionHistoryViewModel(HostLink host)
    {
        _host = host;
        _uploads = App.Config.UploadConnectionHistory;
    }

    /// <summary>
    /// Send the record to the linked account as well. Written straight through rather than through the
    /// settings page's debounce: it is one switch, on the page it is about, and the person who just turned
    /// it off means now.
    /// </summary>
    public bool Uploads
    {
        get => _uploads;
        set
        {
            if (SetProperty(ref _uploads, value))
            {
                App.Config = App.Config with { UploadConnectionHistory = value };
                try
                {
                    App.Config.Save();
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Problem = Strings.Get("history.uploadNotSaved");
                }
            }
        }
    }

    private bool _uploads;

    public ObservableCollection<ConnectionHistoryRow> Rows { get; } = [];

    [ObservableProperty]
    public partial bool IsEmpty { get; set; } = true;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>Set when the engine could not be asked, so the page says why rather than looking empty.</summary>
    [ObservableProperty]
    public partial string Problem { get; set; } = string.Empty;

    /// <summary>Reads the record. Called when the page is opened and by the refresh button.</summary>
    [RelayCommand]
    public async Task RefreshAsync() => await LoadAsync(clear: false);

    /// <summary>Forgets everything. The clearing itself stays in the record, which is the point of it.</summary>
    [RelayCommand]
    private async Task ClearAsync() => await LoadAsync(clear: true);

    private async Task LoadAsync(bool clear)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        Problem = string.Empty;
        try
        {
            IpcMessage reply = await _host.RequestAsync(new IpcMessage
            {
                ConnectionHistoryRequest = new ConnectionHistoryRequest { Limit = 500, Clear = clear },
            });

            Rows.Clear();
            foreach (ConnectionHistoryEntry entry in reply.ConnectionHistory?.Entries ?? [])
            {
                Rows.Add(ConnectionHistoryRow.From(entry));
            }

            IsEmpty = Rows.Count == 0;
        }
        catch (Exception e) when (e is IOException or TimeoutException or InvalidOperationException or ObjectDisposedException)
        {
            Problem = Strings.Get("history.unavailable");
            IsEmpty = Rows.Count == 0;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
