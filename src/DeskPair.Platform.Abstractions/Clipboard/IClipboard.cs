using System.Threading.Channels;

namespace DeskPair.Platform.Abstractions.Clipboard;

public enum ClipboardItemFormat
{
    Text,
    Html,
    Rtf,
    ImagePng,

    /// <summary>
    /// A promise rather than content: the payload is a serialised listing of what was copied, and the bytes
    /// are fetched over the file transfer channel when someone pastes. Only a clipboard implementing
    /// <see cref="IFilePromiseClipboard"/> can publish one.
    /// </summary>
    FileList,
}

public sealed record ClipboardItem(ClipboardItemFormat Format, ReadOnlyMemory<byte> Payload);

public interface IClipboard : IAsyncDisposable
{
    /// <summary>Emits the full clipboard content after each external change (own writes are suppressed).</summary>
    ChannelReader<IReadOnlyList<ClipboardItem>> Changes { get; }

    ValueTask<IReadOnlyList<ClipboardItem>> ReadAsync(CancellationToken ct);

    ValueTask WriteAsync(IReadOnlyList<ClipboardItem> items, CancellationToken ct);
}
