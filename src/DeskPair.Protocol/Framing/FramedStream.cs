using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Security.Cryptography;
using Google.Protobuf;
using DeskPair.Protocol.Crypto;

namespace DeskPair.Protocol.Framing;

public sealed record FramedStreamOptions(int MaxFrameBytes)
{
    public static FramedStreamOptions Peer { get; } = new(ProtocolConstants.MaxPeerFrameBytes);
    public static FramedStreamOptions Control { get; } = new(ProtocolConstants.MaxControlFrameBytes);
}

/// <summary>
/// Length-prefixed message stream. Header is 4 bytes little-endian: bit 31 = encrypted,
/// bit 30 reserved, low 30 bits = payload length. A zero-length frame is a heartbeat and is never encrypted.
/// Encrypted payload = AES-GCM ciphertext followed by a 16-byte tag; the header is the associated data.
/// </summary>
public sealed class FramedStream : IAsyncDisposable
{
    public const int HeaderBytes = 4;
    private const uint EncryptedFlag = 0x8000_0000u;
    private const uint LengthMask = 0x3FFF_FFFFu;

    private readonly Stream _inner;
    private readonly PipeReader _reader;
    private readonly int _maxFrame;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly TimeProvider _time;
    private SessionCipher? _tx;
    private SessionCipher? _rx;
    private bool _disposed;

    public FramedStream(Stream inner, FramedStreamOptions options, TimeProvider? timeProvider = null)
    {
        _inner = inner;
        _maxFrame = options.MaxFrameBytes;
        _time = timeProvider ?? TimeProvider.System;
        _reader = PipeReader.Create(inner, new StreamPipeReaderOptions(bufferSize: 64 * 1024, leaveOpen: true));
        LastReceivedUtc = _time.GetUtcNow();
    }

    public bool IsEncrypted => _tx is not null;

    public DateTimeOffset LastReceivedUtc { get; private set; }

    public DateTimeOffset LastSentUtc { get; private set; }

    /// <summary>Switches both directions to AES-GCM. Must be called exactly once, between the hello messages and the first encrypted frame.</summary>
    public void EnableEncryption(SessionKeys keys)
    {
        if (_tx is not null)
        {
            throw new InvalidOperationException("Encryption is already enabled.");
        }

        _tx = new SessionCipher(keys.TxKey, keys.TxIvPrefix);
        _rx = new SessionCipher(keys.RxKey, keys.RxIvPrefix);
    }

    public ValueTask SendAsync(IMessage message, CancellationToken ct = default)
    {
        int size = message.CalculateSize();
        byte[] plain = ArrayPool<byte>.Shared.Rent(Math.Max(size, 1));
        try
        {
            message.WriteTo(plain.AsSpan(0, size));
            return SendCoreAsync(plain, size, ct);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(plain);
            throw;
        }
    }

