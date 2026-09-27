using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DeskPair.Desktop.ViewModels;

public sealed record ChatLine(string Text, bool FromPeer, DateTimeOffset Time)
{
    public string Prefix => FromPeer ? "‹" : "›";

    public string TimeText => Time.ToLocalTime().ToString("HH:mm");
}

/// <summary>Chat transcript shared by the session window and the connection manager.</summary>
public partial class ChatViewModel : ObservableObject
{
    private readonly Func<string, Task> _send;

    public ChatViewModel(Func<string, Task> send)
    {
        _send = send;
        Draft = string.Empty;
    }

    public ObservableCollection<ChatLine> Lines { get; } = [];

    [ObservableProperty]
    public partial string Draft { get; set; }

    [ObservableProperty]
    public partial int Unread { get; set; }

    public void Received(string text)
    {
        Lines.Add(new ChatLine(text, true, DateTimeOffset.UtcNow));
        Unread++;
    }

    public void MarkRead() => Unread = 0;

    [RelayCommand]
    private async Task SendAsync()
    {
        string text = Draft.Trim();
        if (text.Length == 0)
        {
            return;
        }

        Draft = string.Empty;
        Lines.Add(new ChatLine(text, false, DateTimeOffset.UtcNow));
        await _send(text);
    }
}
