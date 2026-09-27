using System.Net;
using System.Net.Sockets;

namespace DeskPair.Core.Session.Host.Auth;

/// <summary>
/// Who may connect at all, by address or by id. Pure and allocation-light: it is consulted on every inbound
/// connection before anything else happens, including the handshake.
///
/// An entry is one of:
/// <list type="bullet">
/// <item><c>203.0.113.5</c> or <c>2001:db8::1</c> — one address</item>
/// <item><c>192.168.1.0/24</c> or <c>2001:db8::/32</c> — a range</item>
/// <item><c>id:123456789</c> — one peer, whatever address it arrives from</item>
/// </list>
///
/// An address is checked twice, because the two facts arrive at different moments. The address is known as
/// soon as the socket is accepted; the peer's id only after it has said who it is. So a list that names only
/// addresses refuses a stranger before a single byte is exchanged, and a list that also names ids has to let
/// the connection get as far as the login before it can tell. <see cref="MayAccept"/> is the first screen and
/// <see cref="Allows"/> is the answer.
/// </summary>
public sealed class PeerAllowlist
{
    private readonly List<(IPAddress Address, int Bits)> _ranges = [];
    private readonly HashSet<string> _ids = new(StringComparer.OrdinalIgnoreCase);

    private PeerAllowlist(bool enabled)
    {
        Enabled = enabled;
    }

    /// <summary>No restriction: every peer is allowed.</summary>
    public static PeerAllowlist Off { get; } = new(false);

    public bool Enabled { get; }

    /// <summary>Turned on with nothing in it, which is a deliberate "only me" and refuses everybody.</summary>
    public bool IsEmpty => _ranges.Count == 0 && _ids.Count == 0;

    /// <summary>How many entries were understood; the caller logs it so a typo is visible at startup.</summary>
    public int Count => _ranges.Count + _ids.Count;

    /// <summary>
    /// Builds a list from configuration. Entries that cannot be read are dropped and named in
    /// <paramref name="rejected"/> rather than failing the whole list -- a single typo must not quietly
    /// turn the allowlist into "refuse everybody", and must not be silent either.
    /// </summary>
    public static PeerAllowlist Create(bool enabled, IEnumerable<string> entries, out IReadOnlyList<string> rejected)
    {
        var bad = new List<string>();
        var list = new PeerAllowlist(enabled);
        foreach (string raw in entries ?? [])
        {
            string entry = raw.Trim();
            if (entry.Length == 0)
            {
                continue;
            }

            if (entry.StartsWith("id:", StringComparison.OrdinalIgnoreCase))
            {
                string id = entry[3..].Trim();
                if (id.Length == 0)
                {
                    bad.Add(entry);
                    continue;
                }

                list._ids.Add(id);
                continue;
            }

            if (TryParseRange(entry, out IPAddress? address, out int bits))
            {
                list._ranges.Add((address!, bits));
                continue;
            }

            bad.Add(entry);
        }

        rejected = bad;
        return list;
    }

    /// <summary>Whether one entry would be understood; for the settings page to say so as it is typed.</summary>
    public static bool IsValidEntry(string entry)
    {
        entry = entry.Trim();
        if (entry.StartsWith("id:", StringComparison.OrdinalIgnoreCase))
        {
            return entry[3..].Trim().Length > 0;
        }

        return TryParseRange(entry, out _, out _);
    }

    /// <summary>
    /// The screen applied the moment a connection is accepted, when only the address is known. False means
    /// the peer can be dropped without a handshake; true means "not refused yet", which for a list holding
    /// ids is as much as can be said until the peer has identified itself.
    /// </summary>
    public bool MayAccept(IPAddress? address)
    {
        if (!Enabled)
        {
            return true;
        }

        if (IsEmpty)
        {
            return false;
        }

        // Ids are only decidable at login, so a list that has any cannot refuse on the address alone.
        return _ids.Count > 0 || Matches(address);
    }

    /// <summary>The answer, once the peer has said who it is.</summary>
    public bool Allows(IPAddress? address, string? peerId)
    {
        if (!Enabled)
        {
            return true;
        }

        if (IsEmpty)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(peerId) && _ids.Contains(peerId))
        {
            return true;
        }

        return Matches(address);
    }

    private bool Matches(IPAddress? address)
    {
        if (address is null || _ranges.Count == 0)
        {
            return false;
        }

        IPAddress candidate = Normalise(address);
        foreach ((IPAddress network, int bits) in _ranges)
        {
            if (network.AddressFamily == candidate.AddressFamily && InRange(candidate, network, bits))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// IPv4-mapped IPv6 becomes plain IPv4, so an entry of <c>192.168.1.0/24</c> matches a peer that arrived
    /// on the dual-mode listener as <c>::ffff:192.168.1.7</c>. The same unwrapping
    /// <see cref="LoginFailureTracker.BucketOf"/> does, for the same reason: one machine, one answer.
    /// </summary>
    private static IPAddress Normalise(IPAddress address)
    {
        IPAddress plain = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        // A link-local address arrives as fe80::1%eth0, and the zone is about this machine's interfaces, not
        // about who the peer is. ScopeId throws on IPv4, so the family is asked first.
        return plain.AddressFamily == AddressFamily.InterNetworkV6 && plain.ScopeId != 0
            ? new IPAddress(plain.GetAddressBytes())
            : plain;
    }

    private static bool InRange(IPAddress candidate, IPAddress network, int bits)
    {
        Span<byte> a = stackalloc byte[16];
        Span<byte> b = stackalloc byte[16];
        if (!candidate.TryWriteBytes(a, out int lengthA) || !network.TryWriteBytes(b, out int lengthB) || lengthA != lengthB)
        {
            return false;
        }

        int whole = bits / 8;
        for (int i = 0; i < whole; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }

        int remainder = bits % 8;
        if (remainder == 0)
        {
            return true;
        }

        int mask = 0xFF << (8 - remainder);
        return (a[whole] & mask) == (b[whole] & mask);
    }

    private static bool TryParseRange(string entry, out IPAddress? address, out int bits)
    {
        address = null;
        bits = 0;

        int slash = entry.IndexOf('/');
        string host = slash < 0 ? entry : entry[..slash];
        if (!IPAddress.TryParse(host, out IPAddress? parsed))
        {
            return false;
        }

        parsed = Normalise(parsed);
        int width = parsed.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
        if (slash < 0)
        {
            address = parsed;
            bits = width;
            return true;
        }

        if (!int.TryParse(entry[(slash + 1)..], out int prefix) || prefix < 0 || prefix > width)
        {
            return false;
        }

        address = parsed;
        bits = prefix;
        return true;
    }
}
