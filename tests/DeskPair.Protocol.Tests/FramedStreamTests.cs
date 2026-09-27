using System.Buffers.Binary;
using System.Security.Cryptography;
using Google.Protobuf;
using DeskPair.Protocol.Crypto;
using DeskPair.Protocol.Framing;
using DeskPair.Protocol.Messages;

namespace DeskPair.Protocol.Tests;

public class FramedStreamTests
{
    private static (SessionKeys A, SessionKeys B) KeyPair()
    {
        byte[] k1 = RandomNumberGenerator.GetBytes(32);
        byte[] k2 = RandomNumberGenerator.GetBytes(32);
        byte[] iv1 = RandomNumberGenerator.GetBytes(4);
        byte[] iv2 = RandomNumberGenerator.GetBytes(4);
        return (new SessionKeys(k1, iv1, k2, iv2), new SessionKeys(k2, iv2, k1, iv1));
    }

    private static Message Chat(string text) => new() { Misc = new Misc { Chat = new ChatMessage { Text = text } } };

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Plaintext_round_trip_survives_any_read_chunking(int chunk)
    {
        (Stream a, Stream b) = TestStreams.DuplexPair(chunk);
        await using var sa = new FramedStream(a, FramedStreamOptions.Peer);
        await using var sb = new FramedStream(b, FramedStreamOptions.Peer);

        await sa.SendAsync(Chat("hello"));
        await sa.SendHeartbeatAsync();
        await sa.SendAsync(Chat("world"));

        using Frame? f1 = await sb.ReceiveAsync();
        using Frame? f2 = await sb.ReceiveAsync();
        Message.Parser.ParseFrom(f1!.Payload.Span).Misc.Chat.Text.ShouldBe("hello");
        Message.Parser.ParseFrom(f2!.Payload.Span).Misc.Chat.Text.ShouldBe("world");
    }

    [Fact]
    public async Task Encrypted_round_trip_in_both_directions()
    {
        (Stream a, Stream b) = TestStreams.DuplexPair(7);
        await using var sa = new FramedStream(a, FramedStreamOptions.Peer);
        await using var sb = new FramedStream(b, FramedStreamOptions.Peer);
        (SessionKeys ka, SessionKeys kb) = KeyPair();
        sa.EnableEncryption(ka);
        sb.EnableEncryption(kb);

        for (int i = 0; i < 50; i++)
        {
            await sa.SendAsync(Chat($"a{i}"));
            await sb.SendAsync(Chat($"b{i}"));
            using Frame? fa = await sb.ReceiveAsync();
            using Frame? fb = await sa.ReceiveAsync();
            Message.Parser.ParseFrom(fa!.Payload.Span).Misc.Chat.Text.ShouldBe($"a{i}");
            Message.Parser.ParseFrom(fb!.Payload.Span).Misc.Chat.Text.ShouldBe($"b{i}");
        }
    }

    [Fact]
    public async Task Large_frame_round_trips_when_within_limit()
    {
        (Stream a, Stream b) = TestStreams.DuplexPair();
        await using var sa = new FramedStream(a, FramedStreamOptions.Peer);
        await using var sb = new FramedStream(b, FramedStreamOptions.Peer);
        (SessionKeys ka, SessionKeys kb) = KeyPair();
        sa.EnableEncryption(ka);
        sb.EnableEncryption(kb);

        byte[] data = RandomNumberGenerator.GetBytes(3 * 1024 * 1024);
        var msg = new Message { VideoFrame = new VideoFrame { Frame = new EncodedVideoFrame { Data = ByteString.CopyFrom(data), Key = true } } };
        Task send = sa.SendAsync(msg).AsTask();
        using Frame? f = await sb.ReceiveAsync();
        await send;
        Message.Parser.ParseFrom(f!.Payload.Span).VideoFrame.Frame.Data.Span.SequenceEqual(data).ShouldBeTrue();
    }

