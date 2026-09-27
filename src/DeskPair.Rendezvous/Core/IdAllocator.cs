using System.Security.Cryptography;

namespace DeskPair.Rendezvous.Core;

/// <summary>Allocates random fixed-length numeric ids that do not start with zero.</summary>
public sealed class IdAllocator
{
    private readonly int _digits;
    private readonly long _min;
    private readonly long _range;

    public IdAllocator(int digits)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(digits, 6);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(digits, 18);
        _digits = digits;
        _min = (long)Math.Pow(10, digits - 1);
        _range = _min * 9;
    }

    public string Next()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        long random = (long)(BitConverter.ToUInt64(bytes) % (ulong)_range);
        return (_min + random).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public bool IsValid(string id) => id.Length == _digits && id[0] != '0' && id.All(char.IsAsciiDigit);
}
