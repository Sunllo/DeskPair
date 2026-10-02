namespace DeskPair.Platform.Abstractions.Capture;

/// <summary>
/// Raises a helper that lets an app-mode engine see and drive the Windows secure desktop (a UAC prompt) for the
/// length of a session, the way RustDesk's portable service does -- the person at the host still completes the
/// real UAC themselves; nothing here clicks it for them. Present only where elevation is both possible and
/// needed: a Windows host running as the signed-in user. The unattended SYSTEM engine reads the secure desktop
/// itself and is given no elevator, so a host without one simply never advertises the capability.
/// </summary>
public interface ISessionElevator
{
    /// <summary>
    /// Raise the helper. This shows the real UAC on the host and returns true only once a helper is actually
    /// driving the secure desktop; false if the person there cancels the UAC, it never appears, or it times out.
    /// When <paramref name="permanent"/> is set the same elevated step also installs the unattended SYSTEM service
    /// and adds <paramref name="peerId"/> to the devices allowed to see the secure desktop, so that device keeps
    /// unattended access after this connection ends -- a best effort that never fails the elevation itself.
    /// </summary>
    Task<bool> ElevateAsync(bool permanent, string? peerId, CancellationToken ct);

    /// <summary>Stand the helper down. Safe to call when nothing is raised.</summary>
    Task LowerAsync();

    /// <summary>
    /// The helper went away on its own -- it crashed, or the pipe broke -- rather than by <see cref="LowerAsync"/>.
    /// The engine reverts to reading the ordinary desktop and tells the viewers the session is no longer elevated.
    /// </summary>
    event Action? Ended;
}
