namespace DeskPair.Platform.Abstractions.Capture;

/// <summary>
/// Displays that exist only while somebody is watching.
///
/// A Wayland desktop is shared through a portal session: the compositor asks the person at the machine (once,
/// if they let it remember), and shows a "being shared" indicator for as long as the session is open. So the
/// displays are opened when the first viewer arrives and closed when the last one leaves, rather than held open
/// by an engine that nobody is connected to. Until then the platform's enumerator lists nothing.
/// </summary>
public interface IDisplaySession
{
    /// <summary>Whether the displays are open now.</summary>
    bool IsOpen { get; }

    /// <summary>
    /// Opens the displays if they are not open, and waits for them. <paramref name="progress"/> hears a sentence
    /// for the viewers when the wait is a person's (a consent dialog on the machine's own screen). Several callers
    /// share one attempt.
    /// </summary>
    /// <returns>Null once open; otherwise why not, in words a viewer can be shown.</returns>
    Task<string?> OpenAsync(Action<string> progress, CancellationToken ct);

    /// <summary>Closes the displays; the last viewer has gone.</summary>
    ValueTask CloseAsync();

    /// <summary>
    /// The displays closed without being asked to -- the person at the machine stopped sharing, the compositor
    /// went away -- with the reason in words a viewer can be shown.
    /// </summary>
    event Action<string>? Closed;

    /// <summary>
    /// What closed the displays by themselves is over -- the screen they were on was locked, and is unlocked again --
    /// so they can be opened for the viewers still connected, without anyone being asked.
    /// </summary>
    event Action? Reopenable;
}