    [Fact]
    public async Task Tampered_ciphertext_is_rejected()
    {
        (Stream a, Stream b) = TestStreams.DuplexPair();
        await using var sb = new FramedStream(b, FramedStreamOptions.Peer);
        (SessionKeys ka, SessionKeys kb) = KeyPair();
        using var tx = new SessionCipher(ka.TxKey, ka.TxIvPrefix);
        sb.EnableEncryption(kb);

        byte[] plain = Chat("x").ToByteArray();
        byte[] wire = new byte[4 + plain.Length + 16];
        BinaryPrimitives.WriteUInt32LittleEndian(wire, (uint)(plain.Length + 16) | 0x8000_0000u);
        tx.Encrypt(plain, wire.AsSpan(4, plain.Length), wire.AsSpan(4 + plain.Length, 16), wire.AsSpan(0, 4));
        wire[4] ^= 0x01;
        await a.WriteAsync(wire);
        await a.FlushAsync();

        await Should.ThrowAsync<ProtocolException>(async () => await sb.ReceiveAsync());
    }

    [Fact]
    public async Task Plaintext_frame_on_encrypted_stream_is_rejected()
    {
        (Stream a, Stream b) = TestStreams.DuplexPair();
        await using var sa = new FramedStream(a, FramedStreamOptions.Peer);
        await using var sb = new FramedStream(b, FramedStreamOptions.Peer);
        (_, SessionKeys kb) = KeyPair();
        sb.EnableEncryption(kb);

        await sa.SendAsync(Chat("plain"));
        await Should.ThrowAsync<ProtocolException>(async () => await sb.ReceiveAsync());
    }

    [Fact]
    public async Task Oversized_frame_is_rejected_by_sender_and_receiver()
    {
        (Stream a, Stream b) = TestStreams.DuplexPair();
        await using var sa = new FramedStream(a, FramedStreamOptions.Control);
        await using var sb = new FramedStream(b, FramedStreamOptions.Control);

        var big = new Message { VideoFrame = new VideoFrame { Frame = new EncodedVideoFrame { Data = ByteString.CopyFrom(new byte[20_000]) } } };
        await Should.ThrowAsync<ProtocolException>(async () => await sa.SendAsync(big));

        byte[] header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 20_000);
        await a.WriteAsync(header);
        await a.FlushAsync();
        await Should.ThrowAsync<ProtocolException>(async () => await sb.ReceiveAsync());
    }

    [Fact]
    public async Task Receive_returns_null_at_clean_end_of_stream()
    {
        (Stream a, Stream b) = TestStreams.DuplexPair();
        await using var sa = new FramedStream(a, FramedStreamOptions.Peer);
        await using var sb = new FramedStream(b, FramedStreamOptions.Peer);
        await sa.SendAsync(Chat("bye"));
        await sa.DisposeAsync();

        using Frame? f = await sb.ReceiveAsync();
        f.ShouldNotBeNull();
        (await sb.ReceiveAsync()).ShouldBeNull();
    }

    [Fact]
    public async Task ReadExactFrame_does_not_consume_following_bytes()
    {
        (Stream a, Stream b) = TestStreams.DuplexPair();
        await using var sa = new FramedStream(a, FramedStreamOptions.Control);
        await sa.SendAsync(Chat("first"));
        byte[] trailing = "TRAILING"u8.ToArray();
        await a.WriteAsync(trailing);
        await a.FlushAsync();

        using Frame? f = await FramedStream.ReadExactFrameAsync(b, ProtocolConstants.MaxControlFrameBytes);
        Message.Parser.ParseFrom(f!.Payload.Span).Misc.Chat.Text.ShouldBe("first");

        byte[] rest = new byte[trailing.Length];
        int n = 0;
        while (n < rest.Length)
        {
            n += await b.ReadAsync(rest.AsMemory(n));
        }

        rest.ShouldBe(trailing);
    }

    [Fact]
    public void Session_cipher_counter_starts_at_one_and_advances()
    {
        using var c = new SessionCipher(new byte[32], new byte[4]);
        c.Counter.ShouldBe(0UL);
        Span<byte> ct = stackalloc byte[3];
        Span<byte> tag = stackalloc byte[16];
        c.Encrypt("abc"u8, ct, tag, ReadOnlySpan<byte>.Empty);
        c.Counter.ShouldBe(1UL);
        c.Encrypt("abc"u8, ct, tag, ReadOnlySpan<byte>.Empty);
        c.Counter.ShouldBe(2UL);
    }
}
