using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Google.Protobuf;
using DeskPair.Protocol.Messages;
using DeskPair.Protocol.Rendezvous;
using WireAddress = DeskPair.Protocol.Rendezvous.SocketAddress;

namespace DeskPair.Core.Transport.Udp;

/// <summary>Addresses a peer can try to reach our media socket at, ICE-style: local interfaces and the public mapping the rendezvous server saw.</summary>
public static class CandidateGatherer
{
    public const uint LocalPriority = 300;
    public const uint ReflexivePriority = 200;
    public const uint RelayPriority = 100;

    /// <summary>
    /// Local candidates for every usable interface plus, when a rendezvous server is known, the reflexive
    /// candidate learned by sending a NAT test from the media socket itself (so the mapping is the right one).
    /// </summary>
    public static async Task<List<MediaCandidate>> GatherAsync(IDatagramSocket socket, string? rendezvousServer, TimeSpan reflexiveTimeout, CancellationToken ct)
    {
        var list = new List<MediaCandidate>();
        int port = socket.LocalEndPoint.Port;
        foreach (IPAddress address in LocalAddresses())
        {
            list.Add(Candidate(new IPEndPoint(address, port), MediaCandidate.Types.Kind.Local, LocalPriority));
        }

        if (!string.IsNullOrEmpty(rendezvousServer))
        {
            IPEndPoint? reflexive = await DiscoverReflexiveAsync(socket, rendezvousServer, reflexiveTimeout, ct).ConfigureAwait(false);
            if (reflexive is not null && !list.Any(c => SameEndPoint(c, reflexive)))
            {
                list.Add(Candidate(reflexive, MediaCandidate.Types.Kind.Reflexive, ReflexivePriority));
            }
        }

        return list;
    }

    public static MediaCandidate Candidate(IPEndPoint ep, MediaCandidate.Types.Kind kind, uint priority) => new()
    {
        Addr = new WireAddress { Ip = ByteString.CopyFrom(ep.Address.GetAddressBytes()), Port = (uint)ep.Port },
        Kind = kind,
        Priority = priority,
    };

    public static IPEndPoint? ToEndPoint(MediaCandidate candidate)
    {
        WireAddress? a = candidate.Addr;
        if (a is null || a.Ip.Length is not (4 or 16) || a.Port is 0 or > 65535)
        {
            return null;
        }

        var ip = new IPAddress(a.Ip.Span);
        return new IPEndPoint(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip, (int)a.Port);
    }

    public static IEnumerable<IPAddress> LocalAddresses()
    {
        var seen = new HashSet<IPAddress>();
        NetworkInterface[] interfaces = SafeInterfaces();
        foreach (NetworkInterface nic in interfaces)
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            foreach (UnicastIPAddressInformation info in nic.GetIPProperties().UnicastAddresses)
            {
                IPAddress ip = info.Address;
                if (ip.AddressFamily == AddressFamily.InterNetworkV6 && (ip.IsIPv6LinkLocal || ip.IsIPv6Multicast))
                {
                    continue;
                }

                if (seen.Add(ip))
                {
                    yield return ip;
                }
            }
        }

        if (seen.Count == 0)
        {
            yield return IPAddress.Loopback;
        }
    }

    private static NetworkInterface[] SafeInterfaces()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>Sends a NAT test datagram to the rendezvous UDP port from the media socket and reads back the public mapping.</summary>
    private static async Task<IPEndPoint?> DiscoverReflexiveAsync(IDatagramSocket socket, string rendezvousServer, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            (string host, int port) = TcpConnector.ParseHostPort(rendezvousServer, Protocol.ProtocolConstants.RendezvousPort);
            IPAddress[] addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            IPAddress? target = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
            if (target is null)
            {
                return null;
            }

            var server = new IPEndPoint(target, port);
            byte[] request = new RendezvousMessage { TestNatRequest = new TestNatRequest { Serial = Environment.TickCount } }.ToByteArray();
            byte[] buffer = new byte[2048];
            var from = new System.Net.SocketAddress(AddressFamily.InterNetworkV6);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            for (int attempt = 0; attempt < 3 && !cts.IsCancellationRequested; attempt++)
            {
                socket.SendTo(request, server);
                try
                {
                    using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                    attemptCts.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(100, timeout.TotalMilliseconds / 3)));
                    int n = await socket.ReceiveFromAsync(buffer, from, attemptCts.Token).ConfigureAwait(false);
                    RendezvousMessage reply = RendezvousMessage.Parser.ParseFrom(buffer, 0, n);
                    if (reply.UnionCase == RendezvousMessage.UnionOneofCase.TestNatResponse && reply.TestNatResponse.ObservedAddr is { } observed)
                    {
                        var ip = new IPAddress(observed.Ip.Span);
                        return new IPEndPoint(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip, (int)observed.Port);
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                }
                catch (InvalidProtocolBufferException)
                {
                }
            }
        }
        catch (Exception)
        {
        }

        return null;
    }

    private static bool SameEndPoint(MediaCandidate c, IPEndPoint ep) => ToEndPoint(c) is { } e && e.Equals(ep);
}
