using System.Buffers.Binary;
using System.Security.Cryptography;
using Google.Protobuf;
using DeskPair.Platform.Abstractions.Clipboard;
using DeskPair.Protocol;
using DeskPair.Protocol.Messages;

namespace DeskPair.Core.Clipboard;

/// <summary>
/// Echo suppression shared by both sides: content we just applied from the peer is not sent back,
/// and content we just sent is not re-applied if it bounces. Both are compared by content hash.
/// </summary>
public sealed class ClipboardSync
{
    private readonly object _lock = new();
    private byte[]? _lastApplied;
    private byte[]? _lastSent;

    public static byte[] Hash(MultiClipboards clipboards)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> header = stackalloc byte[8];
        foreach (Protocol.Messages.Clipboard c in clipboards.Items.OrderBy(i => (int)i.Format))
        {
            BinaryPrimitives.WriteInt32LittleEndian(header, (int)c.Format);
            BinaryPrimitives.WriteInt32LittleEndian(header[4..], c.Content.Length);
            sha.AppendData(header);
            sha.AppendData(c.Content.Span);
        }

        return sha.GetHashAndReset();
    }

    /// <summary>False when the content is what the peer just gave us, or what we already sent.</summary>
    public bool ShouldSend(ReadOnlySpan<byte> hash)
    {
        lock (_lock)
        {
            return !(_lastApplied is not null && hash.SequenceEqual(_lastApplied)) && !(_lastSent is not null && hash.SequenceEqual(_lastSent));
        }
    }

    public void MarkSent(byte[] hash)
    {
        lock (_lock)
        {
            _lastSent = hash;
        }
    }

    /// <summary>False when the content is our own send bouncing back.</summary>
    public bool ShouldApply(ReadOnlySpan<byte> hash)
    {
        lock (_lock)
        {
            return !(_lastSent is not null && hash.SequenceEqual(_lastSent)) && !(_lastApplied is not null && hash.SequenceEqual(_lastApplied));
        }
    }

    public void MarkApplied(byte[] hash)
    {
        lock (_lock)
        {
            _lastApplied = hash;
        }
    }

    // ---- format mapping ----

    /// <summary>
    /// Packs clipboard items for the wire, dropping any that will not fit.
    /// </summary>
    /// <remarks>
    /// An item that would take the payload past <see cref="ProtocolConstants.MaxClipboardBytes"/> is left
    /// out and the rest are still sent. This used to abandon the whole clipboard the moment the running
    /// total was exceeded, which meant a screenshot too large to carry also silently took the text beside
    /// it — and, because the total accumulated in item order, whether that happened depended on which
    /// format the platform happened to list first.
    /// </remarks>
    public static MultiClipboards? ToWire(IReadOnlyList<ClipboardItem> items)
    {
        var result = new MultiClipboards();
        long total = 0;
        foreach (ClipboardItem item in items)
        {
            if (total + item.Payload.Length > ProtocolConstants.MaxClipboardBytes)
            {
                continue;
            }

            total += item.Payload.Length;

            result.Items.Add(new Protocol.Messages.Clipboard
            {
                Format = item.Format switch
                {
                    ClipboardItemFormat.Html => ClipboardFormat.CfHtml,
                    ClipboardItemFormat.Rtf => ClipboardFormat.CfRtf,
                    ClipboardItemFormat.ImagePng => ClipboardFormat.CfImagePng,
                    ClipboardItemFormat.FileList => ClipboardFormat.CfFileList,
                    _ => ClipboardFormat.CfText,
                },
                Content = ByteString.CopyFrom(item.Payload.Span),
            });
        }

        if (result.Items.Count == 0)
        {
            return null;
        }

        result.ContentHash = ByteString.CopyFrom(Hash(result));
        return result;
    }

    public static IReadOnlyList<ClipboardItem> FromWire(MultiClipboards clipboards) =>
        clipboards.Items.Select(c => new ClipboardItem(
            c.Format switch
            {
                ClipboardFormat.CfHtml => ClipboardItemFormat.Html,
                ClipboardFormat.CfRtf => ClipboardItemFormat.Rtf,
                ClipboardFormat.CfImagePng => ClipboardItemFormat.ImagePng,
                ClipboardFormat.CfFileList => ClipboardItemFormat.FileList,
                _ => ClipboardItemFormat.Text,
            },
            c.Content.Memory)).ToList();
}
