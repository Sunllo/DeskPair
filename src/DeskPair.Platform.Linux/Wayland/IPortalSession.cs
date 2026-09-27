using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace DeskPair.Platform.Linux.Wayland;

/// <summary>
/// A portal session as the capture, the input and the host use it: the monitors it shares, a PipeWire connection
/// for each stream, the input the person allowed, and the moment it ends.
///
/// <see cref="PortalSession"/> is one in this process, on this process's session bus. The unattended engine runs as
/// its own account, which has no session bus of the signed-in user's to call the portal on, so it is served one by a
/// small agent in that user's session instead: the same streams, and PipeWire connections the portal opened for the
/// agent, handed across as descriptors.
/// </summary>
internal interface IPortalSession : IPortalInput, IAsyncDisposable
{
    /// <summary>The shared monitors, in the portal's order.</summary>
    IReadOnlyList<PortalStream> Streams { get; }

    /// <summary>The input devices granted: <c>1</c> keyboard, <c>2</c> pointer.</summary>
    uint Devices { get; }

    /// <summary>Whether this is a remote desktop session (input possible) rather than one that can only watch.</summary>
    bool RemoteControl { get; }

    /// <summary>Whether the portal agreed to carry the clipboard.</summary>
    bool ClipboardEnabled { get; }

    /// <summary>How long starting took, which says whether somebody was asked.</summary>
    TimeSpan StartTook { get; }

    /// <summary>Completes when the portal ends the session without being asked to.</summary>
    Task Closed { get; }

    /// <summary>A new PipeWire connection that can see the shared monitors' nodes and nothing else. The caller owns it.</summary>
    Task<SafeFileHandle> OpenPipeWireRemoteAsync(CancellationToken ct = default);
}

/// <summary>Starts a portal session, remembering the permission in <paramref name="tokens"/>.</summary>
/// <exception cref="PortalException">No portal, the person said no, nobody answered, or the portal failed.</exception>
internal delegate Task<IPortalSession> PortalOpener(PortalTokens tokens, ILogger log, CancellationToken ct);
