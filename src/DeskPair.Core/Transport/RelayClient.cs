using System.Net.Sockets;
using DeskPair.Protocol;
using DeskPair.Protocol.Framing;
using DeskPair.Protocol.Rendezvous;

namespace DeskPair.Core.Transport;

/// <summary>Connects to a relay server and presents the pairing uuid; the returned transport carries the peer stream.</summary>
public static class RelayClient
{
    /// <summary>Presents the uuid and, when there is one, the rendezvous server's ticket for it; a relay that checks tickets refuses a request without one.</summary>
    public static async Task<IPeerTransport> ConnectAsync(string relayServer, string uuid, string myId, ConnType connType, TimeSpan timeout, CancellationToken ct, RelayTicket? ticket = null)
    {
        (string host, int port) = TcpConnector.ParseHostPort(relayServer, ProtocolConstants.RelayPort);
        Socket socket = await TcpConnector.ConnectAsync(host, port, timeout, ct).ConfigureAwait(false);
        try
        {
            var request = new RendezvousMessage
            {
                RequestRelay = new RequestRelay { Uuid = uuid, Id = myId, ConnType = connType, Ticket = ticket },
            };
            // Write the single pairing frame straight to the socket; the FramedStream for the session is
            // created by the caller over the same socket once the relay has spliced the two peers.
            byte[] body = Google.Protobuf.MessageExtensions.ToByteArray(request);
            byte[] frame = new byte[FramedStream.HeaderBytes + body.Length];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)body.Length);
            body.CopyTo(frame, FramedStream.HeaderBytes);
            int sent = 0;
            while (sent < frame.Length)
            {
                sent += await socket.SendAsync(frame.AsMemory(sent), SocketFlags.None, ct).ConfigureAwait(false);
            }

            return new TcpPeerTransport(socket, TransportKind.Relay);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
