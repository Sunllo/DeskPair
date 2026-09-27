using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;

namespace DeskPair.Server.Shared;

/// <summary>
/// Which country an address was allocated to. Country, and nothing finer.
///
/// The data is the five regional registries' own delegation files (ARIN, RIPE, APNIC, LACNIC, AFRINIC),
/// compiled to a sorted range table by <c>tools/DeskPair.Tools.GeoTable</c> and embedded here. They
/// are public domain, need no account and no licence key, and their accuracy is exactly the question being
/// asked: who was this block delegated to. A city-level answer would mean MaxMind, an account, a key and a
/// file to keep updating, for a precision nothing here wants.
///
/// It is a delegation record, not a location. A block delegated to one country can be announced anywhere,
/// and a VPN answers for its exit. Good enough to choose a nearby relay and to draw a distribution; not
/// good enough to assert where a person is, and nothing here should read it as that.
///
/// Missing table, unknown address, or anything it does not cover all return null. Every caller has to have
/// an answer for null anyway, so there is no reason for this to throw.
/// </summary>
public static class GeoCountry
{
    private const string ResourceName = "geo-country.bin";

    private static readonly Lazy<Table?> Loaded = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The ISO 3166-1 alpha-2 code in lower case, or null when it is not known.</summary>
    public static string? Lookup(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        Table? table = Loaded.Value;
        if (table is null)
        {
            return null;
        }

        // IPv4-mapped IPv6 is what a dual-stack socket hands back for an IPv4 peer, and it is the common
        // case here rather than an oddity: the relay and the rendezvous both map their endpoints.
        IPAddress v4 = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        if (v4.AddressFamily != AddressFamily.InterNetwork)
        {
            return table.LookupV6(v4);
        }

        Span<byte> bytes = stackalloc byte[4];
        return v4.TryWriteBytes(bytes, out _) ? table.LookupV4(BinaryPrimitives.ReadUInt32BigEndian(bytes)) : null;
    }

    /// <summary>True when a table was found. For a start-up line that says whether regions will work.</summary>
    public static bool IsAvailable => Loaded.Value is not null;

    private static Table? Load()
    {
        Assembly assembly = typeof(GeoCountry).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            return null;
        }

        try
        {
            using var reader = new BinaryReader(stream);
            return Table.Read(reader);
        }
        catch (Exception e) when (e is EndOfStreamException or InvalidDataException)
        {
            // A truncated or mis-generated table is a reason to fall back to "unknown", not to stop a
            // server from starting over a feature that only chooses between relays.
            return null;
        }
    }

    /// <summary>
    /// Sorted ranges, binary searched.
    ///
    /// Two flat arrays rather than an interval tree: the ranges do not overlap and never change at run time,
    /// so the start of each range is enough, and a binary search over a contiguous array of uints is as fast
    /// as this needs to be. The format is written by the generator tool; see its README for the layout.
    /// </summary>
    private sealed class Table
    {
        private const uint Magic = 0x53474330; // "SGC0"

        private uint[] _v4Start = [];
        private uint[] _v4End = [];
        private ushort[] _v4Country = [];
        private UInt128[] _v6Start = [];
        private UInt128[] _v6End = [];
        private ushort[] _v6Country = [];
        private string[] _countries = [];

        public static Table Read(BinaryReader reader)
        {
            if (reader.ReadUInt32() != Magic)
            {
                throw new InvalidDataException("Not a country table.");
            }

            var table = new Table();
            int countries = reader.ReadInt32();
            table._countries = new string[countries];
            for (int i = 0; i < countries; i++)
            {
                table._countries[i] = reader.ReadString();
            }

            int v4 = reader.ReadInt32();
            table._v4Start = new uint[v4];
            table._v4End = new uint[v4];
            table._v4Country = new ushort[v4];
            for (int i = 0; i < v4; i++)
            {
                table._v4Start[i] = reader.ReadUInt32();
                table._v4End[i] = reader.ReadUInt32();
                table._v4Country[i] = reader.ReadUInt16();
            }

            int v6 = reader.ReadInt32();
            table._v6Start = new UInt128[v6];
            table._v6End = new UInt128[v6];
            table._v6Country = new ushort[v6];
            for (int i = 0; i < v6; i++)
            {
                table._v6Start[i] = new UInt128(reader.ReadUInt64(), reader.ReadUInt64());
                table._v6End[i] = new UInt128(reader.ReadUInt64(), reader.ReadUInt64());
                table._v6Country[i] = reader.ReadUInt16();
            }

            return table;
        }

        public string? LookupV4(uint address)
        {
            int i = Find(_v4Start, address);
            return i >= 0 && address <= _v4End[i] ? _countries[_v4Country[i]] : null;
        }

        public string? LookupV6(IPAddress address)
        {
            Span<byte> bytes = stackalloc byte[16];
            if (!address.TryWriteBytes(bytes, out int written) || written != 16)
            {
                return null;
            }

            var value = new UInt128(BinaryPrimitives.ReadUInt64BigEndian(bytes), BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]));
            int i = Find(_v6Start, value);
            return i >= 0 && value <= _v6End[i] ? _countries[_v6Country[i]] : null;
        }

        /// <summary>Index of the last range starting at or below the value, or -1.</summary>
        private static int Find<T>(T[] starts, T value)
            where T : IComparable<T>
        {
            int lo = 0;
            int hi = starts.Length - 1;
            int found = -1;
            while (lo <= hi)
            {
                int mid = lo + ((hi - lo) / 2);
                if (starts[mid].CompareTo(value) <= 0)
                {
                    found = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            return found;
        }
    }
}
