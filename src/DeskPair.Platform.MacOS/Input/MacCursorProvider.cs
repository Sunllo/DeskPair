using System.IO.Hashing;
using DeskPair.Platform.Abstractions.Cursor;
using DeskPair.Platform.MacOS.Native;

namespace DeskPair.Platform.MacOS.Input;

/// <summary>
/// The pointer on macOS, read as a shape rather than composited into the picture.
///
/// Letting ScreenCaptureKit draw the cursor into the frame is simpler, but then the cursor can only move when
/// a frame arrives: it trails the viewer's own pointer by a frame or more and the two are visibly separate.
/// Sent as a shape, the viewer's pointer becomes the remote one and tracks the mouse instead of the video.
///
/// <c>+[NSCursor currentSystemCursor]</c> gives the shape. Change detection uses a private CoreGraphics seed
/// where it exists; where it does not, the image is hashed instead, which costs a read per poll but keeps the
/// cursor working rather than freezing on whichever shape was first seen.
/// </summary>
public sealed class MacCursorProvider : ICursorProvider
{
    private readonly Lock _lock = new();
    private CursorImage? _cached;

    public ulong GetCurrentCursorId()
    {
        ulong seed = MacShim.fd_cursor_seed();
        return seed != 0 ? seed : Read(0)?.Id ?? 0;
    }

    public CursorImage? GetCursorImage(ulong id)
    {
        lock (_lock)
        {
            if (_cached is { } cached && cached.Id == id)
            {
                return cached;
            }
        }

        return Read(id);
    }

    public (int X, int Y)? GetCursorPosition() =>
        MacShim.fd_cursor_position(out double x, out double y) != 0 ? ((int)x, (int)y) : null;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <param name="id">The id to label the image with, or 0 to derive one from the pixels.</param>
    private CursorImage? Read(ulong id)
    {
        if (MacShim.fd_cursor_image(out int width, out int height, out int hotX, out int hotY, [], 0) == 0)
        {
            return null;
        }

        // A pointer is tens of pixels square; anything else means the read went wrong and is not worth trusting.
        if (width <= 0 || height <= 0 || width > 512 || height > 512)
        {
            return null;
        }

        byte[] bgra = new byte[width * height * 4];
        if (MacShim.fd_cursor_image(out _, out _, out _, out _, bgra, bgra.Length) == 0)
        {
            return null;
        }

        ulong label = id != 0 ? id : XxHash64.HashToUInt64(bgra) | 1;
        var image = new CursorImage(label, hotX, hotY, width, height, bgra);
        lock (_lock)
        {
            _cached = image;
        }

        return image;
    }
}
