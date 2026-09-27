using System.Security.Cryptography;

namespace DeskPair.Protocol.Crypto;

/// <summary>Directional AES-256-GCM keys and nonce prefixes derived by the handshake.</summary>
public sealed class SessionKeys : IDisposable
{
    public const int KeyBytes = 32;
    public const int IvPrefixBytes = 4;

    private readonly byte[] _txKey;
    private readonly byte[] _txIvPrefix;
    private readonly byte[] _rxKey;
    private readonly byte[] _rxIvPrefix;
    private readonly MediaKeys? _media;

    public SessionKeys(byte[] txKey, byte[] txIvPrefix, byte[] rxKey, byte[] rxIvPrefix, MediaKeys? media = null)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(txKey.Length, KeyBytes);
        ArgumentOutOfRangeException.ThrowIfNotEqual(rxKey.Length, KeyBytes);
        ArgumentOutOfRangeException.ThrowIfNotEqual(txIvPrefix.Length, IvPrefixBytes);
        ArgumentOutOfRangeException.ThrowIfNotEqual(rxIvPrefix.Length, IvPrefixBytes);
        _txKey = txKey;
        _txIvPrefix = txIvPrefix;
        _rxKey = rxKey;
        _rxIvPrefix = rxIvPrefix;
        _media = media;
    }

    /// <summary>Independent keys for the UDP media channel; the caller owns the returned copy (kept after this object is disposed).</summary>
    public MediaKeys ExportMediaKeys() => _media?.Clone() ?? throw new InvalidOperationException("This handshake derived no media keys.");

    public ReadOnlySpan<byte> TxKey => _txKey;
    public ReadOnlySpan<byte> TxIvPrefix => _txIvPrefix;
    public ReadOnlySpan<byte> RxKey => _rxKey;
    public ReadOnlySpan<byte> RxIvPrefix => _rxIvPrefix;

    /// <summary>True when this side's transmit key equals the other side's receive key (test helper).</summary>
    public bool IsCounterpartOf(SessionKeys other) =>
        TxKey.SequenceEqual(other.RxKey) && RxKey.SequenceEqual(other.TxKey) &&
        TxIvPrefix.SequenceEqual(other.RxIvPrefix) && RxIvPrefix.SequenceEqual(other.TxIvPrefix);

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_txKey);
        CryptographicOperations.ZeroMemory(_rxKey);
        _media?.Dispose();
    }
}

/// <summary>Directional AES-256-GCM keys and nonce prefixes for the UDP media channel (distinct from the TCP session's).</summary>
public sealed class MediaKeys : IDisposable
{
    private readonly byte[] _txKey;
    private readonly byte[] _txIvPrefix;
    private readonly byte[] _rxKey;
    private readonly byte[] _rxIvPrefix;

    public MediaKeys(byte[] txKey, byte[] txIvPrefix, byte[] rxKey, byte[] rxIvPrefix)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(txKey.Length, SessionKeys.KeyBytes);
        ArgumentOutOfRangeException.ThrowIfNotEqual(rxKey.Length, SessionKeys.KeyBytes);
        ArgumentOutOfRangeException.ThrowIfNotEqual(txIvPrefix.Length, SessionKeys.IvPrefixBytes);
        ArgumentOutOfRangeException.ThrowIfNotEqual(rxIvPrefix.Length, SessionKeys.IvPrefixBytes);
        _txKey = txKey;
        _txIvPrefix = txIvPrefix;
        _rxKey = rxKey;
        _rxIvPrefix = rxIvPrefix;
    }

    public ReadOnlySpan<byte> TxKey => _txKey;
    public ReadOnlySpan<byte> TxIvPrefix => _txIvPrefix;
    public ReadOnlySpan<byte> RxKey => _rxKey;
    public ReadOnlySpan<byte> RxIvPrefix => _rxIvPrefix;

    public MediaKeys Clone() => new((byte[])_txKey.Clone(), (byte[])_txIvPrefix.Clone(), (byte[])_rxKey.Clone(), (byte[])_rxIvPrefix.Clone());

    public bool IsCounterpartOf(MediaKeys other) =>
        TxKey.SequenceEqual(other.RxKey) && RxKey.SequenceEqual(other.TxKey) &&
        TxIvPrefix.SequenceEqual(other.RxIvPrefix) && RxIvPrefix.SequenceEqual(other.TxIvPrefix);

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_txKey);
        CryptographicOperations.ZeroMemory(_rxKey);
    }
}
