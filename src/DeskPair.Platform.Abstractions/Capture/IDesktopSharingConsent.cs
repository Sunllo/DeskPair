namespace DeskPair.Platform.Abstractions.Capture;

/// <summary>What is remembered about a user's permission to share their desktop without asking them each time.</summary>
public enum DesktopSharingState
{
    /// <summary>No desktop of this user's is being served, so there is nothing to allow.</summary>
    Unavailable,

    NotYet,

    /// <summary>Allowed without the keyboard and the pointer.</summary>
    WatchOnly,

    Allowed,
}

/// <summary>How asking a user ended.</summary>
public enum DesktopSharingOutcome
{
    Allowed,
    WatchOnly,
    Declined,
    Unanswered,
    Failed,
}

/// <summary>
/// A desktop an unattended engine shares only with its user's permission, given once: a Wayland desktop, shared through
/// the portal from a session the engine does not run in. The permission is asked for on that user's screen, at their
/// request, and remembered by the engine under their uid.
/// </summary>
public interface IDesktopSharingConsent
{
    Task<DesktopSharingState> SharingStateAsync(uint uid, CancellationToken ct);

    /// <summary>Asks <paramref name="uid"/> on their own screen and remembers the answer; as long as the person takes.</summary>
    Task<(DesktopSharingOutcome Outcome, string? Detail)> AskSharingAsync(uint uid, CancellationToken ct);
}
