using System.Net;
using System.Net.Sockets;
using DeskPair.Core.Config;
using DeskPair.Core.Transport.Nat;
using DeskPair.Protocol;
using DeskPair.Protocol.Framing;
using DeskPair.Protocol.Rendezvous;

namespace DeskPair.Core.Transport;

/// <summary>Whether a saved device can be reached right now.</summary>
public enum PeerOnlineState
{
    Unknown,
    Online,
    Offline,
}

/// <summary>
/// Answers "is this device reachable" for a list of saved devices. Peer ids are asked of the rendezvous server in
/// one batch (it keeps a heartbeat per peer); direct addresses are probed by opening a socket to the host's
/// direct-access port, which is the same thing a connection would do and needs no server at all.
/// </summary>
public sealed class PeerPresence(PeerSettings settings, TimeProvider? time = null)
{
    /// <summary>The rendezvous server answers at most this many ids per query.</summary>
    public const int MaxBatch = 100;

    public static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(6);
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(1500);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>
    /// Looks up every target: ids go to the rendezvous server, anything that parses as an address is probed.
    /// Targets that cannot be judged (no server configured, query failed) come back as <see cref="PeerOnlineState.Unknown"/>.
    /// </summary>
    public async Task<Dictionary<string, PeerOnlineState>> QueryAsync(IReadOnlyList<string> targets, CancellationToken ct = default)
    {
        var result = new Dictionary<string, PeerOnlineState>(StringComparer.OrdinalIgnoreCase);
        List<string> ids = [];
        List<string> addresses = [];
        foreach (string target in targets.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            result[target] = PeerOnlineState.Unknown;
            (PeerConnector.IsDirectTarget(target) ? addresses : ids).Add(target);
        }

        Task<Dictionary<string, PeerOnlineState>> serverTask = QueryServerAsync(ids, ct);
        Task<Dictionary<string, PeerOnlineState>>[] probes = addresses.Select(a => ProbeOneAsync(a, ct)).ToArray();
        foreach (Dictionary<string, PeerOnlineState> part in await Task.WhenAll(probes.Append(serverTask)).ConfigureAwait(false))
        {
            foreach ((string target, PeerOnlineState state) in part)
            {
                result[target] = state;
            }
        }

        return result;
    }

    /// <summary>One batch of ids over the rendezvous TCP port; the answer is positional, so order matters.</summary>
    private async Task<Dictionary<string, PeerOnlineState>> QueryServerAsync(List<string> ids, CancellationToken ct)
    {
        var result = new Dictionary<string, PeerOnlineState>(StringComparer.OrdinalIgnoreCase);
        if (ids.Count == 0 || settings.RendezvousServer.Length == 0)
        {
            return result;
        }

        foreach (string[] batch in ids.Chunk(MaxBatch))
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(QueryTimeout);
                (string host, int port) = TcpConnector.ParseHostPort(settings.RendezvousServer, ProtocolConstants.RendezvousPort);
                IPAddress address = IPAddress.TryParse(host, out IPAddress? literal)
                    ? literal
                    : (await Dns.GetHostAddressesAsync(host, timeout.Token).ConfigureAwait(false)).FirstOrDefault()
                      ?? throw new SocketException((int)SocketError.HostNotFound);

                using Socket socket = await SocketFactory.ConnectReusableAsync(0, new IPEndPoint(address, port), timeout.Token).ConfigureAwait(false);
                await using var stream = new FramedStream(new NetworkStream(socket, ownsSocket: false), FramedStreamOptions.Control);
                var query = new QueryOnline();
                query.Ids.AddRange(batch);
                await stream.SendAsync(new RendezvousMessage { QueryOnline = query }, timeout.Token).ConfigureAwait(false);

                using Frame? frame = await stream.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                if (frame is null)
                {
                    continue;
                }

                var reply = RendezvousMessage.Parser.ParseFrom(frame.Payload.Span);
                if (reply.UnionCase != RendezvousMessage.UnionOneofCase.QueryOnlineResponse)
                {
                    continue;
                }

                for (int i = 0; i < batch.Length && i < reply.QueryOnlineResponse.Online.Count; i++)
                {
                    result[batch[i]] = reply.QueryOnlineResponse.Online[i] ? PeerOnlineState.Online : PeerOnlineState.Offline;
                }
            }
            catch (Exception e) when (e is SocketException or IOException or OperationCanceledException or InvalidOperationException)
            {
                // The server is unreachable; the devices stay "unknown" rather than being shown as offline.
            }
        }

        return result;
    }

    /// <summary>A short connect to the host's direct-access port: reachable means the desk is up and listening.</summary>
    private async Task<Dictionary<string, PeerOnlineState>> ProbeOneAsync(string target, CancellationToken ct)
    {
        var result = new Dictionary<string, PeerOnlineState>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ProbeTimeout);
            (string host, int port) = TcpConnector.ParseHostPort(target, ProtocolConstants.DirectAccessPort);
            using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            result[target] = PeerOnlineState.Online;
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException or ArgumentException)
        {
            result[target] = PeerOnlineState.Offline;
        }

        return result;
    }
}
