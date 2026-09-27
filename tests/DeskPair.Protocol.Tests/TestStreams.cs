using System.IO.Pipelines;

namespace DeskPair.Protocol.Tests;

/// <summary>
/// Bidirectional in-memory stream pair; what one side writes the other reads. Public because the Core tests
/// need it too: a message pump is only worth testing over something that behaves like a connection.
/// </summary>
public static class TestStreams
{
    public static (Stream A, Stream B) DuplexPair(int maxReadChunk = int.MaxValue)
    {
        var aToB = new Pipe();
        var bToA = new Pipe();
        Stream a = new DuplexStream(bToA.Reader.AsStream(), aToB.Writer.AsStream(), maxReadChunk);
        Stream b = new DuplexStream(aToB.Reader.AsStream(), bToA.Writer.AsStream(), maxReadChunk);
        return (a, b);
    }

    private sealed class DuplexStream(Stream read, Stream write, int maxReadChunk) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => write.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => write.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => read.Read(buffer, offset, Math.Min(count, maxReadChunk));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            read.ReadAsync(buffer[..Math.Min(buffer.Length, maxReadChunk)], cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => write.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            write.WriteAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                write.Dispose();
                read.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
