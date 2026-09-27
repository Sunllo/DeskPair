namespace DeskPair.Platform.Linux.Wayland;

/// <summary>Why a portal session could not be had, in the terms a viewer can be told.</summary>
public enum PortalFailure
{
    /// <summary>No session bus, no xdg-desktop-portal, or no backend that implements screen sharing.</summary>
    Unavailable,

    /// <summary>The person at the machine said no.</summary>
    Refused,

    /// <summary>The dialog was shown and nobody answered it in time.</summary>
    TimedOut,

    /// <summary>The portal ended the request some other way, or answered something that made no sense.</summary>
    Failed,
}

/// <summary>A portal session that did not start, with the reason a viewer should be told.</summary>
public sealed class PortalException(PortalFailure reason, string message, Exception? inner = null) : Exception(message, inner)
{
    public PortalFailure Reason { get; } = reason;
}
