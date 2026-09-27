using System.Net;
using DeskPair.Protocol.Rendezvous;

namespace DeskPair.Rendezvous.Core;

/// <summary>Immutable snapshot of one registered peer; the peer table replaces entries atomically.</summary>
public sealed record PeerEntry(
    string Id,
    byte[] Uuid,
    byte[] IdentityPk,
    DateTimeOffset CreatedUtc,
    DateTimeOffset LastSeenUtc,
    IPEndPoint? UdpEndPoint,
    string Version,
    SignedPeerIdentity? SignedIdentity,
    DateTimeOffset SignedAtUtc)
{
    public bool IsOnline(DateTimeOffset now, TimeSpan offlineAfter) =>
        UdpEndPoint is not null && now - LastSeenUtc <= offlineAfter;
}
