using System.Buffers;

namespace DeskPair.Protocol.Framing;

/// <summary>
/// One received frame backed by a pooled buffer. Dispose exactly once after the payload has been parsed.
/// </summary>
public sealed class Frame : IDisposable
{
    private byte[]? _buffer;
    private readonly int _length;

    internal Frame(byte[] buffer, int length)
    {
        _buffer = buffer;
        _length = length;
    }

    public ReadOnlyMemory<byte> Payload => _buffer is null
        ? throw new ObjectDisposedException(nameof(Frame))
        : new ReadOnlyMemory<byte>(_buffer, 0, _length);

    public int Length => _length;

    public void Dispose()
    {
        byte[]? b = Interlocked.Exchange(ref _buffer, null);
        if (b is not null)
        {
            ArrayPool<byte>.Shared.Return(b);
        }
    }
}
