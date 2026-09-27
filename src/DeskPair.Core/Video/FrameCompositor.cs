using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Abstractions.Codec;
using DeskPair.Protocol.Messages;

namespace DeskPair.Core.Video;

/// <summary>
/// The viewer's picture of one remote display: decoded video frames replace the whole canvas, lossless
/// tile updates patch parts of it. The canvas keeps the wire-declared size so tiles always line up.
/// </summary>
/// <remarks>
/// A video frame is passed through by reference (decoders keep their output buffer valid until the next
/// decode), so pure video costs no copy; the canvas is materialised only when a tile update has to patch
/// the latest picture. Not thread-safe: decode and tile application must run on one thread.
/// </remarks>
public sealed class FrameCompositor
{
    private byte[] _canvas = [];
    private ReadOnlyMemory<byte> _borrowed;
    private int _borrowedStride;
    private bool _canvasStale;

    public int Width { get; private set; }

    public int Height { get; private set; }

    public int Stride => Width * 4;

    public bool HasPicture { get; private set; }

    /// <summary>Tiles applied since the last video frame; diagnostics for the UI.</summary>
    public long TilesApplied { get; private set; }

    /// <summary>Full-frame copies made to materialise the canvas; diagnostics.</summary>
    public long CanvasCopies { get; private set; }

    public DecodedFrame Current => _canvasStale
        ? new DecodedFrame { Width = Width, Height = Height, Format = PixelFormat.Bgra32, Cpu = _borrowed, Stride = _borrowedStride }
        : new DecodedFrame { Width = Width, Height = Height, Format = PixelFormat.Bgra32, Cpu = new ReadOnlyMemory<byte>(_canvas, 0, Stride * Height), Stride = Stride };

    /// <summary>Adopts a decoded frame (by reference); the canvas adopts the frame's own size (decoders may crop/pad).</summary>
    public DecodedFrame ApplyVideo(in DecodedFrame frame)
    {
        Width = frame.Width;
        Height = frame.Height;
        _borrowed = frame.Cpu;
        _borrowedStride = frame.Stride;
        _canvasStale = true;
        HasPicture = true;
        return frame;
    }

    /// <summary>Patches lossless tiles; returns false when the update does not match the canvas size (the caller should ask for a refresh).</summary>
    public bool ApplyTiles(TileUpdate update)
    {
        if (update.Width == 0 || update.Height == 0)
        {
            return false;
        }

        if ((int)update.Width != Width || (int)update.Height != Height)
        {
            if (HasPicture)
            {
                return false;
            }

            // No video yet: start a black canvas of the declared size so early tiles are not lost.
            Width = (int)update.Width;
            Height = (int)update.Height;
            _canvas = new byte[Stride * Height];
            _canvasStale = false;
            HasPicture = true;
        }
        else if (_canvasStale)
        {
            Materialise();
        }

        int tileSize = (int)Math.Max(1, update.TileSize);
        foreach (Tile tile in update.Tiles)
        {
            int x = (int)tile.Col * tileSize, y = (int)tile.Row * tileSize;
            int w = (int)tile.W, h = (int)tile.H;
            if (w <= 0 || h <= 0 || x + w > Width || y + h > Height)
            {
                continue;
            }

            TileCodec.Decode(tile.Data.Span, w, h, _canvas.AsSpan(y * Stride + x * 4), Stride);
            TilesApplied++;
        }

        return true;
    }

    /// <summary>
    /// Copies the borrowed decoder picture into the compositor's own canvas. Call before decoding a frame that will
    /// not be presented: decoders reuse their output buffer, which would otherwise change the picture underneath.
    /// </summary>
    public void DetachFromDecoder()
    {
        if (_canvasStale)
        {
            Materialise();
        }
    }

    private void Materialise()
    {
        if (_canvas.Length < Stride * Height)
        {
            _canvas = new byte[Stride * Height];
        }

        ReadOnlySpan<byte> src = _borrowed.Span;
        if (_borrowedStride == Stride)
        {
            src[..(Stride * Height)].CopyTo(_canvas);
        }
        else
        {
            for (int y = 0; y < Height; y++)
            {
                src.Slice(y * _borrowedStride, Stride).CopyTo(_canvas.AsSpan(y * Stride));
            }
        }

        _borrowed = default;
        _canvasStale = false;
        CanvasCopies++;
    }
}
