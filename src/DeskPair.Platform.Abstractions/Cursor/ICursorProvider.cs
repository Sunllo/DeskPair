namespace DeskPair.Platform.Abstractions.Cursor;

/// <summary>Cursor bitmap in BGRA32, premultiplied alpha.</summary>
public sealed record CursorImage(ulong Id, int HotX, int HotY, int Width, int Height, ReadOnlyMemory<byte> Bgra);

public interface ICursorProvider : IAsyncDisposable
{
    /// <summary>Stable id of the current cursor shape; callers cache images by it.</summary>
    ulong GetCurrentCursorId();

    CursorImage? GetCursorImage(ulong id);

    (int X, int Y)? GetCursorPosition();
}
