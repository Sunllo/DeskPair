using System.Threading.Channels;

namespace DeskPair.Platform.Abstractions.Terminal;

/// <summary>
/// The read side of a pseudo-terminal, as <see cref="ITerminal.Output"/> hands it out. A platform's reader
/// thread does the blocking reads and <see cref="Post"/>s what it got; the engine reads it asynchronously
/// and can cancel a read, which a blocking read on a pty or a pipe cannot be. <see cref="Complete"/> is
/// end of output: a read returns 0 once what was posted has been consumed.
/// </summary>
public sealed class TerminalOutputStream : Stream
{
    private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private byte[]? _current;
    private int _offset;

    /// <summary>A copy of <paramref name="data"/> becomes readable.</summary>
    public void Post(ReadOnlySpan<byte> data)
    {
        if (!data.IsEmpty)
        {
            _chunks.Writer.TryWrite(data.ToArray());
        }
    }

    public void Complete() => _chunks.Writer.TryComplete();

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_current is null || _offset >= _current.Length)
        {
            try
            {
                _current = await _chunks.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                return 0;
            }

            _offset = 0;
        }

        int n = Math.Min(buffer.Length, _current.Length - _offset);
        _current.AsSpan(_offset, n).CopyTo(buffer.Span);
        _offset += n;
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
