using System.IO.Pipelines;
using System.IO.Pipes;
using System.Security.Cryptography;
using Google.Protobuf;
using DeskPair.Desktop.Engine.Elevation;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Protocol.Helper;

namespace DeskPair.Desktop.Tests;

/// <summary>
/// The local transport the engine and its SYSTEM helper share: the framing carries a message across intact, the
/// token handshake lets only the two ends that share the token through, and the engine's proxy capturer hands out
/// what the helper pushes. The pipe itself and the SYSTEM launch are checked on the VM.
/// </summary>
public class ElevationTransportTests
{
    [Fact]
    public async Task A_message_survives_the_framing()
    {
        var pipe = new Pipe();
        var writer = new HelperChannel(pipe.Writer.AsStream());
        var reader = new HelperChannel(pipe.Reader.AsStream());

        await writer.WriteAsync(new HelperMessage { Frame = new HelperFrame { Id = 3, Seq = 7, Width = 4, Height = 2, Stride = 16, Bgra = ByteString.CopyFrom(new byte[32]) } }, CancellationToken.None);
        HelperMessage? got = await reader.ReadAsync(CancellationToken.None);

        got.ShouldNotBeNull();
        got.UnionCase.ShouldBe(HelperMessage.UnionOneofCase.Frame);
        got.Frame.Id.ShouldBe(3);
        got.Frame.Seq.ShouldBe(7u);
        got.Frame.Bgra.Length.ShouldBe(32);
    }

    [Fact]
    public async Task The_handshake_passes_when_both_hold_the_token()
    {
        (HelperChannel a, HelperChannel b) = DuplexChannels();
        byte[] token = RandomNumberGenerator.GetBytes(32);

        bool[] both = await Task.WhenAll(
            HelperHandshake.RunAsync(a, token, CancellationToken.None),
            HelperHandshake.RunAsync(b, token, CancellationToken.None));

        both.ShouldAllBe(ok => ok);
    }

    [Fact]
    public async Task The_handshake_passes_over_a_real_named_pipe()
    {
        // The in-memory tests above cannot see the bug this guards: on a real Windows named pipe Stream.Flush maps
        // to FlushFileBuffers, which blocks until the peer drains the write. The handshake has both ends write
        // their nonce before either reads, so a per-write flush made each side wait on the other -- a deadlock
        // that hung the SYSTEM helper and the engine. This runs the handshake over an actual pipe pair.
        string name = "deskpair-test-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous, 64 * 1024, 64 * 1024);
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);

        Task listening = server.WaitForConnectionAsync();
        await client.ConnectAsync(5000);
        await listening;

        byte[] token = RandomNumberGenerator.GetBytes(32);
        Task<bool[]> handshakes = Task.WhenAll(
            HelperHandshake.RunAsync(new HelperChannel(server), token, CancellationToken.None),
            HelperHandshake.RunAsync(new HelperChannel(client), token, CancellationToken.None));

        // Never wait on it forever: a regression (a flush creeping back in) must fail the test, not hang the run.
        Task first = await Task.WhenAny(handshakes, Task.Delay(TimeSpan.FromSeconds(10)));
        first.ShouldBe(handshakes, "the token handshake deadlocked over a real named pipe");
        (await handshakes).ShouldAllBe(ok => ok);
    }

    [Fact]
    public async Task The_handshake_fails_when_the_tokens_differ()
    {
        (HelperChannel a, HelperChannel b) = DuplexChannels();

        bool[] both = await Task.WhenAll(
            HelperHandshake.RunAsync(a, RandomNumberGenerator.GetBytes(32), CancellationToken.None),
            HelperHandshake.RunAsync(b, RandomNumberGenerator.GetBytes(32), CancellationToken.None));

        both.ShouldAllBe(ok => !ok);
    }

    [Fact]
    public async Task The_proxy_capturer_hands_out_a_pushed_frame_then_the_switch()
    {
        var capturer = new HelperScreenCapturer(1, default);

        capturer.Push(new HelperFrame { Id = 1, Width = 2, Height = 2, Stride = 8, Bgra = ByteString.CopyFrom(new byte[16]) });
        CaptureResult frame = await capturer.AcquireFrameAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        frame.Status.ShouldBe(CaptureStatus.Frame);
        frame.Frame.Width.ShouldBe(2);
        frame.Frame.Cpu.Length.ShouldBe(16);

        // Nothing new: it times out rather than repeating the frame.
        CaptureResult idle = await capturer.AcquireFrameAsync(TimeSpan.FromMilliseconds(100), CancellationToken.None);
        idle.Status.ShouldBe(CaptureStatus.Timeout);

        capturer.MarkSwitched();
        CaptureResult switched = await capturer.AcquireFrameAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        switched.Status.ShouldBe(CaptureStatus.DesktopSwitched);
    }

    private static (HelperChannel A, HelperChannel B) DuplexChannels()
    {
        var ab = new Pipe();
        var ba = new Pipe();
        var a = new HelperChannel(new DuplexStream(ba.Reader.AsStream(), ab.Writer.AsStream()));
        var b = new HelperChannel(new DuplexStream(ab.Reader.AsStream(), ba.Writer.AsStream()));
        return (a, b);
    }

    /// <summary>Reads from one stream, writes to another: a two-way channel from two one-way pipes.</summary>
    private sealed class DuplexStream(Stream read, Stream write) : Stream
    {
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => read.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => read.ReadAsync(buffer, ct);
        public override void Write(byte[] buffer, int offset, int count) => write.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => write.WriteAsync(buffer, ct);
        public override void Flush() => write.Flush();
        public override Task FlushAsync(CancellationToken ct) => write.FlushAsync(ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
