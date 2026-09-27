using System.Net;
using DeskPair.Protocol.Rendezvous;

namespace DeskPair.Core.Session.Host;

public sealed record ConnectionSummary(int ConnectionId, string PeerId, string PeerName, string PeerPlatform, EndPoint? RemoteEndPoint, ConnType ConnType);

public enum HostEventKind
{
    Opened,

    /// <summary>The viewer said who it is (a valid login arrived); authorization has not happened yet.</summary>
    Identified,
    Authorized,
    Closed,
    ChatReceived,
    PermissionChanged,
}

public sealed record HostSessionEvent(int ConnectionId, HostEventKind Kind, string? Text = null);

/// <summary>
/// Bridge to whoever can ask the local user (the connection manager window). Implementations must
/// reject when no user-facing process is available; the core never auto-accepts.
/// </summary>
public interface IConnectionApprover
{
    Task<bool> RequestAsync(ConnectionSummary summary, CancellationToken ct);

    void Notify(HostSessionEvent evt);
}

public sealed class RejectingApprover : IConnectionApprover
{
    public static RejectingApprover Instance { get; } = new();

    public Task<bool> RequestAsync(ConnectionSummary summary, CancellationToken ct) => Task.FromResult(false);

    public void Notify(HostSessionEvent evt)
    {
    }
}
