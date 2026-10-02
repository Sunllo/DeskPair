using System.Buffers.Binary;
using System.Security.Cryptography;
using Google.Protobuf;
using DeskPair.Protocol.Helper;

namespace DeskPair.Desktop.Engine.Elevation;

/// <summary>
/// Length-prefixed <see cref="HelperMessage"/> framing over the one local named pipe the engine and its SYSTEM
/// helper share. Platform-neutral -- it takes any <see cref="Stream"/> -- so it can be exercised over a pair of
/// in-memory streams in tests. The pipe itself is ACL'd to the engine's user and SYSTEM only; the handshake below
/// is the second lock.
/// </summary>
internal sealed class HelperChannel(Stream stream) : IAsyncDisposable
{
    public const int Protocol = 1;

    // A full BGRA frame of a large display, with slack. Nothing on this channel is larger, and a length past it
    // means a corrupt or hostile peer, not a real message.
    private const int MaxMessage = 64 * 1024 * 1024;

    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public async Task WriteAsync(HelperMessage message, CancellationToken ct)
    {
        byte[] body = message.ToByteArray();
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(header, ct).ConfigureAwait(false);
            await stream.WriteAsync(body, ct).ConfigureAwait(false);
            // No FlushAsync here. On a Windows named pipe Stream.Flush maps to FlushFileBuffers, which blocks
            // until the peer has drained everything written -- and the handshake has both ends write their
            // nonce before either reads, so a flush on each side would wait on the other and deadlock (the
            // helper hangs as SYSTEM, the engine hangs in ElevateAsync and never lowers). WriteAsync has
            // already handed the bytes to the pipe; the peer's ReadAsync gets them without a flush. In-memory
            // streams flush as a no-op, which is why the transport tests never saw this.
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>The next message, or null when the pipe closed.</summary>
    public async Task<HelperMessage?> ReadAsync(CancellationToken ct)
    {
        byte[] header = new byte[4];
        if (!await ReadExactAsync(header, ct).ConfigureAwait(false))
        {
            return null;
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 0 or > MaxMessage)
        {
            throw new InvalidDataException($"Helper message length {length} is out of range");
        }

        byte[] body = new byte[length];
        if (!await ReadExactAsync(body, ct).ConfigureAwait(false))
        {
            return null;
        }

        return HelperMessage.Parser.ParseFrom(body);
    }

    private async Task<bool> ReadExactAsync(byte[] buffer, CancellationToken ct)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0)
            {
                return false;
            }

            read += n;
        }

        return true;
    }

    public ValueTask DisposeAsync() => stream.DisposeAsync();
}

/// <summary>
/// The mutual-token handshake. Each side sends a nonce, then proves it holds the shared one-time token by
/// HMAC-ing the other side's nonce. A peer that cannot produce the proof does not hold the token and is dropped,
/// so a same-user process that learned the pipe name still cannot drive the SYSTEM helper, and the engine will
/// not talk to a helper that is not the one it raised.
/// </summary>
internal static class HelperHandshake
{
    public static async Task<bool> RunAsync(HelperChannel channel, byte[] token, CancellationToken ct)
    {
        byte[] myNonce = RandomNumberGenerator.GetBytes(32);
        await channel.WriteAsync(new HelperMessage { Hello = new HelperHello { Protocol = HelperChannel.Protocol, Nonce = ByteString.CopyFrom(myNonce) } }, ct).ConfigureAwait(false);

        HelperMessage? theirNonce = await channel.ReadAsync(ct).ConfigureAwait(false);
        if (theirNonce is not { UnionCase: HelperMessage.UnionOneofCase.Hello } || theirNonce.Hello.Protocol != HelperChannel.Protocol || theirNonce.Hello.Nonce.Length != 32)
        {
            return false;
        }

        byte[] myProof = Proof(token, theirNonce.Hello.Nonce.ToByteArray());
        await channel.WriteAsync(new HelperMessage { Hello = new HelperHello { Protocol = HelperChannel.Protocol, Proof = ByteString.CopyFrom(myProof) } }, ct).ConfigureAwait(false);

        HelperMessage? theirProof = await channel.ReadAsync(ct).ConfigureAwait(false);
        if (theirProof is not { UnionCase: HelperMessage.UnionOneofCase.Hello })
        {
            return false;
        }

        byte[] expected = Proof(token, myNonce);
        return CryptographicOperations.FixedTimeEquals(theirProof.Hello.Proof.ToByteArray(), expected);
    }

    private static byte[] Proof(byte[] token, byte[] nonce) => HMACSHA256.HashData(token, nonce);
}
