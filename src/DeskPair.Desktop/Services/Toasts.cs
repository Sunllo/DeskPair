using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DeskPair.Desktop.Services;

/// <summary>
/// The one place a passing message is shown, and the reason it goes away again.
///
/// Every screen used to keep its own line of text: "Saved", "Copied", "The new language applies to windows
/// opened from now on". Each was set when something happened and cleared when the next thing happened --
/// which, for a page somebody sets once and leaves, is never. A confirmation of something done a quarter
/// of an hour ago reads as a description of the page's current state, and the language notice sat under
/// the recording settings looking like a warning about them.
///
/// So a message is shown for ten seconds and then it is gone. Ten because a sentence in a second language
/// takes longer to read than one in your own, and because these are confirmations rather than questions --
/// nothing is lost by missing one.
///
/// A second message replaces the first rather than queueing behind it. Somebody who has just changed two
/// settings wants to know the second one saved; being shown the first for another ten seconds would say
/// the wrong thing about the present moment.
/// </summary>
public sealed partial class Toasts : ObservableObject
{
    /// <summary>Long enough to read a sentence in a language that is not your first.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(10);

    private readonly TimeProvider _time;
    private readonly Action<Action> _post;
    private ITimer? _expiry;

    /// <param name="time">The clock, so the expiry can be tested without waiting ten seconds.</param>
    /// <param name="post">
    /// Runs the expiry on the thread that may touch the interface. A timer fires on a pool thread, and a
    /// binding updated from one is a crash somewhere else entirely.
    /// </param>
    public Toasts(TimeProvider? time = null, Action<Action>? post = null)
    {
        _time = time ?? TimeProvider.System;
        _post = post ?? (work => Avalonia.Threading.Dispatcher.UIThread.Post(work));
    }

    /// <summary>The one every screen shares.</summary>
    public static Toasts Current { get; } = new();

    [ObservableProperty]
    public partial string Message { get; private set; } = string.Empty;

    /// <summary>Whether this is something that went wrong, which is shown differently.</summary>
    [ObservableProperty]
    public partial bool IsProblem { get; private set; }

    [ObservableProperty]
    public partial bool IsVisible { get; private set; }

    /// <summary>
    /// Shows a message for <see cref="Lifetime"/>. An empty one hides whatever is there, which is what a
    /// screen clearing its own notice means.
    /// </summary>
    public void Show(string? message, bool problem = false)
    {
        _expiry?.Dispose();
        _expiry = null;

        if (string.IsNullOrWhiteSpace(message))
        {
            Hide();
            return;
        }

        Message = message;
        IsProblem = problem;
        IsVisible = true;
        _expiry = _time.CreateTimer(_ => _post(Hide), null, Lifetime, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Takes it away now, for the button somebody presses when they have read it.</summary>
    [RelayCommand]
    public void Hide()
    {
        _expiry?.Dispose();
        _expiry = null;
        IsVisible = false;
        Message = string.Empty;
        IsProblem = false;
    }
}