    public ValueTask SendRawAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        byte[] plain = ArrayPool<byte>.Shared.Rent(Math.Max(payload.Length, 1));
        payload.Span.CopyTo(plain);
        return SendCoreAsync(plain, payload.Length, ct);
    }

    public async ValueTask SendHeartbeatAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _inner.WriteAsync(new byte[HeaderBytes], ct).ConfigureAwait(false);
            await _inner.FlushAsync(ct).ConfigureAwait(false);
            LastSentUtc = _time.GetUtcNow();
        }
        finally
        {
            _sendLock.Release();
        }
    }

    // Takes ownership of `plain` (pooled) and returns it.
    private async ValueTask SendCoreAsync(byte[] plain, int size, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (size == 0)
        {
            ArrayPool<byte>.Shared.Return(plain);
            throw new ProtocolException("Cannot send an empty payload; use SendHeartbeatAsync.");
        }

        if (size > _maxFrame)
        {
            ArrayPool<byte>.Shared.Return(plain);
            throw new ProtocolException($"Frame of {size} bytes exceeds the {_maxFrame} byte limit.");
        }

        int bodyLen = _tx is null ? size : size + SessionCipher.TagBytes;
        byte[] wire = ArrayPool<byte>.Shared.Rent(HeaderBytes + bodyLen);
        try
        {
            uint header = (uint)bodyLen | (_tx is null ? 0u : EncryptedFlag);
            BinaryPrimitives.WriteUInt32LittleEndian(wire, header);
            if (_tx is null)
            {
                plain.AsSpan(0, size).CopyTo(wire.AsSpan(HeaderBytes));
            }

            await _sendLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_tx is not null)
                {
                    // Encrypt under the send lock so the nonce counter matches the wire order.
                    _tx.Encrypt(
                        plain.AsSpan(0, size),
                        wire.AsSpan(HeaderBytes, size),
                        wire.AsSpan(HeaderBytes + size, SessionCipher.TagBytes),
                        wire.AsSpan(0, HeaderBytes));
                }

                await _inner.WriteAsync(wire.AsMemory(0, HeaderBytes + bodyLen), ct).ConfigureAwait(false);
                await _inner.FlushAsync(ct).ConfigureAwait(false);
                LastSentUtc = _time.GetUtcNow();
            }
            finally
            {
                _sendLock.Release();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(wire);
            ArrayPool<byte>.Shared.Return(plain);
        }
    }

    /// <summary>Returns the next non-heartbeat frame, or null at end of stream. Heartbeats only refresh <see cref="LastReceivedUtc"/>.</summary>
    public async ValueTask<Frame?> ReceiveAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (true)
        {
            ReadResult result = await _reader.ReadAsync(ct).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = result.Buffer;

            if (TryParseFrame(ref buffer, out Frame? frame, out bool heartbeat))
            {
                _reader.AdvanceTo(buffer.Start);
                LastReceivedUtc = _time.GetUtcNow();
                if (heartbeat)
                {
                    continue;
                }

                return frame;
            }

            if (result.IsCompleted)
            {
                _reader.AdvanceTo(buffer.Start);
                if (!buffer.IsEmpty)
                {
                    throw new ProtocolException("Stream ended in the middle of a frame.");
                }

                return null;
            }

            _reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    private bool TryParseFrame(ref ReadOnlySequence<byte> buffer, out Frame? frame, out bool heartbeat)
    {
        frame = null;
        heartbeat = false;
        if (buffer.Length < HeaderBytes)
        {
            return false;
        }

        Span<byte> header = stackalloc byte[HeaderBytes];
        buffer.Slice(0, HeaderBytes).CopyTo(header);
        uint raw = BinaryPrimitives.ReadUInt32LittleEndian(header);
        int bodyLen = (int)(raw & LengthMask);
        bool encrypted = (raw & EncryptedFlag) != 0;

        if (bodyLen == 0)
        {
            if (encrypted)
            {
                throw new ProtocolException("Heartbeat frames cannot be encrypted.");
            }

            buffer = buffer.Slice(HeaderBytes);
            heartbeat = true;
            return true;
        }

        int maxBody = _rx is null ? _maxFrame : _maxFrame + SessionCipher.TagBytes;
        if (bodyLen > maxBody)
        {
            throw new ProtocolException($"Frame of {bodyLen} bytes exceeds the {_maxFrame} byte limit.");
        }

        if (encrypted != (_rx is not null))
        {
            throw new ProtocolException(encrypted ? "Unexpected encrypted frame." : "Expected an encrypted frame.");
        }

        if (encrypted && bodyLen < SessionCipher.TagBytes + 1)
        {
            throw new ProtocolException("Encrypted frame too short.");
        }

        if (buffer.Length < HeaderBytes + bodyLen)
        {
            return false;
        }

        ReadOnlySequence<byte> body = buffer.Slice(HeaderBytes, bodyLen);
        byte[] owned = ArrayPool<byte>.Shared.Rent(bodyLen);
        int payloadLen = bodyLen;
        try
        {
            if (_rx is null)
            {
                body.CopyTo(owned);
            }
            else
            {
                byte[] cipher = ArrayPool<byte>.Shared.Rent(bodyLen);
                try
                {
                    body.CopyTo(cipher);
                    payloadLen = bodyLen - SessionCipher.TagBytes;
                    try
                    {
                        _rx.Decrypt(
                            cipher.AsSpan(0, payloadLen),
                            cipher.AsSpan(payloadLen, SessionCipher.TagBytes),
                            owned.AsSpan(0, payloadLen),
                            header);
                    }
                    catch (CryptographicException e)
                    {
                        throw new ProtocolException("Frame authentication failed.", e);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(cipher);
                }
            }
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(owned);
            throw;
        }

        frame = new Frame(owned, payloadLen);
        buffer = buffer.Slice(HeaderBytes + bodyLen);
        return true;
    }

    /// <summary>
    /// Reads exactly one plaintext frame from a raw stream without buffering anything past it.
    /// Used where the bytes after the first frame belong to someone else (relay pairing).
    /// </summary>
    public static async ValueTask<Frame?> ReadExactFrameAsync(Stream stream, int maxFrameBytes, CancellationToken ct = default)
    {
        byte[] header = new byte[HeaderBytes];
        int got = await ReadFullyAsync(stream, header, ct).ConfigureAwait(false);
        if (got == 0)
        {
            return null;
        }

        if (got < HeaderBytes)
        {
            throw new ProtocolException("Stream ended inside a frame header.");
        }

        uint raw = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if ((raw & EncryptedFlag) != 0)
        {
            throw new ProtocolException("Expected a plaintext frame.");
        }

        int len = (int)(raw & LengthMask);
        if (len == 0)
        {
            throw new ProtocolException("Expected a message, got a heartbeat.");
        }

        if (len > maxFrameBytes)
        {
            throw new ProtocolException($"Frame of {len} bytes exceeds the {maxFrameBytes} byte limit.");
        }

        byte[] body = ArrayPool<byte>.Shared.Rent(len);
        try
        {
            if (await ReadFullyAsync(stream, body.AsMemory(0, len), ct).ConfigureAwait(false) < len)
            {
                throw new ProtocolException("Stream ended inside a frame body.");
            }
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(body);
            throw;
        }

        return new Frame(body, len);
    }

    private static async ValueTask<int> ReadFullyAsync(Stream stream, Memory<byte> target, CancellationToken ct)
    {
        int total = 0;
        while (total < target.Length)
        {
            int n = await stream.ReadAsync(target[total..], ct).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _reader.CompleteAsync().ConfigureAwait(false);
        _tx?.Dispose();
        _rx?.Dispose();
        _sendLock.Dispose();
        await _inner.DisposeAsync().ConfigureAwait(false);
    }
}
