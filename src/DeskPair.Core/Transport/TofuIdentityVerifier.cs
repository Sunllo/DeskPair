using System.Security.Cryptography;
using System.Text.Json;
using DeskPair.Protocol;
using DeskPair.Protocol.Crypto;

namespace DeskPair.Core.Transport;

/// <summary>Pinned host keys for direct targets, keyed by the address the user typed.</summary>
public interface IKnownHostsStore
{
    /// <summary>SHA-256 fingerprint of the pinned identity key, or null when unknown.</summary>
    byte[]? Get(string target);

    void Set(string target, byte[] fingerprint);

    void Remove(string target);

    /// <summary>Everything pinned so far, so the user can review and forget entries.</summary>
    IReadOnlyList<(string Target, byte[] Fingerprint)> All() => [];
}

public sealed class InMemoryKnownHostsStore : IKnownHostsStore
{
    private readonly Dictionary<string, byte[]> _pins = new(StringComparer.OrdinalIgnoreCase);

    public byte[]? Get(string target)
    {
        lock (_pins)
        {
            return _pins.TryGetValue(target, out byte[]? pin) ? pin : null;
        }
    }

    public void Set(string target, byte[] fingerprint)
    {
        lock (_pins)
        {
            _pins[target] = fingerprint;
        }
    }

    public void Remove(string target)
    {
        lock (_pins)
        {
            _pins.Remove(target);
        }
    }

    public IReadOnlyList<(string Target, byte[] Fingerprint)> All()
    {
        lock (_pins)
        {
            return _pins.Select(kv => (kv.Key, kv.Value)).ToList();
        }
    }
}

/// <summary>JSON file of target → base64 fingerprint; written on every change.</summary>
public sealed class FileKnownHostsStore : IKnownHostsStore
{
    private readonly string _path;
    private readonly Dictionary<string, byte[]> _pins = new(StringComparer.OrdinalIgnoreCase);

    public FileKnownHostsStore(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path))
            {
                Dictionary<string, string>? map = JsonSerializer.Deserialize(File.ReadAllText(path), KnownHostsJson.Default.DictionaryStringString);
                if (map is not null)
                {
                    foreach (KeyValuePair<string, string> kv in map)
                    {
                        _pins[kv.Key] = Convert.FromBase64String(kv.Value);
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or JsonException or FormatException or UnauthorizedAccessException)
        {
        }
    }

    public byte[]? Get(string target)
    {
        lock (_pins)
        {
            return _pins.TryGetValue(target, out byte[]? pin) ? pin : null;
        }
    }

    public void Set(string target, byte[] fingerprint)
    {
        lock (_pins)
        {
            _pins[target] = fingerprint;
            Save();
        }
    }

    public void Remove(string target)
    {
        lock (_pins)
        {
            _pins.Remove(target);
            Save();
        }
    }

    public IReadOnlyList<(string Target, byte[] Fingerprint)> All()
    {
        lock (_pins)
        {
            return _pins.Select(kv => (kv.Key, kv.Value)).OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_pins.ToDictionary(k => k.Key, v => Convert.ToBase64String(v.Value)), KnownHostsJson.Default.DictionaryStringString));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// Trust on first use: the first key seen for a target is pinned; a different key later is rejected
/// with <see cref="HandshakeException"/> so the UI can warn the user (who may clear the pin).
/// </summary>
public sealed class TofuIdentityVerifier : IHostIdentityVerifier
{
    private readonly IKnownHostsStore _store;
    private readonly string _target;

    public TofuIdentityVerifier(IKnownHostsStore store, string target)
    {
        _store = store;
        _target = target;
    }

    public static byte[] Fingerprint(ReadOnlySpan<byte> identityPk) => SHA256.HashData(identityPk);

    public void Verify(string hostId, ReadOnlySpan<byte> identityPk)
    {
        byte[] fingerprint = Fingerprint(identityPk);
        byte[]? pinned = _store.Get(_target);
        if (pinned is null)
        {
            _store.Set(_target, fingerprint);
            return;
        }

        if (!CryptographicOperations.FixedTimeEquals(pinned, fingerprint))
        {
            throw new HandshakeException($"The identity key of {_target} has changed (expected {Convert.ToHexString(pinned)[..16]}, got {Convert.ToHexString(fingerprint)[..16]}). Remove the pinned key if this is expected.");
        }
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class KnownHostsJson : System.Text.Json.Serialization.JsonSerializerContext
{
}
