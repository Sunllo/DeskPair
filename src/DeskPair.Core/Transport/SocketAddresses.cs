using System.Net;
using System.Net.Sockets;
using Google.Protobuf;
using WireAddress = DeskPair.Protocol.Rendezvous.SocketAddress;

namespace DeskPair.Core.Transport;

/// <summary>Conversions between wire <see cref="WireAddress"/> and <see cref="IPEndPoint"/>.</summary>
public static class SocketAddresses
{
    public static IPEndPoint? ToEndPoint(WireAddress? address)
    {
        if (address is null || address.Ip.Length is not (4 or 16) || address.Port is 0 or > 65535)
        {
            return null;
        }

        var ip = new IPAddress(address.Ip.Span);
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        return new IPEndPoint(ip, (int)address.Port);
    }

    public static WireAddress FromEndPoint(IPEndPoint ep)
    {
        IPAddress ip = ep.Address.IsIPv4MappedToIPv6 ? ep.Address.MapToIPv4() : ep.Address;
        return new WireAddress { Ip = ByteString.CopyFrom(ip.GetAddressBytes()), Port = (uint)ep.Port };
    }

    /// <summary>Stable key for pending-punch lookups: IPv4-mapped and plain addresses compare equal.</summary>
    public static string Key(IPEndPoint ep)
    {
        IPAddress ip = ep.Address.IsIPv4MappedToIPv6 ? ep.Address.MapToIPv4() : ep.Address;
        return $"{ip}:{ep.Port}";
    }

    public static bool SameAddress(IPAddress a, IPAddress b)
    {
        static IPAddress Plain(IPAddress x) => x.IsIPv4MappedToIPv6 ? x.MapToIPv4() : x;
        return Plain(a).Equals(Plain(b));
    }

    public static IPAddress PlainAddress(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;

    public static bool IsIPv4(IPAddress ip) => ip.AddressFamily == AddressFamily.InterNetwork || ip.IsIPv4MappedToIPv6;
}
