using System.Buffers.Binary;
using System.Security.Cryptography;

namespace DeskPair.Protocol.Framing;

/// <summary>
/// One-direction AES-256-GCM stream cipher. Nonce = 4-byte prefix || 8-byte little-endian counter,
/// counter starts at 1 and must never repeat under the same key.
/// </summary>
public sealed class SessionCipher : IDisposable
{
    public const int TagBytes = 16;
    public const int NonceBytes = 12;

    private readonly AesGcm _aes;
    private readonly byte[] _nonce = new byte[NonceBytes];
    private ulong _counter;
    private bool _disposed;

    public SessionCipher(ReadOnlySpan<byte> key, ReadOnlySpan<byte> ivPrefix)
    {
        if (!AesGcm.IsSupported)
        {
            throw new PlatformNotSupportedException("AES-GCM is not available on this platform.");
        }

        ArgumentOutOfRangeException.ThrowIfNotEqual(ivPrefix.Length, 4);
        _aes = new AesGcm(key, TagBytes);
        ivPrefix.CopyTo(_nonce);
    }

    public ulong Counter => _counter;

    public void Encrypt(ReadOnlySpan<byte> plaintext, Span<byte> ciphertext, Span<byte> tag, ReadOnlySpan<byte> associatedData)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        NextNonce();
        _aes.Encrypt(_nonce, plaintext, ciphertext, tag, associatedData);
    }

    /// <exception cref="CryptographicException">The tag did not verify; the stream is compromised.</exception>
    public void Decrypt(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> tag, Span<byte> plaintext, ReadOnlySpan<byte> associatedData)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        NextNonce();
        _aes.Decrypt(_nonce, ciphertext, tag, plaintext, associatedData);
    }

    private void NextNonce()
    {
        if (_counter == ulong.MaxValue)
        {
            throw new CryptographicException("Session cipher counter exhausted; rekey required.");
        }

        _counter++;
        BinaryPrimitives.WriteUInt64LittleEndian(_nonce.AsSpan(4), _counter);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _aes.Dispose();
        CryptographicOperations.ZeroMemory(_nonce);
    }
}
