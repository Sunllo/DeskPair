using System.Security.Cryptography;

namespace DeskPair.Platform.Linux.Clipboard;

/// <summary>
/// What this side is currently offering on the CLIPBOARD selection, as one payload per target atom.
///
/// X11 has no clipboard store: an owner advertises the targets it can convert to and answers a request for
/// each one. Holding a single byte array and one hard-coded pair of atoms worked while text was the only
/// thing on offer; with files there are several targets, each with its own bytes and its own conventions,
/// and TARGETS has to name whatever is actually there.
///
/// Instances are immutable and swapped in whole, so the event-loop thread never sees half a clipboard.
/// </summary>
internal sealed class OwnedSelection
{
    public static readonly OwnedSelection Empty = new([], 0, []);

    private readonly Dictionary<nint, byte[]> _byTarget;

    private OwnedSelection(Dictionary<nint, byte[]> byTarget, nuint takenAt, byte[] fingerprint)
    {
        _byTarget = byTarget;
        TakenAt = takenAt;
        Fingerprint = fingerprint;
    }

    /// <summary>The server time ownership was taken at, which is what a TIMESTAMP request must be answered with.</summary>
    public nuint TakenAt { get; }

    /// <summary>
    /// Identifies this content so our own write is not read back as someone else's change. A hash rather than
    /// the text itself: a file listing is not text, and a large image should not be kept twice.
    /// </summary>
    public byte[] Fingerprint { get; }

    public bool IsEmpty => _byTarget.Count == 0;

    public IReadOnlyCollection<nint> Targets => _byTarget.Keys;

    public byte[]? this[nint target] => _byTarget.GetValueOrDefault(target);

    public static OwnedSelection Of(IEnumerable<(nint Target, byte[] Bytes)> payloads, nuint takenAt)
    {
        var map = new Dictionary<nint, byte[]>();
        foreach ((nint target, byte[] bytes) in payloads)
        {
            map[target] = bytes;
        }

        return map.Count == 0 ? Empty : new OwnedSelection(map, takenAt, Hash(map));
    }

    /// <summary>Covers every target, so changing any one of them counts as a different clipboard.</summary>
    private static byte[] Hash(Dictionary<nint, byte[]> map)
    {
        using var sha = SHA256.Create();
        foreach ((nint target, byte[] bytes) in map.OrderBy(p => (long)p.Key))
        {
            byte[] header = BitConverter.GetBytes((long)target);
            sha.TransformBlock(header, 0, header.Length, null, 0);
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }

        sha.TransformFinalBlock([], 0, 0);
        return sha.Hash ?? [];
    }

    public static byte[] Hash(ReadOnlySpan<byte> bytes) => SHA256.HashData(bytes);
}
