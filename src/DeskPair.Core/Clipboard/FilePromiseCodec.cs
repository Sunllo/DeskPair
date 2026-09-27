using Google.Protobuf;
using DeskPair.Core.FileTransfer;
using DeskPair.Platform.Abstractions.Clipboard;
using DeskPair.Protocol.Messages;

namespace DeskPair.Core.Clipboard;

/// <summary>
/// Turns a file promise into the payload of a <see cref="ClipboardItemFormat.FileList"/> item and back.
///
/// Decoding is the trust boundary: every field here was chosen by the other machine. A relative path arrives
/// as a string and leaves as something that has been through <see cref="PathGuard"/>, or the entry is dropped
/// and the caller is told how many were. Windows caps a descriptor's file name at MAX_PATH, so an
/// over-long path is refused here rather than truncated into a different file's name on the far side.
/// </summary>
public static class FilePromiseCodec
{
    /// <summary>cFileName in FILEDESCRIPTORW is MAX_PATH wide characters including the terminator.</summary>
    public const int MaxRelativePathLength = 259;

    /// <summary>A listing has to fit in one clipboard item alongside everything else on the clipboard.</summary>
    public const int MaxEntries = 4096;

    public static ClipboardItem Encode(FilePromiseListing listing)
    {
        var wire = new ClipboardFileListing { Token = listing.Token, Root = listing.RemoteRoot };
        foreach (FilePromiseEntry entry in listing.Entries)
        {
            wire.Entries.Add(new ClipboardFileEntry
            {
                Id = entry.Id,
                RelativePath = entry.RelativePath,
                Size = (ulong)Math.Max(0, entry.Size),
                ModifiedUnixMs = entry.Modified.ToUnixTimeMilliseconds(),
                IsDirectory = entry.IsDirectory,
            });
        }

        return new ClipboardItem(ClipboardItemFormat.FileList, wire.ToByteArray());
    }

    /// <summary>
    /// Reads a listing the peer sent. Returns null when the payload is not a listing at all;
    /// <paramref name="rejected"/> counts entries that were dropped for naming something they must not.
    /// </summary>
    public static FilePromiseListing? Decode(ReadOnlyMemory<byte> payload, out int rejected)
    {
        rejected = 0;
        ClipboardFileListing wire;
        try
        {
            wire = ClipboardFileListing.Parser.ParseFrom(payload.Span);
        }
        catch (InvalidProtocolBufferException)
        {
            return null;
        }

        var entries = new List<FilePromiseEntry>(Math.Min(wire.Entries.Count, MaxEntries));
        foreach (ClipboardFileEntry entry in wire.Entries)
        {
            if (entries.Count >= MaxEntries)
            {
                rejected += wire.Entries.Count - entries.Count;
                break;
            }

            if (!IsAcceptable(entry.RelativePath))
            {
                rejected++;
                continue;
            }

            entries.Add(new FilePromiseEntry(
                entry.Id,
                entry.RelativePath,
                (long)Math.Min(entry.Size, long.MaxValue),
                DateTimeOffset.FromUnixTimeMilliseconds(entry.ModifiedUnixMs),
                entry.IsDirectory));
        }

        return new FilePromiseListing(wire.Token, wire.Root, entries);
    }

    /// <summary>Absolute paths, traversal and anything longer than a Windows descriptor can hold are refused.</summary>
    public static bool IsAcceptable(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath) || relativePath.Length > MaxRelativePathLength)
        {
            return false;
        }

        try
        {
            PathGuard.ValidateRelative(relativePath);
            return true;
        }
        catch (PathGuardException)
        {
            return false;
        }
    }
}
