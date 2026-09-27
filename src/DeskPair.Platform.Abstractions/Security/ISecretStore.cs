namespace DeskPair.Platform.Abstractions.Security;

public interface IMachineIdProvider
{
    /// <summary>Stable per-machine identifier (16..64 bytes); the rendezvous server keys peer IDs on it.</summary>
    byte[] GetStableMachineId();
}

/// <summary>Stores small secrets (identity key, password hashes) using the OS keystore where available.</summary>
public interface ISecretStore
{
    /// <summary>
    /// The stored value, or null when there is none.
    /// </summary>
    /// <exception cref="SecretUnreadableException">
    /// Something is stored under this key and this process cannot read it. Never null, and never silently:
    /// a caller told "there is none" will write a replacement, and on a shared store that destroys the
    /// original. It has happened -- a LocalSystem engine and a signed-in user sharing one directory, where
    /// three of four secrets were overwritten and a host lost the identity it had been reachable by.
    /// </exception>
    ValueTask<byte[]?> GetAsync(string key, CancellationToken ct = default);

    ValueTask SetAsync(string key, ReadOnlyMemory<byte> value, CancellationToken ct = default);

    ValueTask RemoveAsync(string key, CancellationToken ct = default);
}

/// <summary>
/// A secret exists under this key, and this process is not the one that can read it.
///
/// Distinct from absence on purpose. Absence means "make one"; this means "stop, and say whose it is",
/// because whatever is here belongs to somebody and writing over it is not recoverable.
/// </summary>
public sealed class SecretUnreadableException(string key, string? detail = null)
    : Exception($"The secret \"{key}\" is stored by another account or with another key{(detail is null ? "." : $": {detail}")}")
{
    public string Key { get; } = key;
}
